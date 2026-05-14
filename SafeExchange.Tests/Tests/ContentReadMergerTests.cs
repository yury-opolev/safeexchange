/// <summary>
/// ContentReadMergerTests
/// </summary>

namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Model;
    using System;
    using System.Collections.Generic;

    [TestFixture]
    public class ContentReadMergerTests
    {
        private static SecretAuditEvent MakeRead(long seq, string actorId, string contentId, string chunkId)
        {
            var payload = DefaultJsonSerializer.Serialize(new { contentId, chunkId });
            return new SecretAuditEvent
            {
                id = SecretAuditEvent.MakeId("inst", seq),
                AuditInstanceId = "inst",
                SequenceNumber = seq,
                EventType = SecretAuditEventType.ContentRead,
                OccurredAt = new DateTime(2026, 1, 1, 0, 0, (int)seq, DateTimeKind.Utc),
                ActorSubjectType = SubjectType.User,
                ActorSubjectId = actorId,
                ActorDisplayName = actorId,
                Payload = payload,
                PrevHash = string.Empty,
                Hash = "h" + seq,
            };
        }

        private static SecretAuditEvent MakeOther(long seq, SecretAuditEventType type)
            => new SecretAuditEvent
            {
                id = SecretAuditEvent.MakeId("inst", seq),
                AuditInstanceId = "inst",
                SequenceNumber = seq,
                EventType = type,
                OccurredAt = new DateTime(2026, 1, 1, 0, 0, (int)seq, DateTimeKind.Utc),
                ActorSubjectType = SubjectType.User,
                ActorSubjectId = "a@x",
                ActorDisplayName = "a@x",
                Payload = "{}",
                PrevHash = string.Empty,
                Hash = "h" + seq,
            };

        [Test]
        public void Merge_Empty_ReturnsEmpty()
        {
            Assert.That(ContentReadMerger.Merge(new List<SecretAuditEvent>()), Is.Empty);
        }

        [Test]
        public void Merge_SingleReadProducesSingleItemWithOneChunkId()
        {
            var events = new List<SecretAuditEvent> { MakeRead(1, "a@x", "c1", "k1") };
            var merged = ContentReadMerger.Merge(events);
            Assert.That(merged.Count, Is.EqualTo(1));
            Assert.That(merged[0].ChunkIds, Is.EquivalentTo(new[] { "k1" }));
            Assert.That(merged[0].ContentId, Is.EqualTo("c1"));
            Assert.That(merged[0].SequenceFrom, Is.EqualTo(1));
            Assert.That(merged[0].SequenceTo, Is.EqualTo(1));
        }

        [Test]
        public void Merge_ThreeSequentialSameActorSameContent_MergesToOne()
        {
            var events = new List<SecretAuditEvent>
            {
                MakeRead(1, "a@x", "c1", "k1"),
                MakeRead(2, "a@x", "c1", "k2"),
                MakeRead(3, "a@x", "c1", "k3"),
            };
            var merged = ContentReadMerger.Merge(events);
            Assert.That(merged.Count, Is.EqualTo(1));
            Assert.That(merged[0].ChunkIds, Is.EquivalentTo(new[] { "k1", "k2", "k3" }));
            Assert.That(merged[0].SequenceFrom, Is.EqualTo(1));
            Assert.That(merged[0].SequenceTo, Is.EqualTo(3));
        }

        [Test]
        public void Merge_DifferentActorBreaksGroup()
        {
            var events = new List<SecretAuditEvent>
            {
                MakeRead(1, "a@x", "c1", "k1"),
                MakeRead(2, "b@x", "c1", "k2"),
            };
            Assert.That(ContentReadMerger.Merge(events).Count, Is.EqualTo(2));
        }

        [Test]
        public void Merge_DifferentContentIdBreaksGroup()
        {
            var events = new List<SecretAuditEvent>
            {
                MakeRead(1, "a@x", "c1", "k1"),
                MakeRead(2, "a@x", "c2", "k1"),
            };
            Assert.That(ContentReadMerger.Merge(events).Count, Is.EqualTo(2));
        }

        [Test]
        public void Merge_OtherEventInterleaved_BreaksGroup()
        {
            var events = new List<SecretAuditEvent>
            {
                MakeRead(1, "a@x", "c1", "k1"),
                MakeOther(2, SecretAuditEventType.PermissionGranted),
                MakeRead(3, "a@x", "c1", "k2"),
            };
            var merged = ContentReadMerger.Merge(events);
            Assert.That(merged.Count, Is.EqualTo(3));
            Assert.That(merged[0].EventType, Is.EqualTo(SecretAuditEventType.ContentRead.ToString()));
            Assert.That(merged[1].EventType, Is.EqualTo(SecretAuditEventType.PermissionGranted.ToString()));
            Assert.That(merged[2].EventType, Is.EqualTo(SecretAuditEventType.ContentRead.ToString()));
        }

        [Test]
        public void Raw_DoesNotMerge_SequentialReads()
        {
            var events = new List<SecretAuditEvent>
            {
                MakeRead(1, "a@x", "c1", "k1"),
                MakeRead(2, "a@x", "c1", "k2"),
            };
            var raw = ContentReadMerger.Raw(events);
            Assert.That(raw.Count, Is.EqualTo(2));
        }

        [Test]
        public void Merge_NonReadEventOnly_PassesThrough()
        {
            var events = new List<SecretAuditEvent>
            {
                MakeOther(1, SecretAuditEventType.SecretCreated),
            };
            var merged = ContentReadMerger.Merge(events);
            Assert.That(merged.Count, Is.EqualTo(1));
            Assert.That(merged[0].EventType, Is.EqualTo(SecretAuditEventType.SecretCreated.ToString()));
            Assert.That(merged[0].ChunkIds, Is.Null);
        }
    }
}
