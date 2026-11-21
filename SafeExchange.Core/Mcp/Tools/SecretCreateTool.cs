/// <summary>
/// SecretCreateTool
/// </summary>

namespace SafeExchange.Core.Mcp.Tools
{
    using ModelContextProtocol.Protocol;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Services;
    using System;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class SecretCreateTool : IMcpTool
    {
        private static readonly JsonElement Schema = McpSchema.Parse($$$"""
            {"type":"object","properties":{"name":{{{McpSchema.SecretName}}},"expirationSettings":{{{McpSchema.ExpirationSettings}}},"tags":{{{McpSchema.Tags}}},"auditEnabled":{"type":"boolean","description":"Keep a tamper-evident audit trail for this secret. Cannot be changed later.","default":false},"text":{"type":"string","description":"Optional text value, stored as the secret's main content (plain text; line breaks are kept)."}},"required":["name","expirationSettings"]}
            """);

        private readonly SecretService secrets;

        private readonly ChunkService chunks;

        public SecretCreateTool(SecretService secrets, ChunkService chunks)
        {
            this.secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
            this.chunks = chunks ?? throw new ArgumentNullException(nameof(chunks));
        }

        public string Name => "secret_create";

        public string Description => "Creates a secret owned by you (full permissions), with the same settings as the SafeExchange web app, and optionally stores a text value in it. Attach files afterwards with file_upload_begin.";

        public JsonElement InputSchema => Schema;

        public ToolAnnotations Annotations => McpToolAnnotations.Additive();

        public async Task<McpToolResult> ExecuteAsync(McpToolCall call, CancellationToken cancellationToken)
        {
            var name = call.Arguments.RequiredString("name");
            var text = call.Arguments.OptionalString("text");
            var input = call.Arguments.Bind<MetadataCreationInput>();

            var created = await this.secrets.CreateAsync(call.Caller, name, input);
            if (!created.IsOk)
            {
                return McpToolResult.Failure(created.Outcome);
            }

            if (text is null)
            {
                return McpSecretView.Success($"Created secret '{name}'.", created.Value);
            }

            var mainContentId = created.Value.Content.First(c => c.IsMain).ContentName;
            using var data = new MemoryStream(Encoding.UTF8.GetBytes(MainContentText.ToHtml(text)));
            var stored = await this.chunks.UploadChunkAsync(call.Caller, name, mainContentId, data, new ChunkUploadRequest());
            if (!stored.IsOk)
            {
                return McpToolResult.Error(stored.Status.ToStatusString(), $"Secret '{name}' was created, but its text was not stored: {stored.Error}");
            }

            var secret = await this.secrets.GetAsync(call.Caller, name);
            return secret.IsOk
                ? McpSecretView.Success($"Created secret '{name}' with its text.", secret.Value)
                : McpToolResult.Failure(secret.Outcome);
        }
    }
}
