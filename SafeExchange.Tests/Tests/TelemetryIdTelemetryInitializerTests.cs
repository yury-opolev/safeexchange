namespace SafeExchange.Tests
{
    using Microsoft.ApplicationInsights.DataContracts;
    using NUnit.Framework;
    using SafeExchange.Core.Telemetry;
    using SaexTelemetryContext = SafeExchange.Core.Telemetry.TelemetryContext;

    [TestFixture]
    public class TelemetryIdTelemetryInitializerTests
    {
        [TearDown] public void Clear() => SaexTelemetryContext.Current = null;

        [Test]
        public void Stamps_TelemetryId_WhenSet()
        {
            SaexTelemetryContext.Current = "tid-123";
            var t = new TraceTelemetry("x");
            new TelemetryIdTelemetryInitializer().Initialize(t);
            Assert.That(t.Properties["saex.telemetryId"], Is.EqualTo("tid-123"));
        }

        [Test]
        public void NoOp_WhenEmpty()
        {
            SaexTelemetryContext.Current = null;
            var t = new TraceTelemetry("x");
            new TelemetryIdTelemetryInitializer().Initialize(t);
            Assert.That(t.Properties.ContainsKey("saex.telemetryId"), Is.False);
        }
    }
}
