/// <summary>
/// AuditWriter — appends hash-chained SecretAuditEvent rows. Retries on Cosmos
/// concurrency conflicts (DbUpdateException) up to MaxRetries times. On terminal
/// failure logs AuditWriteFailed; never throws to the caller.
/// </summary>

namespace SafeExchange.Core.Audit
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Model;
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class AuditWriter : IAuditWriter
    {
        private const int MaxRetries = 5;

        // Policy-defensible retention floor. KV typos producing 0/1 used to give a
        // ~24h post-delete window; 30 days is the new floor (OWASP review F3).
        private const int MinRetentionDays = 30;

        // Sentinel prefix used by automated purge actors. Never looked up; the literal
        // string is recorded as both id and display name.
        private const string SystemActorPrefix = "system:";

        private readonly IDbContextFactory<SafeExchangeDbContext> dbContextFactory;

        private readonly ILogger<AuditWriter> log;

        // Per-instance (per-request) cache of resolved display names. Reduces N+1
        // DB roundtrips when a single request emits multiple audit events for the
        // same actor (e.g., a bulk permission grant followed by content reads).
        private readonly Dictionary<string, string> displayNameCache = new(StringComparer.OrdinalIgnoreCase);

        public AuditWriter(IDbContextFactory<SafeExchangeDbContext> dbContextFactory, ILogger<AuditWriter> log)
        {
            this.dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public async ValueTask AppendAsync(
            ObjectMetadata secret,
            SecretAuditEventType eventType,
            SubjectType actorType,
            string actorId,
            object? payload,
            ILogger log,
            CancellationToken ct = default)
        {
            if (secret is null || !secret.AuditEnabled || string.IsNullOrEmpty(secret.AuditInstanceId))
            {
                return;
            }

            actorId ??= string.Empty;
            var actorDisplayName = await this.ResolveDisplayNameAsync(actorType, actorId, ct);

            // Serialize payload exactly once. The same string ends up in the canonical
            // hash and in the persisted row, so a verifier never has to re-serialize.
            var payloadJson = payload is null ? "{}" : DefaultJsonSerializer.Serialize(payload);
            var occurredAt = DateTimeProvider.UtcNow;

            for (var attempt = 1; attempt <= MaxRetries; attempt++)
            {
                // Use an isolated DbContext per attempt so a retry never clobbers the
                // request-scoped context's pending changes (issue C1 from review).
                try
                {
                    await using var auditDb = await this.dbContextFactory.CreateDbContextAsync(ct);

                    var tail = await auditDb.SecretAuditEvents
                        .Where(e => e.AuditInstanceId == secret.AuditInstanceId)
                        .OrderByDescending(e => e.SequenceNumber)
                        .FirstOrDefaultAsync(ct);

                    var sequence = (tail?.SequenceNumber ?? 0) + 1;
                    var prevHash = tail?.Hash ?? string.Empty;
                    var hash = AuditEventHasher.ComputeHash(
                        secret.AuditInstanceId, sequence, eventType, occurredAt,
                        actorType, actorId, actorDisplayName,
                        payloadJson, prevHash);

                    var entry = new SecretAuditEvent
                    {
                        id = SecretAuditEvent.MakeId(secret.AuditInstanceId, sequence),
                        AuditInstanceId = secret.AuditInstanceId,
                        SequenceNumber = sequence,
                        EventType = eventType,
                        OccurredAt = occurredAt,
                        ActorSubjectType = actorType,
                        ActorSubjectId = actorId,
                        ActorDisplayName = actorDisplayName,
                        Payload = payloadJson,
                        PrevHash = prevHash,
                        Hash = hash,
                    };

                    auditDb.SecretAuditEvents.Add(entry);
                    await auditDb.SaveChangesAsync(ct);
                    return;
                }
                catch (DbUpdateException ex) when (attempt < MaxRetries)
                {
                    log.LogWarning(ex, "AuditWriter conflict on attempt {Attempt} for instance {InstanceId}; retrying.", attempt, secret.AuditInstanceId);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "AuditWriteFailed: instance={InstanceId} eventType={EventType}", secret.AuditInstanceId, eventType);
                    return;
                }
            }

            log.LogError("AuditWriteFailed (exhausted retries): instance={InstanceId} eventType={EventType}", secret.AuditInstanceId, eventType);
        }

        // Resolve a verified human display name. For Applications the actorId itself
        // already comes from GetApplicationDisplayNameAsync (which reads the stored
        // Application record), so it's safe. For Users the actorId is the UPN, which
        // for federated/guest tenants can contain caller-controlled segments — look
        // up User.DisplayName instead. system: actors are pass-through.
        private async ValueTask<string> ResolveDisplayNameAsync(SubjectType type, string actorId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(actorId))
            {
                return string.Empty;
            }
            if (actorId.StartsWith(SystemActorPrefix, StringComparison.Ordinal))
            {
                return actorId;
            }
            if (type == SubjectType.Application)
            {
                return actorId;
            }
            if (this.displayNameCache.TryGetValue(actorId, out var cached))
            {
                return cached;
            }
            try
            {
                await using var db = await this.dbContextFactory.CreateDbContextAsync(ct);
                var user = await db.Users
                    .AsNoTracking()
                    .FirstOrDefaultAsync(u => u.AadUpn == actorId, ct);
                var resolved = user?.DisplayName ?? actorId;
                this.displayNameCache[actorId] = resolved;
                return resolved;
            }
            catch (Exception ex)
            {
                this.log.LogWarning(ex, "AuditWriter display-name lookup failed for {ActorId}; falling back to id.", actorId);
                return actorId;
            }
        }

        public async ValueTask EnsureAnchorAsync(ObjectMetadata secret, string createdBy, CancellationToken ct = default)
        {
            if (secret is null || !secret.AuditEnabled || string.IsNullOrEmpty(secret.AuditInstanceId))
            {
                return;
            }

            try
            {
                await using var auditDb = await this.dbContextFactory.CreateDbContextAsync(ct);
                var existing = await auditDb.SecretAuditAnchors
                    .FirstOrDefaultAsync(a => a.AuditInstanceId == secret.AuditInstanceId, ct);
                if (existing is not null)
                {
                    return;
                }
                auditDb.SecretAuditAnchors.Add(new SecretAuditAnchor(secret.AuditInstanceId, secret.ObjectName, createdBy ?? string.Empty));
                await auditDb.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                this.log.LogError(ex, "AuditAnchorWriteFailed: instance={InstanceId}", secret.AuditInstanceId);
            }
        }

        public async ValueTask StampDeletionAsync(ObjectMetadata secret, string deletedBy, int retentionDays, CancellationToken ct = default)
        {
            if (secret is null || !secret.AuditEnabled || string.IsNullOrEmpty(secret.AuditInstanceId))
            {
                return;
            }
            try
            {
                await using var auditDb = await this.dbContextFactory.CreateDbContextAsync(ct);
                var anchor = await auditDb.SecretAuditAnchors
                    .FirstOrDefaultAsync(a => a.AuditInstanceId == secret.AuditInstanceId, ct);
                if (anchor is null)
                {
                    return;
                }
                var now = DateTimeProvider.UtcNow;
                anchor.DeletedAt = now;
                anchor.DeletedBy = deletedBy ?? string.Empty;
                anchor.RetentionExpiresAt = now.AddDays(Math.Max(MinRetentionDays, retentionDays));
                await auditDb.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                this.log.LogError(ex, "AuditAnchorStampFailed: instance={InstanceId}", secret.AuditInstanceId);
            }
        }
    }
}
