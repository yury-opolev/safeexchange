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

        [Test]
        public void McpEndpoint_DefaultsFalse()
        {
            Assert.That(new Features().McpEndpoint, Is.False);
        }

        [Test]
        public void McpEndpoint_IsClonedAndCompared()
        {
            var features = new Features { McpEndpoint = true };

            var clone = features.Clone();

            Assert.That(clone.McpEndpoint, Is.True);
            Assert.That(clone, Is.EqualTo(features));
            Assert.That(clone.GetHashCode(), Is.EqualTo(features.GetHashCode()));
            Assert.That(new Features(), Is.Not.EqualTo(features));
        }
    }
}
