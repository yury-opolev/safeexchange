/// <summary>
/// McpOAuthBrokerCallbackTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Net;
    using System.Text;

    [TestFixture]
    public class McpOAuthBrokerCallbackTests : McpOAuthBrokerTestBase
    {
        private const string Code = "1.AQEA-fake-entra-authorization-code";

        private const string Nonce = "bm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm8";

        [Test]
        public void Callback_ForwardsTheCodeAndTheClientsState_ToTheLoopbackRedirect_AndExpiresTheCookie()
        {
            var (state, nonce) = this.AllowConsent(ValidQuery());
            var query = Query(("code", Code), ("state", state), ("session_state", "00000000-0000-0000-0000-0000000000aa"), ("iss", "https://login.microsoftonline.com/x/v2.0"));

            var result = this.Broker.Callback(query, nonce);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(result.Body, Is.Null);
            Assert.That(result.Location, Is.EqualTo($"{ClientRedirect}?code={Uri.EscapeDataString(Code)}&state={ClientState}"));
            Assert.That(result.Cookie, Is.EqualTo(ConsentCookie.Expire));
        }

        [Test]
        public void Callback_AppendsToARedirectThatHasAQuery_AndOmitsAMissingClientState()
        {
            var result = this.Broker.Callback(Query(("code", Code), ("state", Sign("http://localhost:8080/cb?session=7", null))), Nonce);

            Assert.That(result.Location, Is.EqualTo($"http://localhost:8080/cb?session=7&code={Uri.EscapeDataString(Code)}"));
        }

        [Test]
        public void Callback_ForwardsAnEntraError_WithOnlyErrorDescriptionAndState()
        {
            var query = Query(
                ("error", "access_denied"),
                ("error_description", "AADSTS65004: User declined to consent."),
                ("error_uri", "https://login.microsoftonline.com/error?code=65004"),
                ("correlation_id", "00000000-0000-0000-0000-0000000000cc"),
                ("state", Sign(ClientRedirect, "csrf-2")));

            var result = this.Broker.Callback(query, Nonce);

            var (target, parameters) = SplitLocation(result.Location);
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(result.Body, Is.Null);
            Assert.That(target, Is.EqualTo(ClientRedirect));
            Assert.That(parameters.Keys, Is.EquivalentTo(new[] { "error", "error_description", "state" }));
            Assert.That(parameters["error"], Is.EqualTo("access_denied"));
            Assert.That(parameters["error_description"], Is.EqualTo("AADSTS65004: User declined to consent."));
            Assert.That(parameters["state"], Is.EqualTo("csrf-2"));
            Assert.That(result.Cookie, Is.EqualTo(ConsentCookie.Expire));
        }

        [Test]
        public void Callback_WithNeitherCodeNorError_RedirectsServerErrorToTheClient()
        {
            var (target, parameters) = SplitLocation(this.Broker.Callback(Query(("state", Sign(ClientRedirect, "csrf-3"))), Nonce).Location);

            Assert.That(target, Is.EqualTo(ClientRedirect));
            Assert.That(parameters["error"], Is.EqualTo("server_error"));
            Assert.That(parameters["state"], Is.EqualTo("csrf-3"));
        }

        [TestCase(null)]
        [TestCase("not-a-state")]
        [TestCase("eyJyIjoiaHR0cDovLzEyNy4wLjAuMToxL2NiIiwicyI6IngifQ")]
        public void Callback_WithoutAValidState_IsTheInvalidPage(string? state)
        {
            var query = state is null ? Query(("code", Code)) : Query(("code", Code), ("state", state));

            AssertInvalidPage(this.Broker.Callback(query, Nonce));
        }

        [TestCase("https://evil.example/callback")]
        [TestCase("http://localhost.evil.com/callback")]
        [TestCase("http://127.0.0.1.nip.io/callback")]
        public void Callback_WithACraftedUnsignedState_NeverRedirects(string redirectUri)
        {
            var crafted = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"r\":\"{redirectUri}\",\"s\":\"x\",\"n\":\"{Nonce}\",\"e\":4102444800}}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            AssertInvalidPage(this.Broker.Callback(Query(("code", Code), ("state", crafted)), Nonce));
            AssertInvalidPage(this.Broker.Callback(Query(("code", Code), ("state", crafted + ".AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")), Nonce));
        }

        [Test]
        public void Callback_WithARepeatedState_IsTheInvalidPage()
        {
            var query = Query(("code", Code), ("state", Sign(ClientRedirect, "a")), ("state", Sign("http://localhost:1/other", "b")));

            AssertInvalidPage(this.Broker.Callback(query, Nonce));
        }

        [Test]
        public void Callback_WithoutTheCookie_OrWithAnotherNonce_IsTheInvalidPage()
        {
            var query = Query(("code", Code), ("state", Sign(ClientRedirect, "csrf-4")));

            AssertInvalidPage(this.Broker.Callback(query, null));
            AssertInvalidPage(this.Broker.Callback(query, "short"));
            AssertInvalidPage(this.Broker.Callback(query, ConsentToken.NewNonce()));
        }

        [Test]
        public void Callback_WithAnExpiredStateOrOneSignedWithAnotherKey_IsTheInvalidPage()
        {
            Assert.That(SignedBrokerState.TryCreate(McpOAuthTestKey.Bytes, ClientRedirect, "csrf-5", Nonce, DateTimeProvider.UtcNow.AddMinutes(-10), out var expired), Is.True);
            Assert.That(SignedBrokerState.TryCreate(new byte[32], ClientRedirect, "csrf-5", Nonce, DateTimeProvider.UtcNow, out var otherKey), Is.True);

            AssertInvalidPage(this.Broker.Callback(Query(("code", Code), ("state", expired!)), Nonce));
            AssertInvalidPage(this.Broker.Callback(Query(("code", Code), ("state", otherKey!)), Nonce));
        }

        private static string Sign(string redirectUri, string? clientState)
        {
            Assert.That(SignedBrokerState.TryCreate(McpOAuthTestKey.Bytes, redirectUri, clientState, Nonce, DateTimeProvider.UtcNow, out var encoded), Is.True);
            return encoded!;
        }
    }
}
