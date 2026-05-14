/// <summary>
/// IAuditWriter — appends hash-chained SecretAuditEvent rows for audit-enabled secrets.
/// All methods are no-ops when the secret has AuditEnabled = false. All methods
/// swallow their own exceptions and log AuditWriteFailed — they never throw.
/// </summary>

namespace SafeExchange.Core.Audit
{
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Model;
    using System.Threading;
    using System.Threading.Tasks;

    public interface IAuditWriter
    {
        ValueTask AppendAsync(
            ObjectMetadata secret,
            SecretAuditEventType eventType,
            SubjectType actorType,
            string actorId,
            string actorDisplayName,
            object? payload,
            ILogger log,
            CancellationToken ct = default);

        ValueTask EnsureAnchorAsync(
            ObjectMetadata secret,
            string createdBy,
            CancellationToken ct = default);

        ValueTask StampDeletionAsync(
            ObjectMetadata secret,
            string deletedBy,
            int retentionDays,
            CancellationToken ct = default);
    }
}
