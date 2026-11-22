/// <summary>
/// McpOAuthBrokerCallbackTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Net;
    using System.Text;

    [TestFixture]
    public class McpOAuthBrokerCallbackTests : McpOAuthBrokerTestBase
    {
        private const string Code = "1.AQEA-fake-entra-authorization-code";

        [Test]
        public void Callback_ForwardsTheCodeAndTheClientsState_ToTheLoopbackRedirect()
        {
            var query = Query(("code", Code), ("state", Wrap(ClientRedirect, "csrf-1")), ("session_state", "00000000-0000-0000-0000-0000000000aa"), ("iss", "https://login.microsoftonline.com/x/v2.0"));

            var result = this.Broker.Callback(query);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(result.Body, Is.Null);
            Assert.That(result.Location, Is.EqualTo($"{ClientRedirect}?code={Uri.EscapeDataString(Code)}&state=csrf-1"));
        }

        [Test]
        public void Callback_AppendsToARedirectThatHasAQuery_AndOmitsAMissingClientState()
        {
            var result = this.Broker.Callback(Query(("code", Code), ("state", Wrap("http://localhost:8080/cb?session=7", null))));

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
                ("state", Wrap(ClientRedirect, "csrf-2")));

            var result = this.Broker.Callback(query);

            var (target, parameters) = SplitLocation(result.Location);
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(result.Body, Is.Null);
            Assert.That(target, Is.EqualTo(ClientRedirect));
            Assert.That(parameters.Keys, Is.EquivalentTo(new[] { "error", "error_description", "state" }));
            Assert.That(parameters["error"], Is.EqualTo("access_denied"));
            Assert.That(parameters["error_description"], Is.EqualTo("AADSTS65004: User declined to consent."));
            Assert.That(parameters["state"], Is.EqualTo("csrf-2"));
        }

        [Test]
        public void Callback_WithNeitherCodeNorError_RedirectsServerErrorToTheClient()
        {
            var (target, parameters) = SplitLocation(this.Broker.Callback(Query(("state", Wrap(ClientRedirect, "csrf-3")))).Location);

            Assert.That(target, Is.EqualTo(ClientRedirect));
            Assert.That(parameters["error"], Is.EqualTo("server_error"));
            Assert.That(parameters["state"], Is.EqualTo("csrf-3"));
        }

        [TestCase(null)]
        [TestCase("not-a-state")]
        [TestCase("eyJyIjoiaHR0cHM6Ly9ldmlsLmV4YW1wbGUvY2IifQ")]
        public void Callback_WithoutAValidState_AnswersBadRequest_WithoutRedirecting(string? state)
        {
            var query = state is null ? Query(("code", Code)) : Query(("code", Code), ("state", state));

            var result = this.Broker.Callback(query);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(result.Location, Is.Null);
            Assert.That(Json(result.Body)["error"]!.GetValue<string>(), Is.EqualTo("invalid_request"));
        }

        [TestCase("https://evil.example/callback")]
        [TestCase("http://localhost.evil.com/callback")]
        [TestCase("http://127.0.0.1.nip.io/callback")]
        public void Callback_WithACraftedNonLoopbackState_NeverRedirects(string redirectUri)
        {
            var crafted = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"r\":\"{redirectUri}\",\"s\":\"x\"}}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            var result = this.Broker.Callback(Query(("code", Code), ("state", crafted)));

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(result.Location, Is.Null);
        }

        [Test]
        public void Callback_WithARepeatedState_AnswersBadRequest_WithoutRedirecting()
        {
            var query = Query(("code", Code), ("state", Wrap(ClientRedirect, "a")), ("state", Wrap("http://localhost:1/other", "b")));

            var result = this.Broker.Callback(query);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(result.Location, Is.Null);
        }

        private static string Wrap(string redirectUri, string? clientState)
        {
            Assert.That(new BrokerState(redirectUri, clientState).TryEncode(out var encoded), Is.True);
            return encoded!;
        }
    }
}
