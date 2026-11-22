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
    /// Runs register, authorize, callback (success and Entra error) and token (code, refresh, Entra error) through the
    /// adapter with every log category captured, and checks that no code, verifier, challenge, token, client state,
    /// wrapped state or Entra error description reached a log, while routes and outcomes did.
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
        public async Task AFullSignIn_LogsOutcomesButNoCodeVerifierTokenOrState()
        {
            var handler = this.OAuthHandler;
            await handler.Register(OAuthRequest("post", "oauth/register", "{\"redirect_uris\":[\"http://127.0.0.1:53682/callback\"]}"));
            var authorize = await handler.Authorize(OAuthRequest("get",
                $"oauth/authorize?client_id={BrokerClientId}&redirect_uri=http%3A%2F%2F127.0.0.1%3A53682%2Fcallback&response_type=code"
                + $"&code_challenge={Challenge}&code_challenge_method=S256&state={ClientState}"));
            var entraLocation = authorize.Headers.GetValues("Location").Single();
            Assert.That(OAuthParameters.TryParse(new Uri(entraLocation).Query, out var entraParameters), Is.True);
            var wrappedState = entraParameters!["state"];
            var callback = await handler.Callback(OAuthRequest("get", $"oauth/callback?code={Code}&state={wrappedState}"));
            await handler.Callback(OAuthRequest("get", $"oauth/callback?error=access_denied&error_description={Uri.EscapeDataString(EntraDescription)}&state={wrappedState}"));
            await handler.Token(OAuthRequest("post", "oauth/token", $"grant_type=authorization_code&code={Code}&code_verifier={Verifier}&client_id={BrokerClientId}"));
            await handler.Token(OAuthRequest("post", "oauth/token", $"grant_type=refresh_token&refresh_token={FakeEntraTokenClient.RefreshToken}"));
            this.Entra.Response = new EntraTokenResponse(HttpStatusCode.BadRequest, $"{{\"error\":\"invalid_grant\",\"error_description\":\"{EntraDescription}\"}}");
            await handler.Token(OAuthRequest("post", "oauth/token", $"grant_type=refresh_token&refresh_token={FakeEntraTokenClient.RefreshToken}"));
            Assert.That(callback.Headers.GetValues("Location").Single(), Does.Contain(Code), "Arrange: the flow moved the code.");

            var logs = string.Join('\n', this.Logs.Entries);
            Assert.That(logs, Does.Contain("MCP OAuth register answered 201"));
            Assert.That(logs, Does.Contain("MCP OAuth authorize redirected to Entra."));
            Assert.That(logs, Does.Contain("MCP OAuth callback forwarded the authorization code to the client."));
            Assert.That(logs, Does.Contain("MCP OAuth callback forwarded Entra error access_denied to the client."));
            Assert.That(logs, Does.Contain("MCP OAuth token (authorization_code) relayed Entra status 200"));
            Assert.That(logs, Does.Contain("MCP OAuth token (refresh_token) relayed Entra status 400 (error: invalid_grant)."));
            foreach (var forbidden in new[] { Code, Verifier, Challenge, ClientState, wrappedState, FakeEntraTokenClient.AccessToken, FakeEntraTokenClient.RefreshToken, EntraDescription })
            {
                Assert.That(logs, Does.Not.Contain(forbidden), $"'{forbidden}' was logged.");
            }
        }
    }
}
