namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Configuration;

    [TestFixture]
    public class FeaturesTests
    {
        [Test]
        public void RedactTelemetryPii_DefaultsFalse()
        {
            var features = new Features();
            Assert.That(features.RedactTelemetryPii, Is.False);
        }

        [Test]
        public void Clone_CopiesRedactTelemetryPii()
        {
            var features = new Features { RedactTelemetryPii = true };
            var clone = features.Clone();
            Assert.That(clone.RedactTelemetryPii, Is.True);
        }
    }
}
