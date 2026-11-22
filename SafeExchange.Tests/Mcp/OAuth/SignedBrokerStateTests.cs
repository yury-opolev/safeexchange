/// <summary>
/// SignedBrokerStateTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Buffers.Text;
    using System.Security.Cryptography;
    using System.Text;

    [TestFixture]
    public class SignedBrokerStateTests
    {
        private const string Nonce = "bm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm8";

        private static readonly DateTime Now = new(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);

        private static readonly long Expiry = new DateTimeOffset(Now).ToUnixTimeSeconds() + 600;

        [TestCase("http://127.0.0.1:53682/callback", "client-state-123")]
        [TestCase("http://localhost:8080/cb?x=1", "with spaces, \"quotes\", ünïcödé & =+/.")]
        [TestCase("http://[::1]:1234/", null)]
        public void Create_ThenVerify_RoundTrips_WithATenMinuteExpiry(string redirectUri, string? clientState)
        {
            Assert.That(SignedBrokerState.TryCreate(McpOAuthTestKey.Bytes, redirectUri, clientState, Nonce, Now, out var encoded), Is.True);

            Assert.That(encoded, Does.Match("^[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]{43}$"), "base64url payload, a dot and a base64url HMAC-SHA256");
            Assert.That(SignedBrokerState.TryVerify(McpOAuthTestKey.Bytes, encoded, Now, out var state), Is.True);
            Assert.That(state, Is.EqualTo(new SignedBrokerState(redirectUri, clientState, Nonce, Expiry)));
        }

        [Test]
        public void TheState_IsValidForTenMinutes()
        {
            Assert.That(SignedBrokerState.TryCreate(McpOAuthTestKey.Bytes, "http://127.0.0.1:1/", "s", Nonce, Now, out var encoded), Is.True);

            Assert.That(SignedBrokerState.TryVerify(McpOAuthTestKey.Bytes, encoded, Now.AddSeconds(599), out _), Is.True);
            Assert.That(SignedBrokerState.TryVerify(McpOAuthTestKey.Bytes, encoded, Now.AddSeconds(600), out var expired), Is.False);
            Assert.That(expired, Is.Null);
        }

        [Test]
        public void ATamperedPayloadOrSignature_OrAnotherKey_IsRejected()
        {
            Assert.That(SignedBrokerState.TryCreate(McpOAuthTestKey.Bytes, "http://127.0.0.1:1/", "s", Nonce, Now, out var encoded), Is.True);
            var dot = encoded!.IndexOf('.');
            var otherKey = new byte[32];

            Assert.That(SignedBrokerState.TryVerify(McpOAuthTestKey.Bytes, Flip(encoded, 3), Now, out _), Is.False);
            Assert.That(SignedBrokerState.TryVerify(McpOAuthTestKey.Bytes, Flip(encoded, dot + 5), Now, out _), Is.False);
            Assert.That(SignedBrokerState.TryVerify(otherKey, encoded, Now, out _), Is.False);
            Assert.That(SignedBrokerState.TryVerify(McpOAuthTestKey.Bytes, Sign(otherKey, Payload("http://127.0.0.1:1/")), Now, out _), Is.False);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("eyJyIjoiaHR0cDovLzEyNy4wLjAuMToxLyJ9")]
        [TestCase("not base64url!.c2ln")]
        [TestCase("a.b.c")]
        [TestCase(".")]
        public void MissingUnsignedOrMalformedValues_AreRejected(string? encoded)
        {
            Assert.That(SignedBrokerState.TryVerify(McpOAuthTestKey.Bytes, encoded, Now, out var state), Is.False);
            Assert.That(state, Is.Null);
        }

        [TestCase("not json")]
        [TestCase("[]")]
        [TestCase("{}")]
        [TestCase("{\"r\":\"http://127.0.0.1:1/\",\"s\":null,\"n\":\"" + Nonce + "\"}")]
        [TestCase("{\"r\":\"http://127.0.0.1:1/\",\"s\":null,\"n\":\"" + Nonce + "\",\"e\":\"1795342200\"}")]
        [TestCase("{\"r\":\"http://127.0.0.1:1/\",\"s\":2,\"n\":\"" + Nonce + "\",\"e\":1795342200}")]
        [TestCase("{\"r\":\"http://127.0.0.1:1/\",\"s\":null,\"n\":\"" + Nonce + "\",\"e\":1795342200,\"x\":\"extra\"}")]
        [TestCase("{\"r\":\"http://127.0.0.1:1/\",\"r\":\"http://127.0.0.1:2/\",\"s\":null,\"n\":\"" + Nonce + "\",\"e\":1795342200}")]
        [TestCase("{\"r\":\"https://evil.example/cb\",\"s\":null,\"n\":\"" + Nonce + "\",\"e\":1795342200}")]
        [TestCase("{\"r\":\"http://127.0.0.1:1/\",\"s\":\"\\uD800\",\"n\":\"" + Nonce + "\",\"e\":1795342200}")]
        [TestCase("{\"r\":\"http://127.0.0.1:1/\",\"s\":null,\"n\":\"\",\"e\":1795342200}")]
        public void ASignedButMalformedPayload_IsRejected(string json)
        {
            Assert.That(SignedBrokerState.TryVerify(McpOAuthTestKey.Bytes, Sign(McpOAuthTestKey.Bytes, json), Now, out _), Is.False);
        }

        [Test]
        public void TheEncodedState_IsAtMost2KB()
        {
            Assert.That(SignedBrokerState.TryCreate(McpOAuthTestKey.Bytes, "http://127.0.0.1:1/", new string('s', 1000), Nonce, Now, out var fits), Is.True);
            Assert.That(fits!.Length, Is.LessThanOrEqualTo(SignedBrokerState.MaxEncodedLength));
            Assert.That(SignedBrokerState.TryCreate(McpOAuthTestKey.Bytes, "http://127.0.0.1:1/", new string('s', 1600), Nonce, Now, out var none), Is.False);
            Assert.That(none, Is.Null);

            var oversize = Sign(McpOAuthTestKey.Bytes, Payload("http://127.0.0.1:1/", new string('s', 1600)));
            Assert.That(oversize.Length, Is.GreaterThan(SignedBrokerState.MaxEncodedLength));
            Assert.That(SignedBrokerState.TryVerify(McpOAuthTestKey.Bytes, oversize, Now, out _), Is.False);
        }

        private static string Payload(string redirectUri, string clientState = "s")
            => $"{{\"r\":\"{redirectUri}\",\"s\":\"{clientState}\",\"n\":\"{Nonce}\",\"e\":{Expiry}}}";

        // The documented format: base64url(payload) "." base64url(HMAC-SHA256(key, "saex-mcp-state\n" + payload segment)).
        private static string Sign(byte[] key, string json)
        {
            var payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
            var signature = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("saex-mcp-state\n" + payload));
            return payload + "." + Base64Url.EncodeToString(signature);
        }

        private static string Flip(string value, int index)
            => value[..index] + (value[index] == 'A' ? 'B' : 'A') + value[(index + 1)..];
    }
}
