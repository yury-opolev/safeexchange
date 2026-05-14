/// <summary>
/// ContentReadMerger — collapses runs of ContentRead events for the same (actor, contentId)
/// into single output DTO items. Pure function; never mutates the input list and never reads
/// the secret's actual content (only the contentId/chunkId from the stored payload JSON).
/// </summary>

namespace SafeExchange.Core.Audit
{
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;

    public static class ContentReadMerger
    {
        public static List<SecretAuditEventOutput> Merge(IReadOnlyList<SecretAuditEvent> events)
        {
            var result = new List<SecretAuditEventOutput>();
            var i = 0;
            while (i < events.Count)
            {
                var e = events[i];
                if (e.EventType != SecretAuditEventType.ContentRead)
                {
                    result.Add(ToDto(e));
                    i++;
                    continue;
                }

                var (contentId, chunkId) = ParseRead(e.Payload);
                var chunkIds = new List<string> { chunkId };
                var firstAt = e.OccurredAt;
                var lastAt = e.OccurredAt;
                var seqFrom = e.SequenceNumber;
                var seqTo = e.SequenceNumber;
                var hashEnd = e.Hash;
                var prevHashStart = e.PrevHash;

                var j = i + 1;
                while (j < events.Count
                    && events[j].EventType == SecretAuditEventType.ContentRead
                    && events[j].ActorSubjectId == e.ActorSubjectId)
                {
                    var (cId, kId) = ParseRead(events[j].Payload);
                    if (cId != contentId)
                    {
                        break;
                    }
                    chunkIds.Add(kId);
                    lastAt = events[j].OccurredAt;
                    seqTo = events[j].SequenceNumber;
                    hashEnd = events[j].Hash;
                    j++;
                }

                result.Add(new SecretAuditEventOutput
                {
                    EventType = SecretAuditEventType.ContentRead.ToString(),
                    OccurredAt = firstAt,
                    FirstAt = firstAt,
                    LastAt = lastAt,
                    SequenceNumber = seqFrom,
                    SequenceFrom = seqFrom,
                    SequenceTo = seqTo,
                    Actor = new ActorOutput
                    {
                        SubjectType = e.ActorSubjectType.ToString(),
                        SubjectId = e.ActorSubjectId,
                        SubjectName = e.ActorDisplayName,
                    },
                    ContentId = contentId,
                    ChunkIds = chunkIds,
                    Hash = hashEnd,
                    PrevHash = prevHashStart,
                });
                i = j;
            }
            return result;
        }

        public static List<SecretAuditEventOutput> Raw(IReadOnlyList<SecretAuditEvent> events)
            => events.Select(ToDto).ToList();

        private static SecretAuditEventOutput ToDto(SecretAuditEvent e)
        {
            object? payload = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrEmpty(e.Payload) ? "{}" : e.Payload);
                payload = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // leave payload null; the raw string is reachable via the Hash/PrevHash
                // pair if a verifier needs to reconstruct what was actually stored.
            }
            return new SecretAuditEventOutput
            {
                EventType = e.EventType.ToString(),
                OccurredAt = e.OccurredAt,
                SequenceNumber = e.SequenceNumber,
                Actor = new ActorOutput
                {
                    SubjectType = e.ActorSubjectType.ToString(),
                    SubjectId = e.ActorSubjectId,
                    SubjectName = e.ActorDisplayName,
                },
                Payload = payload,
                Hash = e.Hash,
                PrevHash = e.PrevHash,
            };
        }

        private static (string contentId, string chunkId) ParseRead(string payload)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrEmpty(payload) ? "{}" : payload);
                var contentId = doc.RootElement.TryGetProperty("contentId", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                var chunkId = doc.RootElement.TryGetProperty("chunkId", out var k) ? k.GetString() ?? string.Empty : string.Empty;
                return (contentId, chunkId);
            }
            catch (JsonException)
            {
                return (string.Empty, string.Empty);
            }
        }
    }
}
