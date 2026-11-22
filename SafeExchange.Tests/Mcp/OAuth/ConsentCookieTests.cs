/// <summary>
/// ConsentCookieTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;

    [TestFixture]
    public class ConsentCookieTests
    {
        private const string Nonce = "bm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm8";

        [Test]
        public void AnIssuedCookie_IsAHostOnlySecureHttpOnlyLaxCookieForTenMinutes()
        {
            Assert.That(
                ConsentCookie.Issue(Nonce).ToSetCookieHeader(),
                Is.EqualTo($"__Host-saex-mcp-consent={Nonce}; Max-Age=600; Path=/; Secure; HttpOnly; SameSite=Lax"));
        }

        [Test]
        public void TheExpiringCookie_ClearsTheValue()
        {
            Assert.That(
                ConsentCookie.Expire.ToSetCookieHeader(),
                Is.EqualTo("__Host-saex-mcp-consent=; Max-Age=0; Path=/; Secure; HttpOnly; SameSite=Lax"));
        }

        [TestCase("__Host-saex-mcp-consent=" + Nonce, Nonce)]
        [TestCase("a=1; __Host-saex-mcp-consent=" + Nonce + "; b=2", Nonce)]
        [TestCase("a=1;__Host-saex-mcp-consent=" + Nonce, Nonce)]
        [TestCase("__Host-saex-mcp-consent=first; __Host-saex-mcp-consent=second", "first")]
        [TestCase("__Host-saex-mcp-consent=", null)]
        [TestCase("x__Host-saex-mcp-consent=" + Nonce, null)]
        [TestCase("__host-saex-mcp-consent=" + Nonce, null)]
        [TestCase("a=1; b=2", null)]
        [TestCase("", null)]
        [TestCase(null, null)]
        public void Read_FindsTheCookieInACookieHeader(string? header, string? expected)
        {
            Assert.That(ConsentCookie.Read(header), Is.EqualTo(expected));
        }
    }
}
