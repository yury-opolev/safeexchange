/// <summary>
/// RecordingAuditWriter — captures every AppendAsync call so tests can assert
/// audit-event emission. Mirrors NoOpAuditWriter for non-Append surface area.
/// </summary>

namespace SafeExchange.Tests.Utilities
{
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Model;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class RecordingAuditWriter : IAuditWriter
    {
        public List<RecordedAuditEntry> Entries { get; } = new();

        public ValueTask AppendAsync(
            ObjectMetadata secret,
            SecretAuditEventType eventType,
            SubjectType actorType,
            string actorId,
            object? payload,
            ILogger log,
            CancellationToken ct = default)
        {
            this.Entries.Add(new RecordedAuditEntry(secret.ObjectName, eventType, actorType, actorId, payload));
            return ValueTask.CompletedTask;
        }

        public ValueTask EnsureAnchorAsync(ObjectMetadata secret, string createdBy, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask StampDeletionAsync(ObjectMetadata secret, string deletedBy, int retentionDays, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    public sealed record RecordedAuditEntry(
        string SecretId,
        SecretAuditEventType EventType,
        SubjectType ActorType,
        string ActorId,
        object? Payload);
}
