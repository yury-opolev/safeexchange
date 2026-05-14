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

        // Lower bound on retention to defend against KV typos turning a missing/zero
        // setting into immediate purge of every audited secret's events on delete.
        private const int MinRetentionDays = 1;

        private readonly IDbContextFactory<SafeExchangeDbContext> dbContextFactory;

        private readonly ILogger<AuditWriter> log;

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
            string actorDisplayName,
            object? payload,
            ILogger log,
            CancellationToken ct = default)
        {
            if (secret is null || !secret.AuditEnabled || string.IsNullOrEmpty(secret.AuditInstanceId))
            {
                return;
            }

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
                        actorType, actorId ?? string.Empty, actorDisplayName ?? string.Empty,
                        payloadJson, prevHash);

                    var entry = new SecretAuditEvent
                    {
                        id = SecretAuditEvent.MakeId(secret.AuditInstanceId, sequence),
                        AuditInstanceId = secret.AuditInstanceId,
                        SequenceNumber = sequence,
                        EventType = eventType,
                        OccurredAt = occurredAt,
                        ActorSubjectType = actorType,
                        ActorSubjectId = actorId ?? string.Empty,
                        ActorDisplayName = actorDisplayName ?? string.Empty,
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
                    log.LogError(ex, "AuditWriteFailed: instance={InstanceId} eventType={EventType} actor={ActorId}", secret.AuditInstanceId, eventType, actorId);
                    return;
                }
            }

            log.LogError("AuditWriteFailed (exhausted retries): instance={InstanceId} eventType={EventType}", secret.AuditInstanceId, eventType);
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
