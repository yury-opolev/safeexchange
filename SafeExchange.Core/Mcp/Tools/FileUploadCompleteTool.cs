/// <summary>
/// FileUploadCompleteTool
/// </summary>

namespace SafeExchange.Core.Mcp.Tools
{
    using ModelContextProtocol.Protocol;
    using SafeExchange.Core.Services;
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class FileUploadCompleteTool : IMcpTool
    {
        private static readonly JsonElement Schema = McpSchema.Parse($$$"""
            {"type":"object","properties":{"name":{{{McpSchema.SecretName}}},"contentId":{"type":"string","description":"From file_upload_begin."},"sha256":{"type":"string","description":"SHA-256 of the whole file, 64 hex characters."},"ticket":{"type":"string","description":"The ticket returned by the chunk uploads."}},"required":["name","contentId","sha256","ticket"]}
            """);

        private readonly ContentService contents;

        public FileUploadCompleteTool(ContentService contents)
        {
            this.contents = contents ?? throw new ArgumentNullException(nameof(contents));
        }

        public string Name => "file_upload_complete";

        public string Description => "Finishes a file upload (needs Write): the server compares the whole-file SHA-256 with what it received and makes the file available.";

        public JsonElement InputSchema => Schema;

        public ToolAnnotations Annotations => McpToolAnnotations.Additive();

        public async Task<McpToolResult> ExecuteAsync(McpToolCall call, CancellationToken cancellationToken)
        {
            var name = call.Arguments.RequiredString("name");
            var contentId = call.Arguments.RequiredString("contentId");
            var sha256 = call.Arguments.RequiredString("sha256");
            var ticket = call.Arguments.RequiredString("ticket");

            var result = await this.contents.CommitAsync(call.Caller, name, contentId, ticket, new ContentCommitInput(sha256));
            return result.IsOk
                ? McpToolResult.Success($"File '{contentId}' is complete and verified.", new { name, contentId, sha256 = result.Value.Hash })
                : McpToolResult.Failure(result.Outcome);
        }
    }
}
