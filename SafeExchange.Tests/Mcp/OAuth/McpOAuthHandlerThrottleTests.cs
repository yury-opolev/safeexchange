/// <summary>
/// McpOAuthHandlerThrottleTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using Microsoft.Azure.Functions.Worker.Http;
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// The adapter throttles each broker route per client address (the last <c>X-Forwarded-For</c> entry, which the App
    /// Service front end appends) before the broker runs, and refuses a body that is not valid UTF-8.
    /// </summary>
    [TestFixture]
    public class McpOAuthHandlerThrottleTests : McpOAuthHttpTestBase
    {
        private const string RefreshBody = "grant_type=refresh_token&refresh_token=r&client_id=" + BrokerClientId;

        [SetUp]
        public void ThrottleSetUp()
        {
            this.BuildProvider(
                ("Mcp:OAuth:ClientId", BrokerClientId),
                ("Mcp:OAuth:StateKey", McpOAuthTestKey.Value),
                ("Mcp:OAuth:RateLimit:TokenPermitsPerMinute", "1"),
                ("Mcp:OAuth:RateLimit:TokenBurst", "1"),
                ("Mcp:OAuth:RateLimit:PermitsPerMinute", "1"),
                ("Mcp:OAuth:RateLimit:Burst", "1"));
        }

        [Test]
        public async Task Token_BeyondTheBurst_IsSlowDown_WithRetryAfter_AndDoesNotReachEntra()
        {
            var first = await this.OAuthHandler.Token(Forwarded(OAuthRequest("post", "oauth/token", RefreshBody), "198.51.100.1, 203.0.113.7:50123"));
            var second = await this.OAuthHandler.Token(Forwarded(OAuthRequest("post", "oauth/token", RefreshBody), "192.0.2.99, 203.0.113.7:50124"));

            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(second.Headers.GetValues("Retry-After").Single(), Is.EqualTo("60"));
            Assert.That(second.Headers.GetValues("Content-Type"), Is.EqualTo(new[] { "application/json" }));
            Assert.That(second.Headers.GetValues("Cache-Control"), Is.EqualTo(new[] { "no-store" }));
            Assert.That(BodyOf(second), Does.StartWith("{\"error\":\"slow_down\","));
            Assert.That(this.Entra.Requests, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task AnotherClientAddress_HasItsOwnBucket()
        {
            await this.OAuthHandler.Token(Forwarded(OAuthRequest("post", "oauth/token", RefreshBody), "203.0.113.7:50123"));

            var other = await this.OAuthHandler.Token(Forwarded(OAuthRequest("post", "oauth/token", RefreshBody), "203.0.113.8:50123"));

            Assert.That(other.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public async Task Authorize_BeyondTheBurst_IsTheTooManyRequestsPage()
        {
            var query = $"oauth/authorize?client_id={BrokerClientId}&redirect_uri=http%3A%2F%2F127.0.0.1%3A53682%2Fcallback&response_type=code"
                + "&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256";
            await this.OAuthHandler.Authorize(OAuthRequest("get", query));

            var limited = await this.OAuthHandler.Authorize(OAuthRequest("get", query));

            Assert.That(limited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(limited.Headers.GetValues("Retry-After").Single(), Is.EqualTo("60"));
            Assert.That(limited.Headers.GetValues("Content-Type"), Is.EqualTo(new[] { "text/html; charset=utf-8" }));
            Assert.That(limited.Headers.GetValues("X-Frame-Options"), Is.EqualTo(new[] { "DENY" }));
            Assert.That(limited.Headers.Contains("Set-Cookie"), Is.False);
            Assert.That(BodyOf(limited), Does.Contain(ConsentPage.TooManyRequestsMessage));
        }

        [Test]
        public async Task ABodyThatIsNotUtf8_IsRefused()
        {
            var invalid = new byte[] { 0x7B, 0x22, 0x72, 0x22, 0x3A, 0x22, 0xC3, 0x28, 0x22, 0x7D };

            var register = await this.OAuthHandler.Register(RawRequest("oauth/register", invalid));
            var token = await this.OAuthHandler.Token(RawRequest("oauth/token", invalid));

            Assert.That(register.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(BodyOf(register), Does.StartWith("{\"error\":\"invalid_client_metadata\","));
            Assert.That(token.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(BodyOf(token), Does.StartWith("{\"error\":\"invalid_request\","));
        }

        private static HttpRequestData Forwarded(TestHttpRequestData request, string forwardedFor)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
            return request;
        }

        private static TestHttpRequestData RawRequest(string path, byte[] body)
        {
            var request = TestFactory.CreateHttpRequestData("post");
            request.SetUrl(new Uri(ApiBase, path));
            request.SetBodyAsStream(new MemoryStream(body));
            return request;
        }
    }
}
