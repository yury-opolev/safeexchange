/// <summary>
/// FileUploadBeginTool
/// </summary>

namespace SafeExchange.Core.Mcp.Tools
{
    using ModelContextProtocol.Protocol;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Services;
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class FileUploadBeginTool : IMcpTool
    {
        public const string DefaultContentType = "application/octet-stream";

        private static readonly JsonElement Schema = McpSchema.Parse($$$"""
            {"type":"object","properties":{"name":{{{McpSchema.SecretName}}},"fileName":{"type":"string","description":"File name shown to users."},"contentType":{"type":"string","description":"MIME type.","default":"application/octet-stream"}},"required":["name","fileName"]}
            """);

        private readonly ContentService contents;

        public FileUploadBeginTool(ContentService contents)
        {
            this.contents = contents ?? throw new ArgumentNullException(nameof(contents));
        }

        public string Name => "file_upload_begin";

        public string Description => "Adds an empty file to a secret (needs Write) and returns its contentId. Then send the bytes with file_upload_chunk (index 0, 1, ...) and finish with file_upload_complete. In-band bytes pass through the model: keep chunks small (at most 256 KB) and use file_transfer_info for files beyond a few MB.";

        public JsonElement InputSchema => Schema;

        public ToolAnnotations Annotations => McpToolAnnotations.Additive();

        public async Task<McpToolResult> ExecuteAsync(McpToolCall call, CancellationToken cancellationToken)
        {
            var name = call.Arguments.RequiredString("name");
            var fileName = call.Arguments.RequiredString("fileName");
            var contentType = call.Arguments.OptionalString("contentType");
            if (string.IsNullOrEmpty(contentType))
            {
                contentType = DefaultContentType;
            }

            var input = new ContentMetadataCreationInput
            {
                FileName = fileName,
                ContentType = contentType,
                IsImage = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
            };
            var result = await this.contents.CreateAsync(call.Caller, name, string.Empty, input);
            if (!result.IsOk)
            {
                return McpToolResult.Failure(result.Outcome);
            }

            var contentId = result.Value.ContentName;
            return McpToolResult.Success(
                $"Added file '{contentId}' to secret '{name}'. Send chunk 0 next.",
                new { name, contentId, isImage = result.Value.IsImage, nextIndex = 0 },
                new { fileName = result.Value.FileName, contentType = result.Value.ContentType });
        }
    }
}
