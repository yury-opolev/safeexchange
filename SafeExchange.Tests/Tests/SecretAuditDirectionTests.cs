/// <summary>
/// SecretAuditDirectionTests — pure-unit checks for the "reverse around merger"
/// strategy used by SafeExchangeSecretAudit when direction=desc. Ensures merged
/// ContentRead ranges are equivalent (same seqFrom/seqTo/chunkIds) regardless
/// of whether the merger received the page ascending or reversed.
/// </summary>

namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using System.Collections.Generic;
    using System.Linq;

    [TestFixture]
    public class SecretAuditDirectionTests
    {
        private static SecretAuditEvent Read(long seq, string actor, string content, string chunk)
        {
            var payload = DefaultJsonSerializer.Serialize(new { contentId = content, chunkId = chunk });
            return new SecretAuditEvent
            {
                id = SecretAuditEvent.MakeId("inst", seq),
                AuditInstanceId = "inst",
                SequenceNumber = seq,
                EventType = SecretAuditEventType.ContentRead,
                OccurredAt = new System.DateTime(2026, 1, 1, 0, 0, (int)seq, System.DateTimeKind.Utc),
                ActorSubjectType = SubjectType.User,
                ActorSubjectId = actor,
                ActorDisplayName = actor,
                Payload = payload,
                PrevHash = string.Empty,
                Hash = "h" + seq,
            };
        }

        // Mirrors the in-handler reverse-merge-reverse path. Kept in tests because the
        // production code does the same inline; this test pins the resulting shape.
        private static List<SecretAuditEventOutput> ReverseMergeReverse(IList<SecretAuditEvent> desc)
        {
            var asc = new List<SecretAuditEvent>(desc);
            asc.Reverse();
            var merged = ContentReadMerger.Merge(asc);
            merged.Reverse();
            return merged;
        }

        [Test]
        public void DescPath_MergesContentRunsLikeAsc_OnlyReversedInOutput()
        {
            var asc = new List<SecretAuditEvent>
            {
                Read(1, "a@x", "c1", "k1"),
                Read(2, "a@x", "c1", "k2"),
                Read(3, "a@x", "c1", "k3"),
            };
            var ascMerged = ContentReadMerger.Merge(asc);

            var desc = new List<SecretAuditEvent>(asc);
            desc.Reverse();
            var descMerged = ReverseMergeReverse(desc);

            Assert.That(descMerged.Count, Is.EqualTo(ascMerged.Count));
            Assert.That(descMerged[0].SequenceFrom, Is.EqualTo(ascMerged[0].SequenceFrom));
            Assert.That(descMerged[0].SequenceTo, Is.EqualTo(ascMerged[0].SequenceTo));
            Assert.That(descMerged[0].ChunkIds, Is.EquivalentTo(ascMerged[0].ChunkIds!));
        }

        [Test]
        public void DescPath_PreservesNewestFirstAcrossMixedTypes()
        {
            var asc = new List<SecretAuditEvent>
            {
                Read(10, "a@x", "c1", "k1"),
                Read(11, "a@x", "c1", "k2"),
                Read(12, "b@x", "c1", "k1"),
            };
            var desc = new List<SecretAuditEvent>(asc);
            desc.Reverse();
            var descMerged = ReverseMergeReverse(desc);

            // The first output row should be the newest one (the b@x single read).
            Assert.That(descMerged.First().Actor.SubjectId, Is.EqualTo("b@x"));
            Assert.That(descMerged.First().SequenceFrom, Is.EqualTo(12));

            // The second row should be the merged a@x run (seq 10..11).
            Assert.That(descMerged.Last().Actor.SubjectId, Is.EqualTo("a@x"));
            Assert.That(descMerged.Last().SequenceFrom, Is.EqualTo(10));
            Assert.That(descMerged.Last().SequenceTo, Is.EqualTo(11));
        }
    }
}
