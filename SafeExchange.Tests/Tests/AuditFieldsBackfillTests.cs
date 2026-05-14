/// <summary>
/// AuditFieldsBackfillTests
/// </summary>

namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Migrations;

    [TestFixture]
    public class AuditFieldsBackfillTests
    {
        [Test]
        public void BackfillIfMissing_AddsAuditEnabledFalse_WhenMissing()
        {
            const string input = """{"id":"x","PartitionKey":"00"}""";
            var result = AuditFieldsBackfill.BackfillIfMissing(input);
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Does.Contain("\"AuditEnabled\":false"));
            Assert.That(result, Does.Contain("\"AuditInstanceId\":null"));
        }

        [Test]
        public void BackfillIfMissing_NoOpWhenAuditEnabledAlreadyPresent()
        {
            const string input = """{"id":"x","AuditEnabled":false,"AuditInstanceId":null}""";
            Assert.That(AuditFieldsBackfill.BackfillIfMissing(input), Is.Null);
        }

        [Test]
        public void BackfillIfMissing_NoOpWhenAuditEnabledTrue()
        {
            const string input = """{"id":"x","AuditEnabled":true,"AuditInstanceId":"abc"}""";
            Assert.That(AuditFieldsBackfill.BackfillIfMissing(input), Is.Null);
        }

        [Test]
        public void BackfillIfMissing_PreservesOtherFields()
        {
            const string input = """{"id":"x","ObjectName":"x","Tags":["a"]}""";
            var result = AuditFieldsBackfill.BackfillIfMissing(input);
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Does.Contain("\"id\":\"x\""));
            Assert.That(result, Does.Contain("\"ObjectName\":\"x\""));
            Assert.That(result, Does.Contain("\"Tags\":[\"a\"]"));
            Assert.That(result, Does.Contain("\"AuditEnabled\":false"));
            Assert.That(result, Does.Contain("\"AuditInstanceId\":null"));
        }

        [Test]
        public void BackfillIfMissing_MalformedJson_ReturnsNull()
        {
            Assert.That(AuditFieldsBackfill.BackfillIfMissing("not json"), Is.Null);
        }

        [Test]
        public void BackfillIfMissing_AuditEnabledNull_TreatedAsMissing()
        {
            const string input = """{"id":"x","AuditEnabled":null}""";
            var result = AuditFieldsBackfill.BackfillIfMissing(input);
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Does.Contain("\"AuditEnabled\":false"));
        }
    }
}
