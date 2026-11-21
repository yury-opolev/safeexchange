/// <summary>
/// McpLoggingTests
/// </summary>

namespace SafeExchange.Tests.Mcp
{
    using NUnit.Framework;
    using System;
    using System.Linq;
    using System.Net;
    using System.Net.Http.Headers;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json.Nodes;
    using System.Threading.Tasks;

    /// <summary>
    /// Runs a whole MCP session through the handler with every log category captured, and checks that no
    /// secret value, chunk data, token or raw session id reached a log, while tool names, secret names and
    /// statuses did.
    /// </summary>
    [TestFixture]
    public class McpLoggingTests : McpHttpTestBase
    {
        private const string SecretValue = "S3cret-Value-For-The-Log-Test";

        private const string ChunkPayload = "Chunk-Payload-For-The-Log-Test";

        private const string Token = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJsb2ctdGVzdCJ9.c2lnbmF0dXJl";

        [Test]
        public async Task AFullSession_LogsNoSecretValueChunkDataTokenOrSessionId()
        {
            var initialize = await this.PostAsync(Rpc("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "copilot-cli", version = "1.0.114" } }), null);
            var sessionId = initialize.SessionId;
            var chunk = Encoding.UTF8.GetBytes(ChunkPayload);
            var chunkBase64 = Convert.ToBase64String(chunk);
            var chunkHash = Convert.ToHexString(SHA256.HashData(chunk)).ToLowerInvariant();

            await this.ToolAsync(sessionId, "secret_create", new { name = "alpha", expirationSettings = Expiration(), auditEnabled = true, text = SecretValue });
            await this.ToolAsync(sessionId, "secret_read_text", new { name = "alpha", reveal = true });
            var begun = await this.ToolAsync(sessionId, "file_upload_begin", new { name = "alpha", fileName = "log.bin" });
            var contentId = begun["contentId"]!.GetValue<string>();
            var stored = await this.ToolAsync(sessionId, "file_upload_chunk", new { name = "alpha", contentId, index = 0, data = chunkBase64, sha256 = chunkHash });
            await this.ToolAsync(sessionId, "file_upload_complete", new { name = "alpha", contentId, sha256 = chunkHash, ticket = stored["ticket"]!.GetValue<string>() });
            var downloaded = await this.ToolAsync(sessionId, "file_download_chunk", new { name = "alpha", contentId, index = 0 });
            Assert.That(downloaded["untrusted"]!["data"]!.GetValue<string>(), Is.EqualTo(chunkBase64), "Arrange: the session moved the data.");

            var logs = string.Join('\n', this.Logs.Entries);
            Assert.That(logs, Does.Contain("MCP tool secret_create on secret 'alpha' finished with ok"));
            Assert.That(logs, Does.Contain("MCP tool file_download_chunk on secret 'alpha' finished with ok"));
            foreach (var forbidden in new[] { SecretValue, ChunkPayload, chunkBase64, Token, sessionId! })
            {
                Assert.That(logs, Does.Not.Contain(forbidden), $"'{forbidden}' was logged.");
            }
        }

        private async Task<(HttpStatusCode StatusCode, string? SessionId, JsonObject? Message)> PostAsync(string body, string? sessionId)
        {
            var request = HttpRequest("post", body, sessionId);
            request.Headers.Add("Authorization", new AuthenticationHeaderValue("Bearer", Token).ToString());
            var response = await this.Handler.Run(request, UserPrincipal(OwnerUpn), this.Logs.CreateLogger("SafeExchange.Functions.SafeMcp"));
            var text = BodyOf(response);
            var session = response.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.Single() : null;
            return (response.StatusCode, session, text.Length == 0 ? null : JsonNode.Parse(text)!.AsObject());
        }

        private async Task<JsonObject> ToolAsync(string? sessionId, string tool, object arguments)
        {
            var (statusCode, _, message) = await this.PostAsync(Rpc("tools/call", new { name = tool, arguments }), sessionId);
            Assert.That(statusCode, Is.EqualTo(HttpStatusCode.OK));
            return Ok(message!["result"]!.AsObject());
        }
    }
}
