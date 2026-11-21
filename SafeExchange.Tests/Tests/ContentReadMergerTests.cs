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
    using System.Linq;

    [TestFixture]
    public class ContentReadMergerTests
    {
        private static SecretAuditEvent MakeRead(long seq, string actorId, string contentId, string chunkId, string channel = "", string clientLabel = "")
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
                Channel = channel,
                ClientLabel = clientLabel,
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
        public void Merge_McpAndRestReadsOfTheSameContent_AreNotMerged()
        {
            var events = new List<SecretAuditEvent>
            {
                MakeRead(1, "a@x", "c1", "k1"),
                MakeRead(2, "a@x", "c1", "k2", "mcp", "agent/1"),
            };
            var merged = ContentReadMerger.Merge(events);
            Assert.That(merged.Count, Is.EqualTo(2));
            Assert.That(merged.Select(m => m.Channel), Is.EqualTo(new[] { null, "mcp" }));
        }

        [Test]
        public void Merge_DifferentClientLabels_AreNotMerged()
        {
            var events = new List<SecretAuditEvent>
            {
                MakeRead(1, "a@x", "c1", "k1", "mcp", "agent/1"),
                MakeRead(2, "a@x", "c1", "k2", "mcp", "agent/2"),
            };
            Assert.That(ContentReadMerger.Merge(events).Count, Is.EqualTo(2));
        }

        [Test]
        public void Merge_SameOrigin_MergesAndKeepsTheOrigin()
        {
            var events = new List<SecretAuditEvent>
            {
                MakeRead(1, "a@x", "c1", "k1", "mcp", "agent/1"),
                MakeRead(2, "a@x", "c1", "k2", "mcp", "agent/1"),
            };
            var merged = ContentReadMerger.Merge(events);
            Assert.That(merged.Count, Is.EqualTo(1));
            Assert.That(merged[0].ChunkIds, Is.EqualTo(new[] { "k1", "k2" }));
            Assert.That(merged[0].Channel, Is.EqualTo("mcp"));
            Assert.That(merged[0].ClientLabel, Is.EqualTo("agent/1"));
        }

        [Test]
        public void Raw_ExposesTheOriginOnlyWhenSet()
        {
            var events = new List<SecretAuditEvent>
            {
                MakeOther(1, SecretAuditEventType.SecretCreated),
                MakeRead(2, "a@x", "c1", "k1", "mcp", "agent/1"),
            };
            var raw = ContentReadMerger.Raw(events);
            Assert.That(raw[0].Channel, Is.Null);
            Assert.That(raw[0].ClientLabel, Is.Null);
            Assert.That(raw[1].Channel, Is.EqualTo("mcp"));
            Assert.That(raw[1].ClientLabel, Is.EqualTo("agent/1"));
        }

        [Test]
        public void Json_OmitsTheOriginWhenEmpty_AndWritesItWhenSet()
        {
            var events = new List<SecretAuditEvent>
            {
                MakeOther(1, SecretAuditEventType.SecretCreated),
                MakeRead(2, "a@x", "c1", "k1", "mcp", "agent/1"),
            };
            var raw = ContentReadMerger.Raw(events);
            var restJson = DefaultJsonSerializer.Serialize(raw[0]);
            var mcpJson = DefaultJsonSerializer.Serialize(raw[1]);
            Assert.That(restJson, Does.Not.Contain("channel").And.Not.Contain("clientLabel"));
            Assert.That(mcpJson, Does.EndWith(",\"channel\":\"mcp\",\"clientLabel\":\"agent/1\"}"));
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
