/// <summary>
/// AuditEventHasherTests
/// </summary>

namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Model;
    using System;

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
    }
}
