/// <summary>
/// McpMetadataTests
/// </summary>

namespace SafeExchange.Tests.Mcp
{
    using NUnit.Framework;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// The anonymous RFC 9728 protected-resource metadata for MCP clients.
    /// </summary>
    [TestFixture]
    public class McpMetadataTests : McpHttpTestBase
    {
        [Test]
        public async Task Metadata_IsTheRfc9728DocumentFromTheAuthenticationSection()
        {
            var response = await this.MetadataHandler.Run(MetadataRequest());

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Headers.GetValues("Content-Type"), Is.EqualTo(new[] { "application/json" }));
            Assert.That(BodyOf(response), Is.EqualTo(
                "{\"resource\":\"https://safeexchange.test/api/mcp\","
                + $"\"authorization_servers\":[\"https://login.microsoftonline.com/{TenantId}/v2.0\"],"
                + "\"bearer_methods_supported\":[\"header\"],"
                + "\"scopes_supported\":[\"api://safeexchange.test/user_impersonation\"]}"));
        }

        [Test]
        public async Task Metadata_WithTheBrokerConfigured_NamesThisHostAsTheAuthorizationServer()
        {
            this.BuildProvider(("Mcp:OAuth:ClientId", "00000000-0000-0000-0000-0000000000c9"));

            var response = await this.MetadataHandler.Run(MetadataRequest());

            Assert.That(BodyOf(response), Is.EqualTo(
                "{\"resource\":\"https://safeexchange.test/api/mcp\","
                + "\"authorization_servers\":[\"https://safeexchange.test/api\"],"
                + "\"bearer_methods_supported\":[\"header\"],"
                + "\"scopes_supported\":[\"api://safeexchange.test/user_impersonation\"]}"));
        }

        [Test]
        public async Task Metadata_UsesMcpScopes_AndKeepsAV2Authority()
        {
            this.BuildProvider(
                ("Authentication:Authority", $"https://login.microsoftonline.com/{TenantId}/v2.0/"),
                ("Authentication:McpScopes", "api://custom/a, api://custom/b"));

            var response = await this.MetadataHandler.Run(MetadataRequest());

            Assert.That(BodyOf(response), Does.Contain($"\"authorization_servers\":[\"https://login.microsoftonline.com/{TenantId}/v2.0\"]"));
            Assert.That(BodyOf(response), Does.Contain("\"scopes_supported\":[\"api://custom/a\",\"api://custom/b\"]"));
        }

        [Test]
        public async Task Metadata_FlagOff_AnswersNotFound()
        {
            this.BuildProvider(("Features:McpEndpoint", "False"));

            var response = await this.MetadataHandler.Run(MetadataRequest());

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(BodyOf(response), Is.Empty);
        }

        private static TestHttpRequestData MetadataRequest()
        {
            var request = TestFactory.CreateHttpRequestData("get");
            request.SetUrl(new Uri("https://safeexchange.test/api/.well-known/oauth-protected-resource"));
            return request;
        }
    }
}
