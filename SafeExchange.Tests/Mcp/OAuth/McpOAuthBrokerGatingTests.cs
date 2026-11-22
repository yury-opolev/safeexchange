/// <summary>
/// McpOAuthBrokerGatingTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// Every broker endpoint answers a bare 404 unless <c>Features.McpEndpoint</c> is on, <c>Mcp:OAuth:ClientId</c> is
    /// set, <c>Mcp:OAuth:StateKey</c> is a valid key and <c>Authentication:Authority</c> gives Entra endpoints.
    /// </summary>
    [TestFixture]
    public class McpOAuthBrokerGatingTests : McpOAuthBrokerTestBase
    {
        [TestCase(false, BrokerClientId, Tenant, McpOAuthTestKey.Value)]
        [TestCase(true, "", Tenant, McpOAuthTestKey.Value)]
        [TestCase(true, "   ", Tenant, McpOAuthTestKey.Value)]
        [TestCase(true, BrokerClientId, "", McpOAuthTestKey.Value)]
        [TestCase(true, BrokerClientId, Tenant, "")]
        [TestCase(true, BrokerClientId, Tenant, "dG9vIHNob3J0")]
        public async Task EveryEndpoint_IsNotFound_UnlessTheBrokerIsFullyConfigured(bool mcpEnabled, string clientId, string authority, string stateKey)
        {
            var broker = this.CreateBroker(mcpEnabled, clientId, authority, stateKey);

            Assert.That(broker.IsEnabled, Is.False);
            foreach (var result in await AllEndpointsAsync(broker, ApiBase))
            {
                Assert.That(result, Is.EqualTo(McpOAuthResult.NotFound));
            }

            Assert.That(this.Entra.Requests, Is.Empty);
        }

        [Test]
        public void TheBroker_IsEnabled_WithMcpOnAClientIdAStateKeyAndAnAuthority()
        {
            Assert.That(this.Broker.IsEnabled, Is.True);
        }

        [Test]
        public async Task EveryEndpoint_IsNotFound_ForAPlainHttpBase_AndOneWarningIsLogged()
        {
            foreach (var result in await AllEndpointsAsync(this.Broker, new Uri("http://safeexchange.test/api/")))
            {
                Assert.That(result, Is.EqualTo(McpOAuthResult.NotFound));
            }

            Assert.That(this.Entra.Requests, Is.Empty);
            Assert.That(this.Logs.Entries.Single(), Does.StartWith("Warning SafeExchange.Core.Mcp.McpBaseUriGuard: "));
        }

        [Test]
        public void APlainHttpLoopbackBase_IsServed_ForLocalDevelopment()
        {
            var result = this.Broker.Metadata(new Uri("http://localhost:7071/api/"));

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(result.Body, Does.StartWith("{\"issuer\":\"http://localhost:7071/api\","));
        }

        private static async Task<List<McpOAuthResult>> AllEndpointsAsync(McpOAuthBroker broker, Uri apiBase)
            => new List<McpOAuthResult>
            {
                broker.Metadata(apiBase),
                broker.Register(apiBase, "{\"redirect_uris\":[\"http://127.0.0.1:1/cb\"]}"),
                broker.Authorize(apiBase, $"client_id={BrokerClientId}&redirect_uri=http%3A%2F%2F127.0.0.1%3A1%2Fcb&response_type=code&code_challenge={Challenge}&code_challenge_method=S256"),
                broker.AuthorizeConsent(apiBase, $"client_id={BrokerClientId}&decision=allow", "bm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm8"),
                broker.Callback(apiBase, "code=c&state=eyJyIjoiaHR0cDovLzEyNy4wLjAuMToxL2NiIn0", "bm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm8"),
                await broker.TokenAsync(apiBase, "grant_type=refresh_token&refresh_token=r"),
            };
    }
}
