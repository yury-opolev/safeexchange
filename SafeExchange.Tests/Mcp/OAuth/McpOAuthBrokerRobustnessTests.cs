/// <summary>
/// McpOAuthBrokerRobustnessTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Threading.Tasks;

    /// <summary>
    /// Malformed JSON never escapes as an exception, Entra's token answer is relayed only when it is a JSON object with
    /// status 200, 400 or 401, the Entra call gate turns away calls beyond its cap, and refusals log at Warning.
    /// </summary>
    [TestFixture]
    public class McpOAuthBrokerRobustnessTests : McpOAuthBrokerTestBase
    {
        private static readonly string RefreshBody = $"grant_type=refresh_token&refresh_token={FakeEntraTokenClient.RefreshToken}&client_id={BrokerClientId}";

        [TestCase("{\"redirect_uris\":[\"http://127.0.0.1/\\uD800\"]}", "invalid_redirect_uri")]
        [TestCase("{\"redirect_uris\":[\"http://127.0.0.1/cb\"],\"token_endpoint_auth_method\":\"\\uDC00\"}", "invalid_client_metadata")]
        [TestCase("{\"redirect_uris\":[\"http://127.0.0.1/cb\"],\"grant_types\":[\"\\uD800\"]}", "invalid_client_metadata")]
        public void Register_WithAnInvalidUtf16Escape_IsRefused(string body, string error)
        {
            var result = this.Broker.Register(ApiBase, body);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(Json(result.Body)["error"]!.GetValue<string>(), Is.EqualTo(error));
        }

        [TestCase(HttpStatusCode.OK, "{\"access_token\":\"a\"}")]
        [TestCase(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}")]
        [TestCase(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_client\"}")]
        [TestCase(HttpStatusCode.BadRequest, "{\"error\":\"\\uD800\"}")]
        public async Task AJsonObjectWith200_400Or401_IsRelayedUnchanged(HttpStatusCode status, string body)
        {
            this.Entra.Response = new EntraTokenResponse(status, body);

            var result = await this.Broker.TokenAsync(ApiBase, RefreshBody);

            Assert.That(result, Is.EqualTo(McpOAuthResult.Json(status, body)));
        }

        [TestCase(HttpStatusCode.InternalServerError, "{\"error\":\"server_error\"}")]
        [TestCase(HttpStatusCode.TooManyRequests, "{\"error\":\"temporarily_unavailable\"}")]
        [TestCase(HttpStatusCode.Forbidden, "{\"error\":\"access_denied\"}")]
        [TestCase(HttpStatusCode.Found, "")]
        [TestCase(HttpStatusCode.OK, "<html><body>Sign in to the proxy</body></html>")]
        [TestCase(HttpStatusCode.OK, "[]")]
        [TestCase(HttpStatusCode.BadRequest, "")]
        public async Task AnythingElse_IsABadGateway_AndLogsTheStatus(HttpStatusCode status, string body)
        {
            this.Entra.Response = new EntraTokenResponse(status, body);

            var result = await this.Broker.TokenAsync(ApiBase, RefreshBody);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(Json(result.Body)["error"]!.GetValue<string>(), Is.EqualTo("server_error"));
            Assert.That(this.Logs.Entries, Has.One.StartsWith(
                $"Warning SafeExchange.Core.Mcp.OAuth.McpOAuthBroker: MCP OAuth token (refresh_token) got an unexpected Entra response with status {(int)status}."));
        }

        [Test]
        public async Task WhenEveryEntraCallSlotIsBusy_TheTokenRequestIsTemporarilyUnavailable()
        {
            var broker = this.CreateBroker(gate: new EntraCallGate(1, TimeSpan.FromMilliseconds(10)));
            var hold = new TaskCompletionSource();
            this.Entra.Hold = hold.Task;
            var first = broker.TokenAsync(ApiBase, RefreshBody);

            var second = await broker.TokenAsync(ApiBase, RefreshBody);

            Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(Json(second.Body)["error"]!.GetValue<string>(), Is.EqualTo("temporarily_unavailable"));
            Assert.That(second.RetryAfterSeconds, Is.EqualTo(2));
            Assert.That(this.Logs.Entries, Has.Some.StartsWith("Warning SafeExchange.Core.Mcp.OAuth.McpOAuthBroker: MCP OAuth token (refresh_token) found every Entra call slot busy"));
            hold.SetResult();
            Assert.That((await first).StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(this.Entra.Requests, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task AFailedEntraCall_FreesItsSlot()
        {
            var broker = this.CreateBroker(gate: new EntraCallGate(1, TimeSpan.FromMilliseconds(10)));
            this.Entra.Failure = new HttpRequestException("No such host is known.");
            Assert.That((await broker.TokenAsync(ApiBase, RefreshBody)).StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));

            this.Entra.Failure = null;

            Assert.That((await broker.TokenAsync(ApiBase, RefreshBody)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public async Task AnUnreachableEntra_LogsTheRequestErrorAndTheInnerExceptionType()
        {
            this.Entra.Failure = new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known. (login.microsoftonline.com:443)", new SocketException(11001));
            await this.Broker.TokenAsync(ApiBase, RefreshBody);
            this.Entra.Failure = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.", new TimeoutException());
            await this.Broker.TokenAsync(ApiBase, RefreshBody);

            var logs = string.Join('\n', this.Logs.Entries);
            Assert.That(logs, Does.Contain("Warning SafeExchange.Core.Mcp.OAuth.McpOAuthBroker: MCP OAuth token (refresh_token) could not reach Entra: HttpRequestException (NameResolutionError, inner SocketException)."));
            Assert.That(logs, Does.Contain("Warning SafeExchange.Core.Mcp.OAuth.McpOAuthBroker: MCP OAuth token (refresh_token) could not reach Entra: TaskCanceledException (none, inner TimeoutException)."));
            Assert.That(logs, Does.Not.Contain("login.microsoftonline.com:443"));
        }

        [Test]
        public void RefusalsAndRedirectedErrors_LogAtWarning()
        {
            this.Broker.Register(ApiBase, "not json");
            this.Broker.Authorize(ApiBase, ValidQuery().Replace("response_type=code", "response_type=token"));

            Assert.That(this.Logs.Entries, Has.Some.StartsWith("Warning SafeExchange.Core.Mcp.OAuth.McpOAuthBroker: MCP OAuth register refused the request with invalid_client_metadata."));
            Assert.That(this.Logs.Entries, Has.Some.StartsWith("Warning SafeExchange.Core.Mcp.OAuth.McpOAuthBroker: MCP OAuth authorize redirected unsupported_response_type to the client."));
            Assert.That(this.Logs.Entries.Where(e => e.StartsWith("Information", StringComparison.Ordinal)), Is.Empty);
        }
    }
}
