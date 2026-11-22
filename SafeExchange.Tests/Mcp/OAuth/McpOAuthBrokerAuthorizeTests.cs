/// <summary>
/// McpOAuthBrokerAuthorizeTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;

    [TestFixture]
    public class McpOAuthBrokerAuthorizeTests : McpOAuthBrokerTestBase
    {
        private const string ClientState = "rmcp-csrf-token-123";

        [Test]
        public void Authorize_RedirectsToEntra_WithTheBrokersClientCallbackScopeAndWrappedState()
        {
            var result = this.Broker.Authorize(ApiBase, ValidQuery());

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(result.Body, Is.Null);
            var (target, parameters) = SplitLocation(result.Location);
            Assert.That(target, Is.EqualTo(Tenant + "/oauth2/v2.0/authorize"));
            Assert.That(parameters.Keys, Is.EquivalentTo(new[] { "client_id", "response_type", "redirect_uri", "response_mode", "scope", "code_challenge", "code_challenge_method", "state" }));
            Assert.That(parameters["client_id"], Is.EqualTo(BrokerClientId));
            Assert.That(parameters["response_type"], Is.EqualTo("code"));
            Assert.That(parameters["redirect_uri"], Is.EqualTo(Callback));
            Assert.That(parameters["response_mode"], Is.EqualTo("query"));
            Assert.That(parameters["scope"], Is.EqualTo(ApiScope + " offline_access"));
            Assert.That(parameters["code_challenge"], Is.EqualTo(Challenge));
            Assert.That(parameters["code_challenge_method"], Is.EqualTo("S256"));
            Assert.That(BrokerState.TryDecode(parameters["state"], out var state), Is.True);
            Assert.That(state, Is.EqualTo(new BrokerState(ClientRedirect, ClientState)));
        }

        [Test]
        public void Authorize_DropsResourceAndUnknownParameters_AndReplacesTheRequestedScope()
        {
            var result = this.Broker.Authorize(ApiBase, ValidQuery(
                ("resource", "https://safeexchange.test/api/mcp"),
                ("scope", "openid profile https://graph.microsoft.com/Mail.Read api://other/.default"),
                ("nonce", "n-0S6_WzA2Mj"),
                ("redirect_to", "https://evil.example/")));

            var (_, parameters) = SplitLocation(result.Location);
            Assert.That(parameters.Keys, Does.Not.Contain("resource").And.Not.Contain("nonce").And.Not.Contain("redirect_to"));
            Assert.That(parameters["scope"], Is.EqualTo(ApiScope + " offline_access"));
        }

        [Test]
        public void Authorize_PassesPromptAndLoginHint_OnlyWhenPresent()
        {
            var withHints = SplitLocation(this.Broker.Authorize(ApiBase, ValidQuery(("prompt", "select_account"), ("login_hint", "user@contoso.test"))).Location).Parameters;
            var without = SplitLocation(this.Broker.Authorize(ApiBase, ValidQuery()).Location).Parameters;

            Assert.That(withHints["prompt"], Is.EqualTo("select_account"));
            Assert.That(withHints["login_hint"], Is.EqualTo("user@contoso.test"));
            Assert.That(without.Keys, Does.Not.Contain("prompt").And.Not.Contain("login_hint"));
        }

        [Test]
        public void Authorize_WithoutClientState_WrapsOnlyTheRedirect()
        {
            var query = Query(("client_id", BrokerClientId), ("redirect_uri", ClientRedirect), ("response_type", "code"), ("code_challenge", Challenge), ("code_challenge_method", "S256"));

            var (_, parameters) = SplitLocation(this.Broker.Authorize(ApiBase, query).Location);

            Assert.That(BrokerState.TryDecode(parameters["state"], out var state), Is.True);
            Assert.That(state, Is.EqualTo(new BrokerState(ClientRedirect, null)));
        }

        [TestCase("00000000-0000-0000-0000-0000000000ff")]
        [TestCase("")]
        [TestCase(BrokerClientId + " ")]
        public void Authorize_WithAnUnknownClient_AnswersInvalidClient_WithoutRedirecting(string clientId)
        {
            var query = Query(("client_id", clientId), ("redirect_uri", ClientRedirect), ("response_type", "code"), ("code_challenge", Challenge), ("code_challenge_method", "S256"), ("state", ClientState));

            AssertNoRedirect(this.Broker.Authorize(ApiBase, query), "invalid_client");
        }

        [TestCase("https://evil.example/callback")]
        [TestCase("http://evil.example/callback")]
        [TestCase("http://localhost.evil.com/callback")]
        [TestCase("http://127.0.0.1.nip.io/callback")]
        [TestCase("http://user@127.0.0.1:53682/callback")]
        [TestCase("http://127.0.0.1@evil.example/callback")]
        [TestCase("http://127.0.0.1:53682/callback#fragment")]
        [TestCase("//evil.example/callback")]
        [TestCase("/callback")]
        [TestCase("")]
        public void Authorize_WithANonLoopbackRedirect_AnswersInvalidRequest_WithoutRedirecting(string redirectUri)
        {
            var query = Query(("client_id", BrokerClientId), ("redirect_uri", redirectUri), ("response_type", "code"), ("code_challenge", Challenge), ("code_challenge_method", "S256"), ("state", ClientState));

            AssertNoRedirect(this.Broker.Authorize(ApiBase, query), "invalid_request");
        }

        [Test]
        public void Authorize_WithARepeatedParameter_AnswersInvalidRequest_WithoutRedirecting()
        {
            var query = ValidQuery() + "&" + Query(("redirect_uri", "https://evil.example/callback"));

            AssertNoRedirect(this.Broker.Authorize(ApiBase, query), "invalid_request");
        }

        [Test]
        public void Authorize_WithoutAResponseType_RedirectsInvalidRequestToTheClient()
        {
            var query = Query(("client_id", BrokerClientId), ("redirect_uri", ClientRedirect), ("code_challenge", Challenge), ("code_challenge_method", "S256"), ("state", ClientState));

            AssertClientError(this.Broker.Authorize(ApiBase, query), "invalid_request");
        }

        [TestCase("token", "unsupported_response_type")]
        [TestCase("code id_token", "unsupported_response_type")]
        public void Authorize_WithAnotherResponseType_RedirectsTheErrorToTheClient(string responseType, string error)
        {
            var query = Query(("client_id", BrokerClientId), ("redirect_uri", ClientRedirect), ("response_type", responseType), ("code_challenge", Challenge), ("code_challenge_method", "S256"), ("state", ClientState));

            AssertClientError(this.Broker.Authorize(ApiBase, query), error);
        }

        [TestCase(null, "S256")]
        [TestCase(Challenge, null)]
        [TestCase(Challenge, "plain")]
        [TestCase(Challenge, "s256")]
        [TestCase("too-short", "S256")]
        [TestCase("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw+cM", "S256")]
        public void Authorize_WithoutS256Pkce_RedirectsInvalidRequestToTheClient(string? challenge, string? method)
        {
            var parameters = new List<(string, string)> { ("client_id", BrokerClientId), ("redirect_uri", ClientRedirect), ("response_type", "code"), ("state", ClientState) };
            if (challenge is not null)
            {
                parameters.Add(("code_challenge", challenge));
            }

            if (method is not null)
            {
                parameters.Add(("code_challenge_method", method));
            }

            AssertClientError(this.Broker.Authorize(ApiBase, Query(parameters.ToArray())), "invalid_request");
        }

        [Test]
        public void Authorize_WithAStateThatDoesNotFitIn2KB_RedirectsInvalidRequestToTheClient()
        {
            var query = Query(("client_id", BrokerClientId), ("redirect_uri", ClientRedirect), ("response_type", "code"), ("code_challenge", Challenge), ("code_challenge_method", "S256"), ("state", new string('s', 1600)));

            var result = this.Broker.Authorize(ApiBase, query);

            var (target, parameters) = SplitLocation(result.Location);
            Assert.That(target, Is.EqualTo(ClientRedirect));
            Assert.That(parameters["error"], Is.EqualTo("invalid_request"));
        }

        private static string ValidQuery(params (string Name, string Value)[] extra)
            => Query(new[]
            {
                ("client_id", BrokerClientId),
                ("redirect_uri", ClientRedirect),
                ("response_type", "code"),
                ("code_challenge", Challenge),
                ("code_challenge_method", "S256"),
                ("state", ClientState),
            }.Concat(extra).ToArray());

        private static void AssertNoRedirect(McpOAuthResult result, string error)
        {
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(result.Location, Is.Null);
            Assert.That(Json(result.Body)["error"]!.GetValue<string>(), Is.EqualTo(error));
        }

        private static void AssertClientError(McpOAuthResult result, string error)
        {
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            var (target, parameters) = SplitLocation(result.Location);
            Assert.That(target, Is.EqualTo(ClientRedirect));
            Assert.That(parameters.Keys, Is.EquivalentTo(new[] { "error", "error_description", "state" }));
            Assert.That(parameters["error"], Is.EqualTo(error));
            Assert.That(parameters["state"], Is.EqualTo(ClientState));
        }
    }
}
