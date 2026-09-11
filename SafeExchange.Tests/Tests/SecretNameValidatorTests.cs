/// <summary>
/// SecretNameValidatorTests
/// </summary>

namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Utilities;
    using System;

    /// <summary>
    /// The creation naming contract. These cases are mirrored verbatim by the Blazor client's
    /// SecretNameValidatorTests (safeexchange.blazorpwa): if one side changes, the other must
    /// change with it, otherwise a name accepted by one client is rejected by the other.
    /// </summary>
    [TestFixture]
    public class SecretNameValidatorTests
    {
        [TestCase("LegacySecret", "letters")]
        [TestCase("Legacy_Secret", "underscore")]
        [TestCase("Legacy-Secret-123", "hyphen and digits")]
        [TestCase("Legacy_Secret-123", "underscore and hyphen")]
        [TestCase("_leading-underscore", "underscore may lead")]
        [TestCase("-leading-hyphen", "hyphen may lead")]
        [TestCase("12345", "digits only")]
        public void ValidNamesAreAccepted(string input, string _why)
        {
            var isValid = SecretNameValidator.TryValidate(input, out var reason);

            Assert.That(isValid, Is.True, reason);
            Assert.That(reason, Is.Null);
        }

        [TestCase("", "empty")]
        [TestCase(null, "missing")]
        [TestCase("has space", "space")]
        [TestCase("has/slash", "slash")]
        [TestCase("has.dot", "dot")]
        [TestCase("has:colon", "colon")]
        [TestCase("emoji\U0001F600", "non-ascii")]
        [TestCase("Legacy_Secret\n", "trailing newline")]
        [TestCase("Legacy_Secret\r", "trailing carriage return")]
        [TestCase("Legacy\nSecret", "interior newline")]
        public void InvalidNamesAreRejected(string? input, string _why)
        {
            var isValid = SecretNameValidator.TryValidate(input, out var reason);

            Assert.That(isValid, Is.False);
            Assert.That(reason, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void ExactlyMaxLengthIsAccepted()
        {
            var name = new string('a', SecretNameValidator.MaxLength);

            var isValid = SecretNameValidator.TryValidate(name, out var reason);

            Assert.That(isValid, Is.True, reason);
        }

        [Test]
        public void OverMaxLengthIsRejected()
        {
            var name = new string('a', SecretNameValidator.MaxLength + 1);

            var isValid = SecretNameValidator.TryValidate(name, out var reason);

            Assert.That(isValid, Is.False);
            Assert.That(reason, Does.Contain(SecretNameValidator.MaxLength.ToString()));
        }
    }
}
