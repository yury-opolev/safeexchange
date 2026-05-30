namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Model;

    [TestFixture]
    public class UserTelemetryFieldsTests
    {
        [Test]
        public void TelemetryId_DefaultsToEmpty()
        {
            var user = new User();
            Assert.That(user.TelemetryId, Is.EqualTo(string.Empty));
        }
    }
}
