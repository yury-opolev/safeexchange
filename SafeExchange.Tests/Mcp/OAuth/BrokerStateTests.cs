/// <summary>
/// BrokerStateTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Text;

    [TestFixture]
    public class BrokerStateTests
    {
        [TestCase("http://127.0.0.1:53682/callback", "client-state-123")]
        [TestCase("http://localhost:8080/cb?x=1", "with spaces, \"quotes\", ünïcödé & =+/")]
        [TestCase("http://[::1]:1234/", null)]
        public void Encode_ThenDecode_RoundTrips(string redirectUri, string? clientState)
        {
            Assert.That(new BrokerState(redirectUri, clientState).TryEncode(out var encoded), Is.True);

            Assert.That(encoded, Does.Match("^[A-Za-z0-9_-]+$"), "base64url without padding");
            Assert.That(BrokerState.TryDecode(encoded, out var decoded), Is.True);
            Assert.That(decoded, Is.EqualTo(new BrokerState(redirectUri, clientState)));
        }

        [Test]
        public void Encode_RefusesAStateLongerThan2KB()
        {
            var fits = new BrokerState("http://127.0.0.1:1/", new string('s', 1400));
            var tooLong = new BrokerState("http://127.0.0.1:1/", new string('s', 1600));

            Assert.That(fits.TryEncode(out var encoded), Is.True);
            Assert.That(encoded!.Length, Is.LessThanOrEqualTo(BrokerState.MaxEncodedLength));
            Assert.That(tooLong.TryEncode(out var none), Is.False);
            Assert.That(none, Is.Null);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("not base64url!")]
        [TestCase("eyJyIjoiaHR0cDovLzEyNy4wLjAuMToxLyJ9==")]
        public void Decode_RejectsMissingOrNonBase64UrlValues(string? encoded)
        {
            Assert.That(BrokerState.TryDecode(encoded, out var state), Is.False);
            Assert.That(state, Is.Null);
        }

        [TestCase("not json")]
        [TestCase("[]")]
        [TestCase("\"http://127.0.0.1:1/\"")]
        [TestCase("{}")]
        [TestCase("{\"r\":1}")]
        [TestCase("{\"r\":\"http://127.0.0.1:1/\",\"s\":2}")]
        [TestCase("{\"r\":\"http://127.0.0.1:1/\",\"x\":\"extra\"}")]
        [TestCase("{\"r\":\"http://127.0.0.1:1/\",\"r\":\"http://127.0.0.1:2/\"}")]
        [TestCase("{\"r\":\"http://127.0.0.1:1/\"")]
        public void Decode_RejectsMalformedPayloads(string json)
        {
            Assert.That(BrokerState.TryDecode(Encode(json), out _), Is.False);
        }

        [TestCase("https://evil.example/callback")]
        [TestCase("http://localhost.evil.com/callback")]
        [TestCase("http://127.0.0.1.nip.io/callback")]
        [TestCase("http://user@127.0.0.1/callback")]
        public void Decode_RejectsANonLoopbackRedirectInside(string redirectUri)
        {
            var crafted = Encode($"{{\"r\":\"{redirectUri}\",\"s\":\"x\"}}");

            Assert.That(BrokerState.TryDecode(crafted, out _), Is.False);
        }

        [Test]
        public void Decode_RejectsAnOversizeValue_BeforeDecoding()
        {
            var oversize = Encode($"{{\"r\":\"http://127.0.0.1:1/\",\"s\":\"{new string('s', 1600)}\"}}");

            Assert.That(oversize.Length, Is.GreaterThan(BrokerState.MaxEncodedLength));
            Assert.That(BrokerState.TryDecode(oversize, out _), Is.False);
        }

        private static string Encode(string json)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
