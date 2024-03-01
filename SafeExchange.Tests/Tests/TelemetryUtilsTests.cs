/// <summary>
/// TelemetryUtilsTests
/// </summary>

namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Utilities;
    using System;

    [TestFixture]
    public class TelemetryUtilsTests
    {
        [Test]
        public void TestInnerExceptionsDescriptions()
        {
            var innerException = new Exception("Inner message.");
            var outerException = new Exception("Outer message.", innerException);

            var description = TelemetryUtils.GetDescription(outerException);
            Assert.That(description, Is.EqualTo("System.Exception: Outer message. -> System.Exception: Inner message."));
        }
    }
}
