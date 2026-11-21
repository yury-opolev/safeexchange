/// <summary>
/// SecretGetTool
/// </summary>

namespace SafeExchange.Core.Mcp.Tools
{
    using ModelContextProtocol.Protocol;
    using SafeExchange.Core.Services;
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class SecretGetTool : IMcpTool
    {
        private static readonly JsonElement Schema = McpSchema.Parse($$$"""
            {"type":"object","properties":{"name":{{{McpSchema.SecretName}}}},"required":["name"]}
            """);

        private readonly SecretService secrets;

        public SecretGetTool(SecretService secrets)
        {
            this.secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        }

        public string Name => "secret_get";

        public string Description => "Shows a secret's metadata: expiration settings, audit flag, tags, and its content items (the text and the attached files) with id, file name, type, size, SHA-256, readiness and chunk count. Does not return the text value.";

        public JsonElement InputSchema => Schema;

        public ToolAnnotations Annotations => McpToolAnnotations.ReadOnly();

        public async Task<McpToolResult> ExecuteAsync(McpToolCall call, CancellationToken cancellationToken)
        {
            var name = call.Arguments.RequiredString("name");
            var result = await this.secrets.GetAsync(call.Caller, name);
            return result.IsOk
                ? McpSecretView.Success($"Secret '{name}'.", result.Value)
                : McpToolResult.Failure(result.Outcome);
        }
    }
}
