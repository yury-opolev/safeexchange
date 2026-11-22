/// <summary>
/// McpOAuthHandlerTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using Microsoft.Azure.Functions.Worker.Http;
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// The Functions adapter: request URL and body in, status, <c>Location</c>, JSON body and
    /// <c>Cache-Control: no-store</c> out; a bare 404 while the broker is off.
    /// </summary>
    [TestFixture]
    public class McpOAuthHandlerTests : McpOAuthHttpTestBase
    {
        private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

        private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        [TestCase(".well-known/oauth-authorization-server")]
        [TestCase(".well-known/openid-configuration")]
        public async Task Metadata_IsTheBrokersDocument_ForThisHost(string path)
        {
            var response = await this.OAuthHandler.Metadata(OAuthRequest("get", path));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            AssertJsonNoStore(response);
            Assert.That(BodyOf(response), Is.EqualTo(AuthorizationServerMetadata.ToJson(ApiBase, new[] { "api://safeexchange.test/user_impersonation", "offline_access" })));
            Assert.That(BodyOf(response), Does.StartWith("{\"issuer\":\"https://safeexchange.test/api\","));
        }

        [Test]
        public async Task Register_AnswersCreated_WithTheConfiguredClient()
        {
            var response = await this.OAuthHandler.Register(OAuthRequest("post", "oauth/register", "{\"redirect_uris\":[\"http://127.0.0.1:53682/callback\"]}"));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            AssertJsonNoStore(response);
            Assert.That(BodyOf(response), Does.StartWith($"{{\"client_id\":\"{BrokerClientId}\","));
        }

        [Test]
        public async Task Authorize_AnswersFound_WithTheEntraLocationAndNoBody()
        {
            var query = $"oauth/authorize?client_id={BrokerClientId}&redirect_uri=http%3A%2F%2F127.0.0.1%3A53682%2Fcallback&response_type=code"
                + $"&code_challenge={Challenge}&code_challenge_method=S256&state=abc&resource=https%3A%2F%2Fsafeexchange.test%2Fapi%2Fmcp";

            var response = await this.OAuthHandler.Authorize(OAuthRequest("get", query));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(response.Headers.GetValues("Cache-Control"), Is.EqualTo(new[] { "no-store" }));
            Assert.That(response.Headers.GetValues("Location").Single(), Does.StartWith($"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/authorize?client_id={BrokerClientId}&"));
            Assert.That(response.Headers.Contains("Content-Type"), Is.False);
            Assert.That(BodyOf(response), Is.Empty);
        }

        [Test]
        public async Task Authorize_WithANonLoopbackRedirect_AnswersBadRequest_WithoutALocation()
        {
            var query = $"oauth/authorize?client_id={BrokerClientId}&redirect_uri=https%3A%2F%2Fevil.example%2Fcb&response_type=code&code_challenge={Challenge}&code_challenge_method=S256";

            var response = await this.OAuthHandler.Authorize(OAuthRequest("get", query));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(response.Headers.Contains("Location"), Is.False);
            AssertJsonNoStore(response);
        }

        [Test]
        public async Task Callback_AnswersFound_ToTheClientsLoopbackRedirect()
        {
            Assert.That(new BrokerState("http://127.0.0.1:53682/callback", "abc").TryEncode(out var state), Is.True);

            var response = await this.OAuthHandler.Callback(OAuthRequest("get", $"oauth/callback?code=the-code&state={state}"));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(response.Headers.GetValues("Location"), Is.EqualTo(new[] { "http://127.0.0.1:53682/callback?code=the-code&state=abc" }));
            Assert.That(response.Headers.GetValues("Cache-Control"), Is.EqualTo(new[] { "no-store" }));
        }

        [Test]
        public async Task Token_RelaysEntrasStatusAndBody_AsUncacheableJson()
        {
            this.Entra.Response = new EntraTokenResponse(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}");
            var body = $"grant_type=authorization_code&code=the-code&code_verifier={Verifier}&redirect_uri=http%3A%2F%2F127.0.0.1%3A53682%2Fcallback&client_id={BrokerClientId}";

            var response = await this.OAuthHandler.Token(OAuthRequest("post", "oauth/token", body));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            AssertJsonNoStore(response);
            Assert.That(BodyOf(response), Is.EqualTo("{\"error\":\"invalid_grant\"}"));
            Assert.That(this.Entra.Requests.Single()["redirect_uri"], Is.EqualTo("https://safeexchange.test/api/oauth/callback"));
        }

        [Test]
        public async Task Token_WithAnOversizeBody_IsRefused_WithoutCallingEntra()
        {
            var body = "grant_type=refresh_token&refresh_token=" + new string('r', 1024 * 1024);

            var response = await this.OAuthHandler.Token(OAuthRequest("post", "oauth/token", body));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(this.Entra.Requests, Is.Empty);
        }

        [TestCase("False", BrokerClientId)]
        [TestCase("True", "")]
        public async Task EveryRoute_IsABareNotFound_WhileTheBrokerIsOff(string mcpEndpoint, string clientId)
        {
            this.BuildProvider(("Features:McpEndpoint", mcpEndpoint), ("Mcp:OAuth:ClientId", clientId));
            var handler = this.OAuthHandler;

            var responses = new[]
            {
                await handler.Metadata(OAuthRequest("get", ".well-known/oauth-authorization-server")),
                await handler.Metadata(OAuthRequest("get", ".well-known/openid-configuration")),
                await handler.Register(OAuthRequest("post", "oauth/register", "{\"redirect_uris\":[\"http://127.0.0.1:1/cb\"]}")),
                await handler.Authorize(OAuthRequest("get", $"oauth/authorize?client_id={BrokerClientId}")),
                await handler.Callback(OAuthRequest("get", "oauth/callback?code=c")),
                await handler.Token(OAuthRequest("post", "oauth/token", "grant_type=refresh_token&refresh_token=r")),
            };

            foreach (var response in responses)
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                Assert.That(response.Headers, Is.Empty);
                Assert.That(BodyOf(response), Is.Empty);
            }

            Assert.That(this.Entra.Requests, Is.Empty);
        }

        private static void AssertJsonNoStore(HttpResponseData response)
        {
            Assert.That(response.Headers.GetValues("Content-Type"), Is.EqualTo(new[] { "application/json" }));
            Assert.That(response.Headers.GetValues("Cache-Control"), Is.EqualTo(new[] { "no-store" }));
        }
    }
}
