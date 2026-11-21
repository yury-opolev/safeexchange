/// <summary>
/// FileUploadChunkTool
/// </summary>

namespace SafeExchange.Core.Mcp.Tools
{
    using ModelContextProtocol.Protocol;
    using SafeExchange.Core.Services;
    using System;
    using System.IO;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class FileUploadChunkTool : IMcpTool
    {
        private static readonly Regex Sha256Hex = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

        private static readonly JsonElement Schema = McpSchema.Parse($$$"""
            {"type":"object","properties":{"name":{{{McpSchema.SecretName}}},"contentId":{"type":"string","description":"From file_upload_begin."},"index":{"type":"integer","minimum":0,"description":"0 for the first chunk, then 1, 2, ... Re-sending a stored chunk with the same sha256 is a no-op."},"data":{"type":"string","contentEncoding":"base64","description":"The chunk bytes, base64."},"sha256":{"type":"string","description":"SHA-256 of this chunk's bytes, 64 hex characters."},"ticket":{"type":"string","description":"The ticket returned by the previous chunk; omit for chunk 0."}},"required":["name","contentId","index","data","sha256"]}
            """);

        private readonly ChunkService chunks;

        public FileUploadChunkTool(ChunkService chunks)
        {
            this.chunks = chunks ?? throw new ArgumentNullException(nameof(chunks));
        }

        public string Name => "file_upload_chunk";

        public string Description => "Stores the next chunk of a file started with file_upload_begin (needs Write). The server checks the chunk's SHA-256 and returns the upload ticket to pass with the next chunk and to file_upload_complete. On 'Expected chunk index n.' resume at index n.";

        public JsonElement InputSchema => Schema;

        public ToolAnnotations Annotations => McpToolAnnotations.Additive();

        public async Task<McpToolResult> ExecuteAsync(McpToolCall call, CancellationToken cancellationToken)
        {
            var name = call.Arguments.RequiredString("name");
            var contentId = call.Arguments.RequiredString("contentId");
            var index = call.Arguments.RequiredNonNegativeInt("index");
            var sha256 = call.Arguments.RequiredString("sha256");
            if (!Sha256Hex.IsMatch(sha256))
            {
                throw new McpArgumentException("'sha256' must be 64 hex characters.");
            }

            var bytes = DecodeBase64(call.Arguments.RequiredString("data"));
            var request = new ChunkUploadRequest
            {
                AccessTicket = call.Arguments.OptionalString("ticket") ?? string.Empty,
                ChunkHash = sha256.ToLowerInvariant(),
                DeclaredLength = null,
                ExpectedIndex = index,
            };

            using var data = new MemoryStream(bytes);
            var result = await this.chunks.UploadChunkAsync(call.Caller, name, contentId, data, request);
            if (!result.IsOk)
            {
                return McpToolResult.Failure(result.Outcome);
            }

            var chunk = result.Value;
            return McpToolResult.Success(
                $"Stored chunk {index} of '{contentId}' ({chunk.Length} bytes). Send chunk {index + 1} next, or finish with file_upload_complete.",
                new { name, contentId, index, sha256 = chunk.Hash, length = chunk.Length, ticket = chunk.AccessTicket, nextIndex = index + 1 });
        }

        private static byte[] DecodeBase64(string data)
        {
            try
            {
                return Convert.FromBase64String(data);
            }
            catch (FormatException)
            {
                throw new McpArgumentException("'data' must be base64.");
            }
        }
    }
}
