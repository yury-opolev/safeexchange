/// <summary>
/// McpOAuthBrokerRegisterTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System.Linq;
    using System.Net;

    [TestFixture]
    public class McpOAuthBrokerRegisterTests : McpOAuthBrokerTestBase
    {
        // What rmcp (Copilot CLI) posts.
        private const string RmcpRegistration =
            "{\"client_name\":\"GitHub Copilot CLI\",\"redirect_uris\":[\"http://127.0.0.1:53682/callback\"],"
            + "\"grant_types\":[\"authorization_code\",\"refresh_token\"],\"token_endpoint_auth_method\":\"none\","
            + "\"response_types\":[\"code\"],\"scope\":\"api://safeexchange.test/user_impersonation\",\"application_type\":\"native\"}";

        [Test]
        public void Metadata_AdvertisesTheApiScopePlusOfflineAccess()
        {
            var result = this.Broker.Metadata(ApiBase);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(result.Body, Is.EqualTo(AuthorizationServerMetadata.ToJson(ApiBase, new[] { ApiScope, "offline_access" })));
        }

        [Test]
        public void Register_AnRmcpClient_ReturnsTheConfiguredPublicClient()
        {
            var result = this.Broker.Register(RmcpRegistration);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(result.Location, Is.Null);
            Assert.That(result.Body, Is.EqualTo(
                $"{{\"client_id\":\"{BrokerClientId}\","
                + "\"redirect_uris\":[\"http://127.0.0.1:53682/callback\"],"
                + "\"grant_types\":[\"authorization_code\",\"refresh_token\"],"
                + "\"response_types\":[\"code\"],"
                + "\"token_endpoint_auth_method\":\"none\"}"));
        }

        [Test]
        public void Register_WithOnlyRedirectUris_EchoesThemAll()
        {
            var result = this.Broker.Register("{\"redirect_uris\":[\"http://localhost:8080/cb\",\"http://[::1]:8080/cb\"]}");

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(Json(result.Body)["redirect_uris"]!.ToJsonString(), Is.EqualTo("[\"http://localhost:8080/cb\",\"http://[::1]:8080/cb\"]"));
        }

        [TestCase("{\"redirect_uris\":[\"https://evil.example/callback\"]}")]
        [TestCase("{\"redirect_uris\":[\"http://localhost.evil.com/callback\"]}")]
        [TestCase("{\"redirect_uris\":[\"http://127.0.0.1.nip.io/callback\"]}")]
        [TestCase("{\"redirect_uris\":[\"http://127.0.0.1:1/cb\",\"https://evil.example/cb\"]}")]
        [TestCase("{\"redirect_uris\":[\"http://user@127.0.0.1/cb\"]}")]
        [TestCase("{\"redirect_uris\":[\"http://127.0.0.1/cb#x\"]}")]
        [TestCase("{\"redirect_uris\":[]}")]
        [TestCase("{\"redirect_uris\":\"http://127.0.0.1/cb\"}")]
        [TestCase("{\"redirect_uris\":[1]}")]
        [TestCase("{\"client_name\":\"no redirect\"}")]
        public void Register_WithoutOnlyLoopbackRedirectUris_IsInvalidRedirectUri(string body)
        {
            var result = this.Broker.Register(body);

            AssertError(result, "invalid_redirect_uri");
        }

        [Test]
        public void Register_WithMoreThanTenRedirectUris_IsInvalidRedirectUri()
        {
            var uris = string.Join(',', Enumerable.Range(1, 11).Select(i => $"\"http://127.0.0.1:{i}/cb\""));

            AssertError(this.Broker.Register($"{{\"redirect_uris\":[{uris}]}}"), "invalid_redirect_uri");
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("not json")]
        [TestCase("[\"http://127.0.0.1/cb\"]")]
        [TestCase("{\"redirect_uris\":[\"http://127.0.0.1/cb\"],\"redirect_uris\":[\"https://evil.example/cb\"]}")]
        [TestCase("{\"redirect_uris\":[\"http://127.0.0.1/cb\"],\"token_endpoint_auth_method\":\"client_secret_basic\"}")]
        [TestCase("{\"redirect_uris\":[\"http://127.0.0.1/cb\"],\"grant_types\":[\"authorization_code\",\"client_credentials\"]}")]
        [TestCase("{\"redirect_uris\":[\"http://127.0.0.1/cb\"],\"grant_types\":\"authorization_code\"}")]
        [TestCase("{\"redirect_uris\":[\"http://127.0.0.1/cb\"],\"response_types\":[\"token\"]}")]
        public void Register_WithOtherInvalidMetadata_IsInvalidClientMetadata(string? body)
        {
            AssertError(this.Broker.Register(body), "invalid_client_metadata");
        }

        [Test]
        public void Register_ABodyOver16KB_IsInvalidClientMetadata()
        {
            var body = "{\"redirect_uris\":[\"http://127.0.0.1/cb\"],\"client_name\":\"" + new string('n', McpOAuthBroker.MaxBodyLength) + "\"}";

            AssertError(this.Broker.Register(body), "invalid_client_metadata");
        }

        private static void AssertError(McpOAuthResult result, string error)
        {
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(result.Location, Is.Null);
            Assert.That(Json(result.Body)["error"]!.GetValue<string>(), Is.EqualTo(error));
            Assert.That(Json(result.Body)["error_description"]!.GetValue<string>(), Is.Not.Empty);
        }
    }
}
