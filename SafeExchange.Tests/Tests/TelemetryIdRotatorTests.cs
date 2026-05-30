namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Telemetry;
    using System;

    [TestFixture]
    public class TelemetryIdRotatorTests
    {
        [Test]
        public void NextWeekBoundary_IsNextMonday0000Utc()
        {
            // Wed 2026-05-27 10:00 UTC -> Mon 2026-06-01 00:00 UTC
            var now = new DateTime(2026, 5, 27, 10, 0, 0, DateTimeKind.Utc);
            var b = TelemetryIdRotator.NextWeekBoundaryUtc(now);
            Assert.That(b, Is.EqualTo(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(b.Kind, Is.EqualTo(DateTimeKind.Utc));
        }

        [Test]
        public void NextWeekBoundary_OnMonday_GoesToFollowingMonday()
        {
            var monday = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
            Assert.That(TelemetryIdRotator.NextWeekBoundaryUtc(monday),
                Is.EqualTo(new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc)));
        }

        [Test]
        public void EnsureCurrent_EmptyId_GeneratesAndSetsExpiry()
        {
            var rotator = new TelemetryIdRotator();
            var user = new User();
            var now = new DateTime(2026, 5, 27, 10, 0, 0, DateTimeKind.Utc);
            var changed = rotator.EnsureCurrent(user, now);
            Assert.That(changed, Is.True);
            Assert.That(user.TelemetryId, Is.Not.Empty);
            Assert.That(user.TelemetryIdExpiresAt, Is.EqualTo(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
        }

        [Test]
        public void EnsureCurrent_NotExpired_NoChange()
        {
            var rotator = new TelemetryIdRotator();
            var user = new User { TelemetryId = "abc", TelemetryIdExpiresAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) };
            var changed = rotator.EnsureCurrent(user, new DateTime(2026, 5, 31, 23, 0, 0, DateTimeKind.Utc));
            Assert.That(changed, Is.False);
            Assert.That(user.TelemetryId, Is.EqualTo("abc"));
        }

        [Test]
        public void EnsureCurrent_Expired_RotatesToNewId()
        {
            var rotator = new TelemetryIdRotator();
            var user = new User { TelemetryId = "abc", TelemetryIdExpiresAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) };
            var changed = rotator.EnsureCurrent(user, new DateTime(2026, 6, 1, 0, 0, 1, DateTimeKind.Utc));
            Assert.That(changed, Is.True);
            Assert.That(user.TelemetryId, Is.Not.EqualTo("abc"));
            Assert.That(user.TelemetryIdExpiresAt, Is.EqualTo(new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc)));
        }
    }
}
