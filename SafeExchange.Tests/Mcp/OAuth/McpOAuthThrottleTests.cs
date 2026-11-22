/// <summary>
/// McpOAuthThrottleTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using SafeExchange.Tests.Utilities;
    using System.Linq;
    using System.Net;
    using System.Text.Json.Nodes;

    /// <summary>
    /// Per endpoint and client address: the burst passes, then 429 with <c>Retry-After</c>; JSON <c>slow_down</c> for
    /// register and token, the HTML page for the browser endpoints. Refill is 1 per minute here, so it does not refill
    /// while a test runs.
    /// </summary>
    [TestFixture]
    public class McpOAuthThrottleTests
    {
        private const string Address = "203.0.113.7";

        private RecordingLoggerProvider logs = null!;

        private McpOAuthThrottle throttle = null!;

        [SetUp]
        public void SetUp()
        {
            var options = new McpOAuthOptions
            {
                RateLimit = new McpOAuthRateLimitOptions { TokenPermitsPerMinute = 1, TokenBurst = 1, PermitsPerMinute = 1, Burst = 2 },
            };
            this.logs = new RecordingLoggerProvider();
            this.throttle = new McpOAuthThrottle(
                Mock.Of<IOptionsMonitor<McpOAuthOptions>>(x => x.CurrentValue == options),
                new LoggerFactory(new[] { this.logs }).CreateLogger<McpOAuthThrottle>());
        }

        [Test]
        public void Token_AfterItsBurst_IsSlowDownJson_WithRetryAfter_AndAWarning()
        {
            Assert.That(this.throttle.Check(McpOAuthThrottle.Token, Address), Is.Null);

            var limited = this.throttle.Check(McpOAuthThrottle.Token, Address)!;

            Assert.That(limited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(limited.ContentType, Is.EqualTo("application/json"));
            Assert.That(JsonNode.Parse(limited.Body!)!["error"]!.GetValue<string>(), Is.EqualTo("slow_down"));
            Assert.That(limited.RetryAfterSeconds, Is.InRange(59, 60));
            Assert.That(this.logs.Entries.Single(), Does.StartWith("Warning SafeExchange.Core.Mcp.OAuth.McpOAuthThrottle: MCP OAuth token rate limit exceeded; retry after"));
            Assert.That(this.logs.Entries.Single(), Does.Not.Contain(Address));
        }

        [TestCase(McpOAuthThrottle.Authorize)]
        [TestCase(McpOAuthThrottle.Callback)]
        public void TheBrowserEndpoints_AfterTheirBurst_AreTheTooManyRequestsPage(string endpoint)
        {
            Assert.That(this.throttle.Check(endpoint, Address), Is.Null);
            Assert.That(this.throttle.Check(endpoint, Address), Is.Null);

            var limited = this.throttle.Check(endpoint, Address)!;

            Assert.That(limited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(limited.ContentType, Is.EqualTo("text/html; charset=utf-8"));
            Assert.That(limited.Body, Does.Contain(ConsentPage.TooManyRequestsMessage));
            Assert.That(limited.RetryAfterSeconds, Is.InRange(59, 60));
        }

        [Test]
        public void Register_AfterItsBurst_IsSlowDownJson()
        {
            this.throttle.Check(McpOAuthThrottle.Register, Address);
            this.throttle.Check(McpOAuthThrottle.Register, Address);

            var limited = this.throttle.Check(McpOAuthThrottle.Register, Address)!;

            Assert.That(limited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(JsonNode.Parse(limited.Body!)!["error"]!.GetValue<string>(), Is.EqualTo("slow_down"));
        }

        [Test]
        public void EveryEndpointAndAddress_HasItsOwnBucket_AndNoAddressSharesOne()
        {
            Assert.That(this.throttle.Check(McpOAuthThrottle.Token, Address), Is.Null);
            Assert.That(this.throttle.Check(McpOAuthThrottle.Token, Address), Is.Not.Null);

            Assert.That(this.throttle.Check(McpOAuthThrottle.Token, "198.51.100.1"), Is.Null);
            Assert.That(this.throttle.Check(McpOAuthThrottle.Register, Address), Is.Null);
            Assert.That(this.throttle.Check(McpOAuthThrottle.Token, null), Is.Null);
            Assert.That(this.throttle.Check(McpOAuthThrottle.Token, string.Empty), Is.Not.Null);
        }
    }
}
