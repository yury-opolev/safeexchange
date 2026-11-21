/// <summary>
/// AuditEventHasherTests
/// </summary>

namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Model;
    using System;
    using System.Security.Cryptography;
    using System.Text;

    [TestFixture]
    public class AuditEventHasherTests
    {
        private static readonly DateTime FixedOccurredAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        [Test]
        public void ComputeHash_Deterministic_SameInputsProduceSameOutput()
        {
            var h1 = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "");
            var h2 = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "");
            Assert.That(h2, Is.EqualTo(h1));
            Assert.That(h1, Is.Not.Empty);
            Assert.That(h1.Length, Is.EqualTo(44));
        }

        [Test]
        public void ComputeHash_ChangingInstanceId_ChangesOutput()
        {
            var baseline = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "");
            var other = AuditEventHasher.ComputeHash("inst-2", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "");
            Assert.That(other, Is.Not.EqualTo(baseline));
        }

        [Test]
        public void ComputeHash_ChangingSequenceNumber_ChangesOutput()
        {
            var baseline = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "");
            var other = AuditEventHasher.ComputeHash("inst-1", 2, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "");
            Assert.That(other, Is.Not.EqualTo(baseline));
        }

        [Test]
        public void ComputeHash_ChangingEventType_ChangesOutput()
        {
            var baseline = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "");
            var other = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretDeleted, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "");
            Assert.That(other, Is.Not.EqualTo(baseline));
        }

        [Test]
        public void ComputeHash_ChangingOccurredAt_ChangesOutput()
        {
            var baseline = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "");
            var other = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt.AddTicks(1), SubjectType.User, "u@x", "U", "{}", "");
            Assert.That(other, Is.Not.EqualTo(baseline));
        }

        [Test]
        public void ComputeHash_ChangingActorSubjectType_ChangesOutput()
        {
            var baseline = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "");
            var other = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.Application, "u@x", "U", "{}", "");
            Assert.That(other, Is.Not.EqualTo(baseline));
        }

        [Test]
        public void ComputeHash_ChangingActorSubjectId_ChangesOutput()
        {
            var baseline = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "");
            var other = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "v@x", "U", "{}", "");
            Assert.That(other, Is.Not.EqualTo(baseline));
        }

        [Test]
        public void ComputeHash_ChangingActorDisplayName_ChangesOutput()
        {
            var baseline = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "");
            var other = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "V", "{}", "");
            Assert.That(other, Is.Not.EqualTo(baseline));
        }

        [Test]
        public void ComputeHash_ChangingPayload_ChangesOutput()
        {
            var baseline = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "");
            var other = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{\"a\":1}", "");
            Assert.That(other, Is.Not.EqualTo(baseline));
        }

        [Test]
        public void ComputeHash_ChangingPrevHash_ChangesOutput()
        {
            var baseline = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "");
            var other = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "prev");
            Assert.That(other, Is.Not.EqualTo(baseline));
        }

        [Test]
        public void ComputeHash_OutputIsBase64WithCorrectLength()
        {
            var hash = AuditEventHasher.ComputeHash(
                "inst-fixed", 1, SecretAuditEventType.SecretCreated,
                new DateTime(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc),
                SubjectType.User, "alice@x", "Alice", "{}", "");
            Assert.That(hash.Length, Is.EqualTo(44));
            // round-trip the base64 to confirm validity
            var bytes = Convert.FromBase64String(hash);
            Assert.That(bytes.Length, Is.EqualTo(32));
        }

        [Test]
        public void ComputeHash_WithoutOrigin_HashesTheOriginalCanonicalString()
        {
            var expected = Sha256Base64("inst-1|1|SecretCreated|2026-01-02T03:04:05.0000000Z|User|u@x|U|{}|prev");

            var legacyCall = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "prev");
            var emptyOrigin = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "prev", string.Empty, string.Empty);
            var nullOrigin = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "prev", null, null);

            Assert.That(legacyCall, Is.EqualTo(expected));
            Assert.That(emptyOrigin, Is.EqualTo(expected));
            Assert.That(nullOrigin, Is.EqualTo(expected));
        }

        [Test]
        public void ComputeHash_WithOrigin_AppendsTheVersionMarkerChannelAndLabel()
        {
            var expected = Sha256Base64("inst-1|1|SecretCreated|2026-01-02T03:04:05.0000000Z|User|u@x|U|{}|prev|v2|mcp|copilot-cli/1.0.114");

            var hash = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "prev", "mcp", "copilot-cli/1.0.114");

            Assert.That(hash, Is.EqualTo(expected));
        }

        [Test]
        public void ComputeHash_WithOnlyAChannelOrOnlyALabel_StillAppendsTheMarker()
        {
            var channelOnly = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "prev", "mcp", string.Empty);
            var labelOnly = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "prev", null, "agent/1");

            Assert.That(channelOnly, Is.EqualTo(Sha256Base64("inst-1|1|SecretCreated|2026-01-02T03:04:05.0000000Z|User|u@x|U|{}|prev|v2|mcp|")));
            Assert.That(labelOnly, Is.EqualTo(Sha256Base64("inst-1|1|SecretCreated|2026-01-02T03:04:05.0000000Z|User|u@x|U|{}|prev|v2||agent/1")));
        }

        [Test]
        public void ComputeHash_ChangingChannelOrLabel_ChangesOutput()
        {
            var baseline = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "prev", "mcp", "agent/1");
            var otherLabel = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "prev", "mcp", "agent/2");
            var noLabel = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "prev", "mcp", string.Empty);
            var noOrigin = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "prev");

            Assert.That(otherLabel, Is.Not.EqualTo(baseline));
            Assert.That(noLabel, Is.Not.EqualTo(baseline));
            Assert.That(noLabel, Is.Not.EqualTo(noOrigin));
            Assert.That(baseline, Is.Not.EqualTo(noOrigin));
        }

        private static string Sha256Base64(string canonical)
            => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
