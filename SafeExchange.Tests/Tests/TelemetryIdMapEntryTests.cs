namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Model;
    using System;

    [TestFixture]
    public class TelemetryIdMapEntryTests
    {
        [Test]
        public void Properties_RoundTrip()
        {
            var from = new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc);
            var to = new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc);
            var entry = new TelemetryIdMapEntry
            {
                id = "0123456789abcdef0123456789abcdef",
                UserId = "user-1",
                ValidFromUtc = from,
                ValidToUtc = to,
            };

            Assert.That(entry.id, Is.EqualTo("0123456789abcdef0123456789abcdef"));
            Assert.That(entry.UserId, Is.EqualTo("user-1"));
            Assert.That(entry.ValidFromUtc, Is.EqualTo(from));
            Assert.That(entry.ValidToUtc, Is.EqualTo(to));
        }
    }
}
