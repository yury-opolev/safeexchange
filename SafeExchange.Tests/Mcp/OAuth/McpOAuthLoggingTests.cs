/// <summary>
/// McpOAuthLoggingTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// Runs register, authorize (page, refused and allowed consent), callback (refused, success and Entra error) and token
    /// (code, refresh, Entra error) through the adapter with every log category captured, and checks that no code,
    /// verifier, challenge, token, client state, consent nonce, consent token, signed state or Entra error description
    /// reached a log, while routes and outcomes (refusals at Warning) did.
    /// </summary>
    [TestFixture]
    public class McpOAuthLoggingTests : McpOAuthHttpTestBase
    {
        private const string ClientState = "Client-State-For-The-Log-Test";

        private const string Code = "Authorization-Code-For-The-Log-Test";

        private const string Verifier = "Code-Verifier-For-The-Log-Test-0123456789abcdef";

        private const string Challenge = "Code-Challenge-For-The-Log-Test-0123456789abcd";

        private const string EntraDescription = "AADSTS70008 Description-For-The-Log-Test";

        [Test]
        public async Task AFullSignIn_LogsOutcomesButNoCodeVerifierTokenStateCookieOrNonce()
        {
            var handler = this.OAuthHandler;
            await handler.Register(OAuthRequest("post", "oauth/register", "{\"redirect_uris\":[\"http://127.0.0.1:53682/callback\"]}"));
            var page = await handler.Authorize(OAuthRequest("get",
                $"oauth/authorize?client_id={BrokerClientId}&redirect_uri=http%3A%2F%2F127.0.0.1%3A53682%2Fcallback&response_type=code"
                + $"&code_challenge={Challenge}&code_challenge_method=S256&state={ClientState}"));
            var cookie = CookieFrom(page);
            var nonce = cookie[(ConsentCookie.Name.Length + 1)..];
            var fields = ConsentForm.HiddenFields(BodyOf(page));
            var consentToken = fields.Single(f => f.Key == "consent_token").Value;
            await handler.Authorize(OAuthRequest("post", "oauth/authorize", ConsentForm.Post(fields), "theme=dark"));
            await handler.Callback(OAuthRequest("get", $"oauth/callback?code={Code}&state=forged"));
            var allow = await handler.Authorize(OAuthRequest("post", "oauth/authorize", ConsentForm.Post(fields), cookie));
            Assert.That(OAuthParameters.TryParse(new Uri(allow.Headers.GetValues("Location").Single()).Query, out var entraParameters), Is.True);
            var signedState = entraParameters!["state"];
            await handler.Callback(OAuthRequest("get", $"oauth/callback?code={Code}&state={signedState}"));
            var callback = await handler.Callback(OAuthRequest("get", $"oauth/callback?code={Code}&state={signedState}", cookie: cookie));
            await handler.Callback(OAuthRequest("get", $"oauth/callback?error=access_denied&error_description={Uri.EscapeDataString(EntraDescription)}&state={signedState}", cookie: cookie));
            await handler.Token(OAuthRequest("post", "oauth/token", $"grant_type=authorization_code&code={Code}&code_verifier={Verifier}&client_id={BrokerClientId}"));
            await handler.Token(OAuthRequest("post", "oauth/token", $"grant_type=refresh_token&refresh_token={FakeEntraTokenClient.RefreshToken}&client_id={BrokerClientId}"));
            this.Entra.Response = new EntraTokenResponse(HttpStatusCode.BadRequest, $"{{\"error\":\"invalid_grant\",\"error_description\":\"{EntraDescription}\"}}");
            await handler.Token(OAuthRequest("post", "oauth/token", $"grant_type=refresh_token&refresh_token={FakeEntraTokenClient.RefreshToken}&client_id={BrokerClientId}"));
            Assert.That(callback.Headers.GetValues("Location").Single(), Does.Contain(Code), "Arrange: the flow moved the code.");

            var logs = string.Join('\n', this.Logs.Entries);
            Assert.That(logs, Does.Contain("MCP OAuth register answered 201"));
            Assert.That(logs, Does.Contain("MCP OAuth authorize answered the consent page."));
            Assert.That(logs, Does.Contain("Warning SafeExchange.Core.Mcp.OAuth.McpOAuthBroker: MCP OAuth authorize refused the request: missing_cookie."));
            Assert.That(logs, Does.Contain("Warning SafeExchange.Core.Mcp.OAuth.McpOAuthBroker: MCP OAuth callback refused the request: invalid_state."));
            Assert.That(logs, Does.Contain("Warning SafeExchange.Core.Mcp.OAuth.McpOAuthBroker: MCP OAuth callback refused the request: missing_cookie."));
            Assert.That(logs, Does.Contain("MCP OAuth authorize redirected to Entra."));
            Assert.That(logs, Does.Contain("MCP OAuth callback forwarded the authorization code to the client."));
            Assert.That(logs, Does.Contain("MCP OAuth callback forwarded Entra error access_denied to the client."));
            Assert.That(logs, Does.Contain("MCP OAuth token (authorization_code) relayed Entra status 200"));
            Assert.That(logs, Does.Contain("MCP OAuth token (refresh_token) relayed Entra status 400 (error: invalid_grant)."));
            foreach (var forbidden in new[] { Code, Verifier, Challenge, ClientState, nonce, consentToken, signedState, FakeEntraTokenClient.AccessToken, FakeEntraTokenClient.RefreshToken, EntraDescription })
            {
                Assert.That(logs, Does.Not.Contain(forbidden), $"'{forbidden}' was logged.");
            }
        }    }
}
