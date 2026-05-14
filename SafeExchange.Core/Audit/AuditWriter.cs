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

        private readonly SafeExchangeDbContext dbContext;

        private readonly ILogger<AuditWriter> log;

        public AuditWriter(SafeExchangeDbContext dbContext, ILogger<AuditWriter> log)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
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
                try
                {
                    var tail = await this.dbContext.SecretAuditEvents
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

                    this.dbContext.SecretAuditEvents.Add(entry);
                    await this.dbContext.SaveChangesAsync(ct);
                    return;
                }
                catch (DbUpdateException ex) when (attempt < MaxRetries)
                {
                    // Another writer claimed our sequence number. Detach and retry.
                    this.dbContext.ChangeTracker.Clear();
                    log.LogWarning(ex, $"AuditWriter conflict on attempt {attempt} for instance {secret.AuditInstanceId}; retrying.");
                }
                catch (Exception ex)
                {
                    log.LogError(ex, $"AuditWriteFailed: instance={secret.AuditInstanceId} eventType={eventType} actor={actorId}");
                    this.dbContext.ChangeTracker.Clear();
                    return;
                }
            }

            log.LogError($"AuditWriteFailed (exhausted retries): instance={secret.AuditInstanceId} eventType={eventType}");
        }

        public async ValueTask EnsureAnchorAsync(ObjectMetadata secret, string createdBy, CancellationToken ct = default)
        {
            if (secret is null || !secret.AuditEnabled || string.IsNullOrEmpty(secret.AuditInstanceId))
            {
                return;
            }

            try
            {
                var existing = await this.dbContext.SecretAuditAnchors
                    .FirstOrDefaultAsync(a => a.AuditInstanceId == secret.AuditInstanceId, ct);
                if (existing is not null)
                {
                    return;
                }
                this.dbContext.SecretAuditAnchors.Add(new SecretAuditAnchor(secret.AuditInstanceId, secret.ObjectName, createdBy ?? string.Empty));
                await this.dbContext.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                this.log.LogError(ex, $"AuditAnchorWriteFailed: instance={secret.AuditInstanceId}");
                this.dbContext.ChangeTracker.Clear();
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
                var anchor = await this.dbContext.SecretAuditAnchors
                    .FirstOrDefaultAsync(a => a.AuditInstanceId == secret.AuditInstanceId, ct);
                if (anchor is null)
                {
                    return;
                }
                var now = DateTimeProvider.UtcNow;
                anchor.DeletedAt = now;
                anchor.DeletedBy = deletedBy ?? string.Empty;
                anchor.RetentionExpiresAt = now.AddDays(retentionDays);
                await this.dbContext.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                this.log.LogError(ex, $"AuditAnchorStampFailed: instance={secret.AuditInstanceId}");
                this.dbContext.ChangeTracker.Clear();
            }
        }
    }
}
