/// <summary>
/// McpOAuthBrokerConsentTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;

    /// <summary>
    /// The consent form post: allow redirects to Entra with a signed state bound to the consent cookie, deny redirects
    /// <c>access_denied</c> to the client, and anything that does not match the cookie, the token or the parameters is
    /// the 400 page without a redirect.
    /// </summary>
    [TestFixture]
    public class McpOAuthBrokerConsentTests : McpOAuthBrokerTestBase
    {
        [Test]
        public void Allow_RedirectsToEntra_WithTheBrokersClientCallbackScopeAndSignedState()
        {
            var (fields, nonce) = this.OpenConsent(ValidQuery());

            var result = this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields), nonce);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(result.Body, Is.Null);
            Assert.That(result.Cookie, Is.EqualTo(ConsentCookie.Issue(nonce)), "The cookie is re-issued with the same nonce so that it lives as long as the signed state.");
            Assert.That(result.Cookie!.MaxAgeSeconds, Is.EqualTo(600));
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
            Assert.That(SignedBrokerState.TryVerify(McpOAuthTestKey.Bytes, parameters["state"], DateTimeProvider.UtcNow, out var state), Is.True);
            Assert.That((state!.RedirectUri, state.ClientState, state.Nonce), Is.EqualTo((ClientRedirect, ClientState, nonce)));
            Assert.That(this.Entra.Requests, Is.Empty);
        }

        [TestCase("none")]
        [TestCase("select_account")]
        [TestCase("consent")]
        public void Allow_ForwardsLoginHint_ButNeverPrompt_AndReplacesTheRequestedScope(string prompt)
        {
            var (fields, nonce) = this.OpenConsent(ValidQuery(
                ("prompt", prompt),
                ("login_hint", "user@contoso.test"),
                ("scope", "openid profile https://graph.microsoft.com/Mail.Read api://other/.default"),
                ("resource", "https://safeexchange.test/api/mcp")));
            var posted = fields.Append(new KeyValuePair<string, string>("prompt", prompt));

            var (_, parameters) = SplitLocation(this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(posted), nonce).Location);

            Assert.That(parameters.Keys, Does.Not.Contain("prompt").And.Not.Contain("resource"));
            Assert.That(parameters["login_hint"], Is.EqualTo("user@contoso.test"));
            Assert.That(parameters["scope"], Is.EqualTo(ApiScope + " offline_access"));
        }

        [Test]
        public void Deny_RedirectsAccessDeniedToTheClient_AndExpiresTheCookie()
        {
            var (fields, nonce) = this.OpenConsent(ValidQuery());

            var result = this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields, "deny"), nonce);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            var (target, parameters) = SplitLocation(result.Location);
            Assert.That(target, Is.EqualTo(ClientRedirect));
            Assert.That(parameters.Keys, Is.EquivalentTo(new[] { "error", "error_description", "state" }));
            Assert.That(parameters["error"], Is.EqualTo("access_denied"));
            Assert.That(parameters["state"], Is.EqualTo(ClientState));
            Assert.That(result.Cookie, Is.EqualTo(ConsentCookie.Expire));
        }

        [Test]
        public void WithoutTheCookie_OrWithAnotherSignInsCookie_IsTheInvalidPage()
        {
            var (fields, _) = this.OpenConsent(ValidQuery());
            var (_, otherNonce) = this.OpenConsent(ValidQuery());

            AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields), null));
            AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields), "not-a-nonce"));
            AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields), otherNonce));
        }

        [TestCase("state", "attacker-state")]
        [TestCase("redirect_uri", "http://127.0.0.1:1/other")]
        [TestCase("code_challenge", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
        [TestCase("consent_expires", "4102444800")]
        [TestCase("consent_token", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
        public void ATamperedField_IsTheInvalidPage(string name, string value)
        {
            var (fields, nonce) = this.OpenConsent(ValidQuery());

            AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields.With(name, value)), nonce));
        }

        [Test]
        public void AnAddedLoginHint_IsTheInvalidPage()
        {
            var (fields, nonce) = this.OpenConsent(ValidQuery());

            AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields.Append(new KeyValuePair<string, string>("login_hint", "victim@contoso.test"))), nonce));
        }

        [TestCase("client_id", "00000000-0000-0000-0000-0000000000ff")]
        [TestCase("redirect_uri", "https://evil.example/cb")]
        [TestCase("response_type", "token")]
        [TestCase("code_challenge_method", "plain")]
        public void AnInvalidParameter_IsTheInvalidPage_AndNeverRedirected(string name, string value)
        {
            var (fields, nonce) = this.OpenConsent(ValidQuery());

            AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields.With(name, value)), nonce));
        }

        [TestCase(null)]
        [TestCase("maybe")]
        public void AMissingOrUnknownDecision_IsTheInvalidPage(string? decision)
        {
            var (fields, nonce) = this.OpenConsent(ValidQuery());

            AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields, decision), nonce));
        }

        [Test]
        public void ARepeatedEmptyOrOversizeForm_IsTheInvalidPage()
        {
            var (fields, nonce) = this.OpenConsent(ValidQuery());
            var form = ConsentForm.Post(fields);

            AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, form + "&state=other", nonce));
            AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, string.Empty, nonce));
            AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, null, nonce));
            AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, form + "&pad=" + new string('p', McpOAuthBroker.MaxBodyLength), nonce));
        }

        [Test]
        public void AConsentPageOlderThanTenMinutes_IsTheInvalidPage()
        {
            var (useSpecified, specified) = (DateTimeProvider.UseSpecifiedDateTime, DateTimeProvider.SpecifiedDateTime);
            try
            {
                DateTimeProvider.SpecifiedDateTime = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
                DateTimeProvider.UseSpecifiedDateTime = true;
                var (fields, nonce) = this.OpenConsent(ValidQuery());

                DateTimeProvider.SpecifiedDateTime = DateTimeProvider.SpecifiedDateTime.AddSeconds(599);
                Assert.That(this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields), nonce).StatusCode, Is.EqualTo(HttpStatusCode.Found));
                DateTimeProvider.SpecifiedDateTime = DateTimeProvider.SpecifiedDateTime.AddSeconds(1);
                AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields), nonce));
            }
            finally
            {
                (DateTimeProvider.UseSpecifiedDateTime, DateTimeProvider.SpecifiedDateTime) = (useSpecified, specified);
            }
        }
    }
}
