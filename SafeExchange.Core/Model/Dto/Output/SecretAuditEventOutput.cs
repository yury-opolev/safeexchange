/// <summary>
/// SecretAuditEventOutput — DTO for a single audit event in API responses. Sequential
/// ContentRead events for the same (actor, contentId) are merged into a single output
/// item with a populated ChunkIds list; non-merged events use the scalar fields only.
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System;
    using System.Collections.Generic;

    public class SecretAuditEventOutput
    {
        public string EventType { get; set; } = string.Empty;

        public DateTime OccurredAt { get; set; }

        public DateTime? FirstAt { get; set; }

        public DateTime? LastAt { get; set; }

        public long SequenceNumber { get; set; }

        public long? SequenceFrom { get; set; }

        public long? SequenceTo { get; set; }

        public ActorOutput Actor { get; set; } = new();

        public string? ContentId { get; set; }

        public List<string>? ChunkIds { get; set; }

        public object? Payload { get; set; }

        public string Hash { get; set; } = string.Empty;

        public string PrevHash { get; set; } = string.Empty;
    }

    public class ActorOutput
    {
        public string SubjectType { get; set; } = string.Empty;

        public string SubjectId { get; set; } = string.Empty;

        public string SubjectName { get; set; } = string.Empty;
    }
}
