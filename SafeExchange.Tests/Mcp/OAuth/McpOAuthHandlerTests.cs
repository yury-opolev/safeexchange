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
    /// The Functions adapter: request URL, body and cookie in; status, <c>Location</c>, <c>Set-Cookie</c>, a JSON or HTML
    /// body (HTML with the security headers) and <c>Cache-Control: no-store</c> out; a bare 404 while the broker is off.
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
        public async Task Authorize_AnswersTheConsentPage_AsUncacheableHtmlWithTheSecurityHeaders_AndSetsTheCookie()
        {
            var response = await this.OAuthHandler.Authorize(OAuthRequest("get", AuthorizeQuery + "&resource=https%3A%2F%2Fsafeexchange.test%2Fapi%2Fmcp"));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            AssertHtmlNoStore(response);
            Assert.That(response.Headers.GetValues("Set-Cookie").Single(), Does.Match("^__Host-saex-mcp-consent=[A-Za-z0-9_-]{43}; Max-Age=600; Path=/; Secure; HttpOnly; SameSite=Lax$"));
            Assert.That(response.Headers.Contains("Location"), Is.False);
            Assert.That(BodyOf(response), Does.Contain("<form method=\"post\" action=\"https://safeexchange.test/api/oauth/authorize\">"));
        }

        [Test]
        public async Task AuthorizePost_WithTheCookie_RedirectsToEntra()
        {
            var page = await this.OAuthHandler.Authorize(OAuthRequest("get", AuthorizeQuery));
            var form = ConsentForm.Post(ConsentForm.HiddenFields(BodyOf(page)));

            var response = await this.OAuthHandler.Authorize(OAuthRequest("post", "oauth/authorize", form, "theme=dark; " + CookieFrom(page)));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(response.Headers.GetValues("Cache-Control"), Is.EqualTo(new[] { "no-store" }));
            Assert.That(response.Headers.GetValues("Location").Single(), Does.StartWith($"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/authorize?client_id={BrokerClientId}&"));
            Assert.That(response.Headers.Contains("Set-Cookie"), Is.False);
            Assert.That(response.Headers.Contains("Content-Type"), Is.False);
            Assert.That(BodyOf(response), Is.Empty);
        }

        [Test]
        public async Task AuthorizePost_WithoutTheCookie_IsTheInvalidPage()
        {
            var page = await this.OAuthHandler.Authorize(OAuthRequest("get", AuthorizeQuery));

            var response = await this.OAuthHandler.Authorize(OAuthRequest("post", "oauth/authorize", ConsentForm.Post(ConsentForm.HiddenFields(BodyOf(page)))));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            AssertHtmlNoStore(response);
            Assert.That(response.Headers.Contains("Location"), Is.False);
            Assert.That(BodyOf(response), Does.Contain(ConsentPage.InvalidRequestMessage));
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
        public async Task Callback_ForwardsToTheClientsLoopbackRedirect_AndExpiresTheCookie()
        {
            var nonce = ConsentToken.NewNonce();
            Assert.That(SignedBrokerState.TryCreate(McpOAuthTestKey.Bytes, "http://127.0.0.1:53682/callback", "abc", nonce, DateTime.UtcNow, out var state), Is.True);
            var request = OAuthRequest("get", $"oauth/callback?code=the-code&state={state}", cookie: "theme=dark");
            request.Headers.Add("Cookie", $"{ConsentCookie.Name}={nonce}");

            var response = await this.OAuthHandler.Callback(request);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(response.Headers.GetValues("Location"), Is.EqualTo(new[] { "http://127.0.0.1:53682/callback?code=the-code&state=abc" }));
            Assert.That(response.Headers.GetValues("Cache-Control"), Is.EqualTo(new[] { "no-store" }));
            Assert.That(response.Headers.GetValues("Set-Cookie"), Is.EqualTo(new[] { "__Host-saex-mcp-consent=; Max-Age=0; Path=/; Secure; HttpOnly; SameSite=Lax" }));
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

        [TestCase("False", BrokerClientId, McpOAuthTestKey.Value)]
        [TestCase("True", "", McpOAuthTestKey.Value)]
        [TestCase("True", BrokerClientId, "")]
        public async Task EveryRoute_IsABareNotFound_WhileTheBrokerIsOff(string mcpEndpoint, string clientId, string stateKey)
        {
            this.BuildProvider(("Features:McpEndpoint", mcpEndpoint), ("Mcp:OAuth:ClientId", clientId), ("Mcp:OAuth:StateKey", stateKey));
            var handler = this.OAuthHandler;

            var responses = new[]
            {
                await handler.Metadata(OAuthRequest("get", ".well-known/oauth-authorization-server")),
                await handler.Metadata(OAuthRequest("get", ".well-known/openid-configuration")),
                await handler.Register(OAuthRequest("post", "oauth/register", "{\"redirect_uris\":[\"http://127.0.0.1:1/cb\"]}")),
                await handler.Authorize(OAuthRequest("get", $"oauth/authorize?client_id={BrokerClientId}")),
                await handler.Authorize(OAuthRequest("post", "oauth/authorize", $"client_id={BrokerClientId}&decision=allow")),
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

        private const string AuthorizeQuery = "oauth/authorize?client_id=" + BrokerClientId + "&redirect_uri=http%3A%2F%2F127.0.0.1%3A53682%2Fcallback"
            + "&response_type=code&code_challenge=" + Challenge + "&code_challenge_method=S256&state=abc";

        private static void AssertHtmlNoStore(HttpResponseData response)
        {
            Assert.That(response.Headers.GetValues("Content-Type"), Is.EqualTo(new[] { "text/html; charset=utf-8" }));
            Assert.That(response.Headers.GetValues("Cache-Control"), Is.EqualTo(new[] { "no-store" }));
            Assert.That(response.Headers.GetValues("Content-Security-Policy"), Is.EqualTo(new[] { "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'" }));
            Assert.That(response.Headers.GetValues("X-Frame-Options"), Is.EqualTo(new[] { "DENY" }));
            Assert.That(response.Headers.GetValues("X-Content-Type-Options"), Is.EqualTo(new[] { "nosniff" }));
            Assert.That(response.Headers.GetValues("Referrer-Policy"), Is.EqualTo(new[] { "no-referrer" }));
        }

        private static void AssertJsonNoStore(HttpResponseData response)
        {
            Assert.That(response.Headers.GetValues("Content-Type"), Is.EqualTo(new[] { "application/json" }));
            Assert.That(response.Headers.GetValues("Cache-Control"), Is.EqualTo(new[] { "no-store" }));
        }
    }
}
