/// <summary>
/// SecretReadTextTool
/// </summary>

namespace SafeExchange.Core.Mcp.Tools
{
    using ModelContextProtocol.Protocol;
    using SafeExchange.Core.Services;
    using System;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class SecretReadTextTool : IMcpTool
    {
        private static readonly JsonElement Schema = McpSchema.Parse($$$"""
            {"type":"object","properties":{"name":{{{McpSchema.SecretName}}},"reveal":{"type":"boolean","description":"Must be true: the value is put into this conversation and sent to the model provider."}},"required":["name","reveal"]}
            """);

        private readonly SecretService secrets;

        private readonly ChunkService chunks;

        public SecretReadTextTool(SecretService secrets, ChunkService chunks)
        {
            this.secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
            this.chunks = chunks ?? throw new ArgumentNullException(nameof(chunks));
        }

        public string Name => "secret_read_text";

        public string Description => "Reads a secret's text value (needs Read; the read is audited). Returns it as plain text and as the stored HTML. Requires reveal=true because the value enters the conversation.";

        public JsonElement InputSchema => Schema;

        public ToolAnnotations Annotations => McpToolAnnotations.ReadOnly();

        public async Task<McpToolResult> ExecuteAsync(McpToolCall call, CancellationToken cancellationToken)
        {
            var name = call.Arguments.RequiredString("name");
            if (!call.Arguments.Flag("reveal"))
            {
                return McpToolResult.Error(
                    McpToolResult.RevealRequiredStatus,
                    $"Reading secret '{name}' puts its value into this conversation and sends it to the model provider. Call again with reveal=true to read it.");
            }

            var secret = await this.secrets.GetAsync(call.Caller, name);
            if (!secret.IsOk)
            {
                return McpToolResult.Failure(secret.Outcome);
            }

            var main = secret.Value.Content.FirstOrDefault(c => c.IsMain);
            if (main is null || main.Chunks.Count == 0)
            {
                return McpToolResult.Success($"Secret '{name}' has no text.", new { name, hasText = false });
            }

            var download = await this.chunks.DownloadContentAsync(call.Caller, name, main.ContentName);
            if (!download.IsOk)
            {
                return McpToolResult.Failure(download.Outcome);
            }

            using var buffer = new MemoryStream();
            await download.Value.WriteToAsync(buffer, cancellationToken);
            var html = Encoding.UTF8.GetString(buffer.ToArray());

            return McpToolResult.Success(
                $"Text of secret '{name}'.",
                new { name, hasText = true },
                new { text = MainContentText.ToText(html), html });
        }
    }
}
