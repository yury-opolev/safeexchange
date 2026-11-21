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

        // Documents written before origin attribution have no Channel/ClientLabel
        // properties. EF Cosmos materialises a missing property as null into the
        // backing field, so the getters normalise null to empty.
        private string? channel = string.Empty;

        private string? clientLabel = string.Empty;

        /// <summary>
        /// "mcp" for events produced through the MCP endpoint; empty for REST, system and older events.
        /// </summary>
        public string Channel
        {
            get => this.channel ?? string.Empty;
            set => this.channel = value;
        }

        /// <summary>
        /// The MCP client's self-reported label (sanitised, at most 128 characters); carries no authority.
        /// </summary>
        public string ClientLabel
        {
            get => this.clientLabel ?? string.Empty;
            set => this.clientLabel = value;
        }

        public static string MakeId(string auditInstanceId, long sequenceNumber)
            => $"{auditInstanceId}|{sequenceNumber.ToString("D12", CultureInfo.InvariantCulture)}";
    }
}
