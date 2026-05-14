/// <summary>
/// NoOpAuditWriter — test double that records nothing. Used by handler integration
/// tests that don't exercise audit behaviour. Audit-specific behaviour is covered
/// by AuditWriterTests and the pure-unit hasher/diff/merger tests.
/// </summary>

namespace SafeExchange.Tests.Utilities
{
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Model;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class NoOpAuditWriter : IAuditWriter
    {
        public ValueTask AppendAsync(
            ObjectMetadata secret,
            SecretAuditEventType eventType,
            SubjectType actorType,
            string actorId,
            string actorDisplayName,
            object? payload,
            ILogger log,
            CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask EnsureAnchorAsync(ObjectMetadata secret, string createdBy, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask StampDeletionAsync(ObjectMetadata secret, string deletedBy, int retentionDays, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }
}
