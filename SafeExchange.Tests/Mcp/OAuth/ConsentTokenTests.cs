/// <summary>
/// ConsentTokenTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System.Collections.Generic;
    using System.Linq;

    [TestFixture]
    public class ConsentTokenTests
    {
        private static readonly KeyValuePair<string, string>[] Fields =
        {
            new("client_id", "00000000-0000-0000-0000-0000000000c9"),
            new("redirect_uri", "http://127.0.0.1:53682/callback"),
            new("state", "a&b=c d"),
            new("consent_expires", "1795342200"),
        };

        [Test]
        public void NewNonce_Is32RandomBytesInBase64Url()
        {
            var first = ConsentToken.NewNonce();
            var second = ConsentToken.NewNonce();

            Assert.That(first, Does.Match("^[A-Za-z0-9_-]{43}$"));
            Assert.That(ConsentToken.IsNonce(first), Is.True);
            Assert.That(second, Is.Not.EqualTo(first));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("short")]
        [TestCase("bm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm8=")]
        [TestCase("bm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Ut+m8")]
        public void IsNonce_RejectsAnythingButA43CharacterBase64UrlValue(string? value)
        {
            Assert.That(ConsentToken.IsNonce(value), Is.False);
        }

        [Test]
        public void ATokenVerifies_ForTheSameKeyNonceAndFields()
        {
            var nonce = ConsentToken.NewNonce();
            var token = ConsentToken.Create(McpOAuthTestKey.Bytes, nonce, Fields);

            Assert.That(token, Does.Match("^[A-Za-z0-9_-]{43}$"));
            Assert.That(ConsentToken.Verify(McpOAuthTestKey.Bytes, nonce, Fields, token), Is.True);
        }

        [Test]
        public void ATokenFails_ForAnotherNonceKeyOrAnyChangedField()
        {
            var nonce = ConsentToken.NewNonce();
            var token = ConsentToken.Create(McpOAuthTestKey.Bytes, nonce, Fields);
            var changedValue = Fields.Select(f => f.Key == "state" ? new KeyValuePair<string, string>("state", "a&b=c e") : f);
            var movedSeparator = Fields.Select(f => f.Key == "state" ? new KeyValuePair<string, string>("state&b", "c d") : f);
            var added = Fields.Append(new KeyValuePair<string, string>("login_hint", "user@contoso.test"));
            var reordered = Fields.Reverse();

            Assert.That(ConsentToken.Verify(McpOAuthTestKey.Bytes, ConsentToken.NewNonce(), Fields, token), Is.False);
            Assert.That(ConsentToken.Verify(new byte[32], nonce, Fields, token), Is.False);
            Assert.That(ConsentToken.Verify(McpOAuthTestKey.Bytes, nonce, changedValue, token), Is.False);
            Assert.That(ConsentToken.Verify(McpOAuthTestKey.Bytes, nonce, movedSeparator, token), Is.False);
            Assert.That(ConsentToken.Verify(McpOAuthTestKey.Bytes, nonce, added, token), Is.False);
            Assert.That(ConsentToken.Verify(McpOAuthTestKey.Bytes, nonce, reordered, token), Is.False);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("not-a-token")]
        public void AMissingOrMalformedToken_OrNonce_Fails(string? value)
        {
            var nonce = ConsentToken.NewNonce();
            var token = ConsentToken.Create(McpOAuthTestKey.Bytes, nonce, Fields);

            Assert.That(ConsentToken.Verify(McpOAuthTestKey.Bytes, nonce, Fields, value), Is.False);
            Assert.That(ConsentToken.Verify(McpOAuthTestKey.Bytes, value, Fields, token), Is.False);
        }
    }
}
