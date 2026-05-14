/// <summary>
/// SecretAuditEvent — one row per recorded action. Append-only; hash-chained.
/// </summary>

namespace SafeExchange.Core.Model
{
    using System;
    using System.Globalization;

    public class SecretAuditEvent
    {
        public SecretAuditEvent() { }

        public string id { get; set; } = string.Empty;

        public string AuditInstanceId { get; set; } = string.Empty;

        public long SequenceNumber { get; set; }

        public SecretAuditEventType EventType { get; set; }

        public DateTime OccurredAt { get; set; }

        public SubjectType ActorSubjectType { get; set; }

        public string ActorSubjectId { get; set; } = string.Empty;

        public string ActorDisplayName { get; set; } = string.Empty;

        public string Payload { get; set; } = string.Empty;

        public string PrevHash { get; set; } = string.Empty;

        public string Hash { get; set; } = string.Empty;

        public static string MakeId(string auditInstanceId, long sequenceNumber)
            => $"{auditInstanceId}|{sequenceNumber.ToString("D12", CultureInfo.InvariantCulture)}";
    }
}
