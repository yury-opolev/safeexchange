/// <summary>
/// SecretDeleteTool
/// </summary>

namespace SafeExchange.Core.Mcp.Tools
{
    using ModelContextProtocol.Protocol;
    using SafeExchange.Core.Services;
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class SecretDeleteTool : IMcpTool
    {
        private static readonly JsonElement Schema = McpSchema.Parse($$$"""
            {"type":"object","properties":{"name":{{{McpSchema.SecretName}}},"confirm":{"type":"boolean","description":"Must be true; deleting cannot be undone."}},"required":["name","confirm"]}
            """);

        private readonly SecretService secrets;

        public SecretDeleteTool(SecretService secrets)
        {
            this.secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        }

        public string Name => "secret_delete";

        public string Description => "Deletes a secret with its text and files for everyone (needs Write). Requires confirm=true.";

        public JsonElement InputSchema => Schema;

        public ToolAnnotations Annotations => McpToolAnnotations.Destructive();

        public async Task<McpToolResult> ExecuteAsync(McpToolCall call, CancellationToken cancellationToken)
        {
            var name = call.Arguments.RequiredString("name");
            if (!call.Arguments.Flag("confirm"))
            {
                return McpToolResult.Error(
                    McpToolResult.ConfirmationRequiredStatus,
                    $"Deleting secret '{name}' removes it, its text and its files for everyone. Call again with confirm=true to delete it.");
            }

            var result = await this.secrets.DeleteAsync(call.Caller, name);
            return result.IsOk
                ? McpToolResult.Success($"Deleted secret '{name}'.", new { name, deleted = true })
                : McpToolResult.Failure(result);
        }
    }
}
