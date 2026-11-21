/// <summary>
/// FileDeleteTool
/// </summary>

namespace SafeExchange.Core.Mcp.Tools
{
    using ModelContextProtocol.Protocol;
    using SafeExchange.Core.Services;
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class FileDeleteTool : IMcpTool
    {
        private static readonly JsonElement Schema = McpSchema.Parse($$$"""
            {"type":"object","properties":{"name":{{{McpSchema.SecretName}}},"contentId":{"type":"string","description":"The file's contentId (see secret_get)."},"confirm":{"type":"boolean","description":"Must be true; deleting cannot be undone."}},"required":["name","contentId","confirm"]}
            """);

        private readonly ContentService contents;

        public FileDeleteTool(ContentService contents)
        {
            this.contents = contents ?? throw new ArgumentNullException(nameof(contents));
        }

        public string Name => "file_delete";

        public string Description => "Deletes a file from a secret for everyone (needs Write; the text cannot be deleted this way). Requires confirm=true.";

        public JsonElement InputSchema => Schema;

        public ToolAnnotations Annotations => McpToolAnnotations.Destructive();

        public async Task<McpToolResult> ExecuteAsync(McpToolCall call, CancellationToken cancellationToken)
        {
            var name = call.Arguments.RequiredString("name");
            var contentId = call.Arguments.RequiredString("contentId");
            if (!call.Arguments.Flag("confirm"))
            {
                return McpToolResult.Error(
                    McpToolResult.ConfirmationRequiredStatus,
                    $"Deleting file '{contentId}' of secret '{name}' removes it for everyone. Call again with confirm=true to delete it.");
            }

            var result = await this.contents.DeleteAsync(call.Caller, name, contentId);
            return result.IsOk
                ? McpToolResult.Success($"Deleted file '{contentId}' from secret '{name}'.", new { name, contentId, deleted = true })
                : McpToolResult.Failure(result);
        }
    }
}
