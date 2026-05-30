namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Migrations;
    using System;

    [TestFixture]
    public class IssuedAtBackfillTests
    {
        [Test]
        public void Backfill_SetsIssuedAt_WhenMissing()
        {
            var json = "{\"id\":\"u1\",\"TelemetryId\":\"abc\",\"TelemetryIdExpiresAt\":\"2026-06-01T00:00:00Z\"}";
            var expiresAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var result = IssuedAtBackfill.BackfillIfMissing(json, expiresAt);
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Does.Contain("\"TelemetryIdIssuedAt\""));
            Assert.That(result, Does.Contain("2026-05-25T00:00:00")); // expiresAt - 7d
        }

        [Test]
        public void Backfill_ReturnsNull_WhenAlreadySet()
        {
            var json = "{\"id\":\"u1\",\"TelemetryId\":\"abc\",\"TelemetryIdIssuedAt\":\"2026-05-20T00:00:00Z\",\"TelemetryIdExpiresAt\":\"2026-06-01T00:00:00Z\"}";
            var result = IssuedAtBackfill.BackfillIfMissing(json, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.That(result, Is.Null);
        }

        [Test]
        public void Backfill_ReturnsNull_WhenTelemetryIdEmpty()
        {
            var json = "{\"id\":\"u1\",\"TelemetryId\":\"\",\"TelemetryIdExpiresAt\":\"2026-06-01T00:00:00Z\"}";
            var result = IssuedAtBackfill.BackfillIfMissing(json, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.That(result, Is.Null);
        }
    }
}
