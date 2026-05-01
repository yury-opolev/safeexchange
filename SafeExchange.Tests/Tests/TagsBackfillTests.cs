/// <summary>
/// TagsBackfillTests
/// </summary>

namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Migrations;

    [TestFixture]
    public class TagsBackfillTests
    {
        [Test]
        public void BackfillIfMissing_NoTagsField_AddsEmptyArray()
        {
            const string input = """{"id":"x","PartitionKey":"00","ObjectName":"x"}""";
            var result = TagsBackfill.BackfillIfMissing(input);
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Does.Contain("\"Tags\":[]"));
            Assert.That(result, Does.Contain("\"id\":\"x\""));
        }

        [Test]
        public void BackfillIfMissing_TagsNull_ReplacesWithEmptyArray()
        {
            const string input = """{"id":"x","PartitionKey":"00","Tags":null}""";
            var result = TagsBackfill.BackfillIfMissing(input);
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Does.Contain("\"Tags\":[]"));
            Assert.That(result, Does.Not.Contain("\"Tags\":null"));
        }

        [Test]
        public void BackfillIfMissing_TagsAlreadyEmptyArray_ReturnsNullToSignalNoChange()
        {
            const string input = """{"id":"x","PartitionKey":"00","Tags":[]}""";
            Assert.That(TagsBackfill.BackfillIfMissing(input), Is.Null);
        }

        [Test]
        public void BackfillIfMissing_TagsAlreadyPopulated_ReturnsNullToSignalNoChange()
        {
            const string input = """{"id":"x","PartitionKey":"00","Tags":["audiobook","photo"]}""";
            Assert.That(TagsBackfill.BackfillIfMissing(input), Is.Null);
        }

        [Test]
        public void BackfillIfMissing_PreservesAllOtherFields()
        {
            const string input = """{"id":"x","PartitionKey":"00","ObjectName":"x","KeepInStorage":true,"Content":[],"CreatedBy":"u@t.t"}""";
            var result = TagsBackfill.BackfillIfMissing(input);
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Does.Contain("\"id\":\"x\""));
            Assert.That(result, Does.Contain("\"PartitionKey\":\"00\""));
            Assert.That(result, Does.Contain("\"ObjectName\":\"x\""));
            Assert.That(result, Does.Contain("\"KeepInStorage\":true"));
            Assert.That(result, Does.Contain("\"Content\":[]"));
            Assert.That(result, Does.Contain("\"CreatedBy\":\"u@t.t\""));
            Assert.That(result, Does.Contain("\"Tags\":[]"));
        }

        [Test]
        public void BackfillIfMissing_MalformedJson_ReturnsNull()
        {
            Assert.That(TagsBackfill.BackfillIfMissing("not json at all"), Is.Null.Or.SameAs(null));
        }

        [Test]
        public void BackfillIfMissing_EmptyObject_AddsTagsField()
        {
            const string input = "{}";
            var result = TagsBackfill.BackfillIfMissing(input);
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Does.Contain("\"Tags\":[]"));
        }
    }
}
