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
        [Test]
        public void Authorize_AnswersTheConsentPage_AndSetsTheConsentCookie_WithoutContactingEntra()
        {
            var result = this.Broker.Authorize(ApiBase, ValidQuery());

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(result.Location, Is.Null);
            Assert.That(result.ContentType, Is.EqualTo("text/html; charset=utf-8"));
            Assert.That(result.Cookie!.MaxAgeSeconds, Is.EqualTo(600));
            Assert.That(ConsentToken.IsNonce(result.Cookie.Value), Is.True);
            Assert.That(result.Body, Does.Not.Contain(result.Cookie.Value), "The nonce travels only in the cookie.");
            Assert.That(result.Body, Does.Contain("<form method=\"post\" action=\"https://safeexchange.test/api/oauth/authorize\">"));
            Assert.That(result.Body, Does.Contain("It will receive the result at <b>http://127.0.0.1:53682</b>."));
            var fields = ConsentForm.HiddenFields(result.Body);
            Assert.That(fields.Select(f => f.Key), Is.EqualTo(new[] { "client_id", "redirect_uri", "response_type", "code_challenge", "code_challenge_method", "state", "consent_expires", "consent_token" }));
            Assert.That(fields.Take(6).Select(f => f.Value), Is.EqualTo(new[] { BrokerClientId, ClientRedirect, "code", Challenge, "S256", ClientState }));
            Assert.That(this.Entra.Requests, Is.Empty);
        }

        [Test]
        public void Authorize_FormActionOrigins_AreTheEntraAuthorityAndTheValidatedRedirectOrigin()
        {
            var result = this.Broker.Authorize(ApiBase, ValidQuery());

            Assert.That(result.FormActionOrigins, Is.EqualTo(new[] { "https://login.microsoftonline.com", "http://127.0.0.1:53682" }));
        }

        [TestCase("http://localhost:4000/cb?x=1;script-src%20*", "http://localhost:4000")]
        [TestCase("http://[::1]:4000/cb", "http://[::1]:4000")]
        [TestCase("http://127.0.0.1/cb", "http://127.0.0.1")]
        [TestCase("http://LOCALHOST:4000/cb", "http://localhost:4000")]
        public void Authorize_ClientOrigin_IsOnlySchemeHostAndPort_WhateverTheRestOfTheRedirectHolds(string redirectUri, string expected)
        {
            var query = Query(("client_id", BrokerClientId), ("redirect_uri", redirectUri), ("response_type", "code"), ("code_challenge", Challenge), ("code_challenge_method", "S256"));

            var result = this.Broker.Authorize(ApiBase, query);

            Assert.That(result.FormActionOrigins, Is.EqualTo(new[] { "https://login.microsoftonline.com", expected }));
            Assert.That(result.FormActionOrigins, Has.All.Match(@"^https?://(\[[0-9a-f:]+\]|[a-z0-9.\-]+)(:[0-9]{1,5})?$"));
            Assert.That(ConsentPage.ContentSecurityPolicy(result.FormActionOrigins), Does.Contain($"form-action 'self' https://login.microsoftonline.com {expected}; frame-ancestors"));
        }

        [Test]
        public void TheAuthorityOrigin_KeepsANonDefaultPort_AndDropsThePath()
        {
            var broker = this.CreateBroker(authority: "https://login.example:8443/tenant-1/v2.0");

            var result = broker.Authorize(ApiBase, ValidQuery());

            Assert.That(result.FormActionOrigins, Is.EqualTo(new[] { "https://login.example:8443", "http://127.0.0.1:53682" }));
        }

        [Test]
        public void EveryOtherAnswer_HasNoFormActionOrigins()
        {
            Assert.That(this.Broker.Authorize(ApiBase, "client_id=x").FormActionOrigins, Is.Empty);
            Assert.That(this.Broker.AuthorizeConsent(ApiBase, "x=1", null).FormActionOrigins, Is.Empty);
            Assert.That(this.Broker.Callback(ApiBase, "code=c", null).FormActionOrigins, Is.Empty);
        }

        [Test]
        public void Authorize_KeepsScopeAndLoginHintInTheForm_ButNeverPromptOrUnknownParameters()
        {
            var (fields, _) = this.OpenConsent(ValidQuery(
                ("prompt", "none"),
                ("login_hint", "user@contoso.test"),
                ("scope", "openid profile"),
                ("resource", "https://safeexchange.test/api/mcp"),
                ("nonce", "n-0S6_WzA2Mj"),
                ("redirect_to", "https://evil.example/")));

            Assert.That(fields.Select(f => f.Key), Is.EqualTo(new[] { "client_id", "redirect_uri", "response_type", "code_challenge", "code_challenge_method", "state", "scope", "login_hint", "consent_expires", "consent_token" }));
            Assert.That(fields.Single(f => f.Key == "scope").Value, Is.EqualTo("openid profile"));
            Assert.That(fields.Single(f => f.Key == "login_hint").Value, Is.EqualTo("user@contoso.test"));
        }

        [Test]
        public void Authorize_WithoutClientState_LeavesItOutOfTheForm()
        {
            var query = Query(("client_id", BrokerClientId), ("redirect_uri", ClientRedirect), ("response_type", "code"), ("code_challenge", Challenge), ("code_challenge_method", "S256"));

            var (fields, _) = this.OpenConsent(query);

            Assert.That(fields.Select(f => f.Key), Does.Not.Contain("state"));
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
