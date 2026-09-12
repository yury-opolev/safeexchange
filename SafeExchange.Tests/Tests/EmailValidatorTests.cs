/// <summary>
/// EmailValidatorTests
/// </summary>

namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Utilities;

    /// <summary>
    /// The email-like format rule for contact addresses and group mail. These cases are
    /// mirrored verbatim by the client's SubjectNameValidatorTests (safeexchange.blazorpwa):
    /// the two repositories are independent, so the only thing keeping the rules aligned is
    /// that both fixtures are changed together. A client that is stricter than this silently
    /// makes a legitimate address unusable; a client that is looser produces a server error.
    /// </summary>
    [TestFixture]
    public class EmailValidatorTests
    {
        [TestCase("user@example.com", "plain address")]
        [TestCase("first.last@example.com", "dotted local part")]
        [TestCase("first+label@example.com", "plus addressing")]
        [TestCase("first-last@example.com", "hyphen in local part")]
        [TestCase("user_name@example.com", "underscore in local part")]
        [TestCase("user@sub.example.com", "subdomain")]
        [TestCase("user@example.co.uk", "two label suffix")]
        [TestCase("user@example.online", "six character top level domain")]
        [TestCase("user@example.consulting", "ten character top level domain")]
        [TestCase("user@example.technology", "long top level domain")]
        [TestCase("user@my-company.com", "hyphen in domain")]
        public void ValidAddressesAreAccepted(string input, string _why)
        {
            Assert.That(EmailValidator.IsValid(input), Is.True);
            Assert.That(EmailValidator.HasValidFormat(input), Is.True);
        }

        [TestCase("", "empty")]
        [TestCase(null, "missing")]
        [TestCase("user", "no domain")]
        [TestCase("user@", "no domain part")]
        [TestCase("@example.com", "no local part")]
        [TestCase("user@example", "no dot in domain")]
        [TestCase("user@example.c", "single character top level domain")]
        [TestCase("user name@example.com", "space")]
        [TestCase("user@exa mple.com", "space in domain")]
        [TestCase("user@example.com\n", "trailing newline")]
        [TestCase("user@example.com\r", "trailing carriage return")]
        [TestCase("user\n@example.com", "interior newline")]
        public void InvalidAddressesAreRejected(string? input, string _why)
        {
            Assert.That(EmailValidator.IsValid(input), Is.False);
            Assert.That(EmailValidator.HasValidFormat(input), Is.False);
        }

        [Test]
        public void AddressAtTheLengthLimitIsAccepted()
        {
            var domain = "@example.com";
            var local = new string('a', EmailValidator.MaxLength - domain.Length);

            var email = local + domain;

            Assert.That(email, Has.Length.EqualTo(EmailValidator.MaxLength));
            Assert.That(EmailValidator.IsValid(email), Is.True);
        }

        [Test]
        public void AddressOverTheLengthLimitIsRejected()
        {
            var domain = "@example.com";
            var local = new string('a', EmailValidator.MaxLength - domain.Length + 1);

            var email = local + domain;

            Assert.That(EmailValidator.IsValid(email), Is.False);

            // Length is the only thing wrong with it: callers that report "too long"
            // separately from "bad format" rely on the shape check still passing.
            Assert.That(EmailValidator.HasValidFormat(email), Is.True);
        }

        [Test]
        public void TrailingNewlineIsRejectedSoItCannotReachLogsOrResponses()
        {
            // '$' would accept this: in .NET it also matches immediately before a single
            // trailing newline. These values are interpolated into log messages.
            Assert.That(EmailValidator.Pattern, Does.EndWith(@"\z"));
            Assert.That(EmailValidator.IsValid("user@example.com\n"), Is.False);
        }
    }
}
