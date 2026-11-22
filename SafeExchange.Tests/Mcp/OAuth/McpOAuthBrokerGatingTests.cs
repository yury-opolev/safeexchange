/// <summary>
/// McpOAuthBrokerGatingTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System.Collections.Generic;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// Every broker endpoint answers a bare 404 unless <c>Features.McpEndpoint</c> is on, <c>Mcp:OAuth:ClientId</c> is
    /// set and <c>Authentication:Authority</c> gives Entra endpoints.
    /// </summary>
    [TestFixture]
    public class McpOAuthBrokerGatingTests : McpOAuthBrokerTestBase
    {
        [TestCase(false, BrokerClientId, Tenant)]
        [TestCase(true, "", Tenant)]
        [TestCase(true, "   ", Tenant)]
        [TestCase(true, BrokerClientId, "")]
        public async Task EveryEndpoint_IsNotFound_UnlessTheBrokerIsFullyConfigured(bool mcpEnabled, string clientId, string authority)
        {
            var broker = this.CreateBroker(mcpEnabled, clientId, authority);

            Assert.That(broker.IsEnabled, Is.False);
            foreach (var result in await AllEndpointsAsync(broker))
            {
                Assert.That(result, Is.EqualTo(McpOAuthResult.NotFound));
            }
        }

        [Test]
        public void TheBroker_IsEnabled_WithMcpOnAClientIdAndAnAuthority()
        {
            Assert.That(this.Broker.IsEnabled, Is.True);
        }

        private static Task<List<McpOAuthResult>> AllEndpointsAsync(McpOAuthBroker broker)
            => Task.FromResult(new List<McpOAuthResult>
            {
                broker.Metadata(ApiBase),
                broker.Register("{\"redirect_uris\":[\"http://127.0.0.1:1/cb\"]}"),
                broker.Authorize(ApiBase, $"client_id={BrokerClientId}&redirect_uri=http%3A%2F%2F127.0.0.1%3A1%2Fcb&response_type=code&code_challenge={Challenge}&code_challenge_method=S256"),
            });
    }
}
