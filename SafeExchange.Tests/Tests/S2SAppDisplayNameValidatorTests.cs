namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Utilities;

    [TestFixture]
    public class S2SAppDisplayNameValidatorTests
    {
        [TestCase("abc")]
        [TestCase("Build-Pipeline-1")]
        [TestCase("build_pipeline_1")]
        [TestCase("App42")]
        [TestCase("a1B2c3")]
        [TestCase("A-quite-long-but-still-valid-display-name-123_456")]
        public void TryValidate_AcceptsValidNames(string input)
        {
            var ok = S2SAppDisplayNameValidator.TryValidate(input, out var reason);
            Assert.That(ok, Is.True, $"Expected '{input}' to be accepted. Reason: {reason}");
            Assert.That(reason, Is.Null);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void TryValidate_RejectsNullOrWhitespace(string? input)
        {
            var ok = S2SAppDisplayNameValidator.TryValidate(input, out var reason);
            Assert.That(ok, Is.False);
            Assert.That(reason, Does.Contain("required"));
        }

        [TestCase("a")]
        [TestCase("ab")]
        public void TryValidate_RejectsTooShort(string input)
        {
            var ok = S2SAppDisplayNameValidator.TryValidate(input, out var reason);
            Assert.That(ok, Is.False);
            Assert.That(reason, Does.Contain("at least"));
        }

        [Test]
        public void TryValidate_RejectsTooLong()
        {
            var input = new string('a', S2SAppDisplayNameValidator.MaxLength + 1);
            var ok = S2SAppDisplayNameValidator.TryValidate(input, out var reason);
            Assert.That(ok, Is.False);
            Assert.That(reason, Does.Contain("at most"));
        }

        [TestCase("1app")]
        [TestCase("-app")]
        [TestCase("_app")]
        public void TryValidate_RejectsNonLetterStart(string input)
        {
            var ok = S2SAppDisplayNameValidator.TryValidate(input, out var reason);
            Assert.That(ok, Is.False);
            Assert.That(reason, Does.Contain("English letter"));
        }

        [TestCase("app name")]      // space
        [TestCase("app.name")]      // dot
        [TestCase("app@name")]      // at-sign
        [TestCase("app/name")]      // slash
        [TestCase("app!")]          // bang
        [TestCase("Résumé")]        // non-ASCII letter
        [TestCase("приложение")]    // non-Latin
        [TestCase("app​name")] // zero-width space
        public void TryValidate_RejectsDisallowedCharacters(string input)
        {
            var ok = S2SAppDisplayNameValidator.TryValidate(input, out var reason);
            Assert.That(ok, Is.False);
            Assert.That(reason, Does.Contain("English letters"));
        }

        [Test]
        public void TryValidate_AcceptsMinLengthBoundary()
        {
            var input = new string('a', S2SAppDisplayNameValidator.MinLength);
            var ok = S2SAppDisplayNameValidator.TryValidate(input, out var reason);
            Assert.That(ok, Is.True, $"Expected min-length boundary to pass. Reason: {reason}");
        }

        [Test]
        public void TryValidate_AcceptsMaxLengthBoundary()
        {
            var input = new string('a', S2SAppDisplayNameValidator.MaxLength);
            var ok = S2SAppDisplayNameValidator.TryValidate(input, out var reason);
            Assert.That(ok, Is.True, $"Expected max-length boundary to pass. Reason: {reason}");
        }
    }
}
