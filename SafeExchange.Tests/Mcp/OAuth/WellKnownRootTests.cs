/// <summary>
/// WellKnownRootTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using Microsoft.Azure.Functions.Worker.Http;
    using NUnit.Framework;
    using SafeExchange.Core.Functions.Mcp;
    using SafeExchange.Functions;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Net;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;

    /// <summary>
    /// The discovery documents at the host root answer exactly like their <c>/api/.well-known/*</c> twins (same
    /// gating, same <c>https://host/api</c> issuer and resource), and any other root well-known path is a plain 404.
    /// </summary>
    [TestFixture]
    public class WellKnownRootTests : McpOAuthHttpTestBase
    {
        private static readonly Uri HostRoot = new("https://safeexchange.test/");

        private SafeWellKnownRoot Root => new(this.OAuthHandler, this.MetadataHandler);

        [Test]
        public async Task AuthorizationServerApi_IsTheBrokersDocument_WithTheApiIssuer()
        {
            var expected = await this.OAuthHandler.Metadata(OAuthRequest("get", ".well-known/oauth-authorization-server"));

            var response = await this.Root.AuthorizationServerApi(RootRequest(".well-known/oauth-authorization-server/api"));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(BodyOf(response), Is.EqualTo(BodyOf(expected)));
            Assert.That(BodyOf(response), Does.StartWith("{\"issuer\":\"https://safeexchange.test/api\","));
            Assert.That(BodyOf(response), Does.Contain("\"token_endpoint\":\"https://safeexchange.test/api/oauth/token\""));
        }

        [Test]
        public async Task OpenIdConfigurationApi_IsTheBrokersDocument_WithTheApiIssuer()
        {
            var expected = await this.OAuthHandler.Metadata(OAuthRequest("get", ".well-known/openid-configuration"));

            var response = await this.Root.OpenIdConfigurationApi(RootRequest(".well-known/openid-configuration/api"));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(BodyOf(response), Is.EqualTo(BodyOf(expected)));
            Assert.That(BodyOf(response), Does.StartWith("{\"issuer\":\"https://safeexchange.test/api\","));
        }

        [Test]
        public async Task ProtectedResourceApiMcp_IsTheRfc9728Document_ForTheApiMcpResource()
        {
            var expected = await this.MetadataHandler.Run(RootRequest("api/.well-known/oauth-protected-resource"));

            var response = await this.Root.ProtectedResourceApiMcp(RootRequest(".well-known/oauth-protected-resource/api/mcp"));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(BodyOf(response), Is.EqualTo(BodyOf(expected)));
            Assert.That(BodyOf(response), Does.StartWith("{\"resource\":\"https://safeexchange.test/api/mcp\",\"authorization_servers\":[\"https://safeexchange.test/api\"]"));
        }

        [Test]
        public async Task RootDocuments_AreNotFound_WhileTheirApiTwinsAre()
        {
            this.BuildProvider(("Features:McpEndpoint", "False"));

            foreach (var response in new[]
            {
                await this.Root.AuthorizationServerApi(RootRequest(".well-known/oauth-authorization-server/api")),
                await this.Root.OpenIdConfigurationApi(RootRequest(".well-known/openid-configuration/api")),
                await this.Root.ProtectedResourceApiMcp(RootRequest(".well-known/oauth-protected-resource/api/mcp")),
            })
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                Assert.That(BodyOf(response), Is.Empty);
            }
        }

        [Test]
        public async Task BrokerDocuments_AreNotFound_WithoutAClientId()
        {
            this.BuildProvider();

            var response = await this.Root.AuthorizationServerApi(RootRequest(".well-known/oauth-authorization-server/api"));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }

        [TestCase(".well-known/oauth-authorization-server/api/mcp")]
        [TestCase(".well-known/anything")]
        [TestCase(".well-known/a/b/c")]
        public void NotFound_IsAPlain404(string path)
        {
            var response = this.Root.NotFound(RootRequest(path));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(BodyOf(response), Is.Empty);
        }

        // The Functions host takes the first matching route (by function name), so the catch-all must not match the root documents.
        [TestCase("oauth-authorization-server/api", false)]
        [TestCase("openid-configuration/api", false)]
        [TestCase("oauth-protected-resource/api/mcp", false)]
        [TestCase("oauth-authorization-server/api/mcp", true)]
        [TestCase("oauth-authorization-server/apix", true)]
        [TestCase("oauth-protected-resource/api", true)]
        [TestCase("anything", true)]
        [TestCase("a/b/c", true)]
        public void NotFoundRoute_ExcludesTheRootDocuments(string rest, bool matched)
        {
            var constraint = Regex.Match(McpRoutes.WellKnownNotFound, @"\{\*rest:regex\((.*)\)\}$");

            Assert.That(constraint.Success, Is.True);
            Assert.That(Regex.IsMatch(rest, constraint.Groups[1].Value), Is.EqualTo(matched));
        }

        private static TestHttpRequestData RootRequest(string path)
        {
            var request = TestFactory.CreateHttpRequestData("get");
            request.SetUrl(new Uri(HostRoot, path));
            return request;
        }
    }
}
