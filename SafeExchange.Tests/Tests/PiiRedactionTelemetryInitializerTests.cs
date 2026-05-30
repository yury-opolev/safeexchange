namespace SafeExchange.Tests
{
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.Extensions.Options;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Telemetry;

    [TestFixture]
    public class PiiRedactionTelemetryInitializerTests
    {
        private static PiiRedactionTelemetryInitializer Make(bool enabled)
        {
            var features = new Features { RedactTelemetryPii = enabled };
            var monitor = Mock.Of<IOptionsMonitor<Features>>(m => m.CurrentValue == features);
            return new PiiRedactionTelemetryInitializer(monitor);
        }

        [Test]
        public void Enabled_RedactsEmail()
        {
            var t = new TraceTelemetry("Principal alice@contoso.com is authenticated");
            Make(true).Initialize(t);
            Assert.That(t.Message, Does.Not.Contain("alice@contoso.com"));
            Assert.That(t.Message, Does.Contain("[redacted]"));
        }

        [Test]
        public void Enabled_LeavesCleanTextAndGuids()
        {
            var t = new TraceTelemetry("secret BLOB-20260529 id 8f3a2b1c-0000-0000-0000-000000000000 read");
            var original = t.Message;
            Make(true).Initialize(t);
            Assert.That(t.Message, Is.EqualTo(original)); // no '@', no redaction
        }

        [Test]
        public void Disabled_PassesThrough()
        {
            var t = new TraceTelemetry("Principal alice@contoso.com is authenticated");
            var original = t.Message;
            Make(false).Initialize(t);
            Assert.That(t.Message, Is.EqualTo(original));
        }
    }
}
