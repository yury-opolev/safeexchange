/// <summary>
/// LoopbackRedirectUriTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;

    [TestFixture]
    public class LoopbackRedirectUriTests
    {
        [TestCase("http://127.0.0.1:53682/callback")]
        [TestCase("http://127.0.0.1/")]
        [TestCase("http://127.0.0.1")]
        [TestCase("http://localhost:8080/")]
        [TestCase("http://localhost/oauth/callback?client=copilot")]
        [TestCase("http://LOCALHOST:5000/cb")]
        [TestCase("http://[::1]:1234/oauth/callback")]
        [TestCase("http://[::1]/")]
        public void LoopbackHttpUris_OnAnyPortAndPath_AreAllowed(string value)
        {
            Assert.That(LoopbackRedirectUri.IsValid(value), Is.True);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("https://127.0.0.1:8080/callback")]
        [TestCase("https://localhost/callback")]
        [TestCase("http://example.com/callback")]
        [TestCase("http://localhost.evil.com/callback")]
        [TestCase("http://127.0.0.1.nip.io/callback")]
        [TestCase("http://localhost./callback")]
        [TestCase("http://evil.com/http://127.0.0.1/")]
        [TestCase("http://user@localhost:8080/callback")]
        [TestCase("http://user:pass@127.0.0.1/callback")]
        [TestCase("http://localhost:8080@evil.com/")]
        [TestCase("http://127.0.0.1@evil.com/")]
        [TestCase("http://127.0.0.1:8080/callback#fragment")]
        [TestCase("http://127.0.0.1:8080/callback#")]
        [TestCase("/callback")]
        [TestCase("localhost:8080/callback")]
        [TestCase("//localhost:8080/callback")]
        [TestCase("http://127.0.0.2/callback")]
        [TestCase("http://0.0.0.0/callback")]
        [TestCase("http://[::ffff:127.0.0.1]/callback")]
        [TestCase("http://127.0.0.1\\@evil.com/")]
        [TestCase("http://127.0.0.1/call back")]
        [TestCase("http://127.0.0.1/callback\r\nLocation: https://evil.com")]
        [TestCase("javascript://localhost/%0aalert(1)")]
        [TestCase("ftp://127.0.0.1/callback")]
        public void EverythingElse_IsRejected(string? value)
        {
            Assert.That(LoopbackRedirectUri.IsValid(value), Is.False);
        }

        [Test]
        public void TheLengthLimit_Is2048Characters()
        {
            var prefix = "http://127.0.0.1:8080/";
            var longest = prefix + new string('a', LoopbackRedirectUri.MaxLength - prefix.Length);

            Assert.That(LoopbackRedirectUri.IsValid(longest), Is.True);
            Assert.That(LoopbackRedirectUri.IsValid(longest + "a"), Is.False);
        }
    }
}
