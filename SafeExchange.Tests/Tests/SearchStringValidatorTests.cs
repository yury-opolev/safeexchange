/// <summary>
/// SearchStringValidatorTests
/// </summary>

namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Utilities;

    /// <summary>
    /// Tests for <see cref="SearchStringValidator"/>, the allowlist-based guard
    /// that prevents Microsoft Graph <c>$search</c> injection
    /// (OWASP A05:2025 / CWE-943). The validator runs both at the HTTP handler
    /// boundary and inside <see cref="SafeExchange.Core.Graph.GraphDataProvider"/>
    /// as defense-in-depth, so any regression in either site is covered by these
    /// cases.
    /// </summary>
    [TestFixture]
    public class SearchStringValidatorTests
    {
        [TestCase("alice")]
        [TestCase("Alice Smith")]
        [TestCase("alice@contoso.com")]
        [TestCase("Jose Mourinho")]
        [TestCase("finance-team")]
        [TestCase("Résumé Owner")]
        [TestCase("42")]
        public void TryValidate_AcceptsTypicalInputs(string input)
        {
            var ok = SearchStringValidator.TryValidate(input, out var reason);
            Assert.That(ok, Is.True, $"Expected '{input}' to be accepted. Reason was: {reason}");
            Assert.That(reason, Is.Null);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void TryValidate_RejectsEmptyOrWhitespace(string? input)
        {
            var ok = SearchStringValidator.TryValidate(input, out var reason);
            Assert.That(ok, Is.False);
            Assert.That(reason, Does.Contain("empty"));
        }

        /// <summary>
        /// The canonical payload from the OWASP finding:
        /// <c>foo" OR "accountEnabled:true</c> is what lets an attacker escape
        /// the quoted <c>"field:value"</c> template and append arbitrary search
        /// terms to enumerate the directory. Must be rejected.
        /// </summary>
        [Test]
        public void TryValidate_RejectsQuoteBreakout()
        {
            var payload = "foo\" OR \"accountEnabled:true";
            var ok = SearchStringValidator.TryValidate(payload, out var reason);
            Assert.That(ok, Is.False);
            Assert.That(reason, Does.Contain("invalid characters"));
        }

        [TestCase("has\"quote")]
        [TestCase("back\\slash")]
        [TestCase("line\nbreak")]
        [TestCase("carriage\rreturn")]
        [TestCase("null\0byte")]
        [TestCase("tab\tchar")]
        public void TryValidate_RejectsControlAndMetaCharacters(string input)
        {
            var ok = SearchStringValidator.TryValidate(input, out var reason);
            Assert.That(ok, Is.False, $"Expected '{input.Replace("\0", "\\0").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t")}' to be rejected.");
            Assert.That(reason, Does.Contain("invalid characters"));
        }

        [Test]
        public void TryValidate_AcceptsAtMaxLengthBoundary()
        {
            var input = new string('a', SearchStringValidator.MaxLength);
            var ok = SearchStringValidator.TryValidate(input, out var reason);
            Assert.That(ok, Is.True, $"Reason: {reason}");
        }

        [Test]
        public void TryValidate_RejectsOneOverMaxLength()
        {
            var input = new string('a', SearchStringValidator.MaxLength + 1);
            var ok = SearchStringValidator.TryValidate(input, out var reason);
            Assert.That(ok, Is.False);
            Assert.That(reason, Does.Contain("too long"));
        }
    }
}
