/// <summary>
/// TagValidatorTests
/// </summary>

namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Utilities;
    using System.Linq;

    [TestFixture]
    public class TagValidatorTests
    {
        [TestCase("audiobook", "audiobook")]
        [TestCase("AudioBook", "audiobook")]
        [TestCase("genre:Sci-Fi", "genre:sci-fi")]
        [TestCase("year_1965", "year_1965")]
        public void ValidNormalises(string input, string expected)
        {
            var (ok, normalised, error) = TagValidator.TryNormalize(input);
            Assert.That(ok, Is.True, error);
            Assert.That(normalised, Is.EqualTo(expected));
        }

        [TestCase("", "empty")]
        [TestCase("  ", "whitespace")]
        [TestCase(":leading-colon", "must start alphanum")]
        [TestCase("-leading-hyphen", "must start alphanum")]
        [TestCase("has space", "no whitespace")]
        [TestCase("emoji😀", "ascii only")]
        public void InvalidIsRejected(string input, string _why)
        {
            var (ok, _, error) = TagValidator.TryNormalize(input);
            Assert.That(ok, Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void TooLongIsRejected()
        {
            var input = new string('a', 65);
            var (ok, _, _) = TagValidator.TryNormalize(input);
            Assert.That(ok, Is.False);
        }

        [Test]
        public void NormalizeListDeduplicatesAndPreservesOrder()
        {
            var input = new[] { "Audiobook", "audiobook", "Genre:SciFi", "audiobook" };
            var (ok, normalised, error) = TagValidator.TryNormalizeList(input);
            Assert.That(ok, Is.True, error);
            Assert.That(normalised, Is.EqualTo(new[] { "audiobook", "genre:scifi" }));
        }

        [Test]
        public void NormalizeListRejectsTooMany()
        {
            var input = Enumerable.Range(0, 17).Select(i => $"tag{i}").ToArray();
            var (ok, _, _) = TagValidator.TryNormalizeList(input);
            Assert.That(ok, Is.False);
        }
    }
}
