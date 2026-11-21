/// <summary>
/// FileDownloadChunkTool
/// </summary>

namespace SafeExchange.Core.Mcp.Tools
{
    using ModelContextProtocol.Protocol;
    using SafeExchange.Core.Services;
    using System;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class FileDownloadChunkTool : IMcpTool
    {
        private static readonly JsonElement Schema = McpSchema.Parse($$$"""
            {"type":"object","properties":{"name":{{{McpSchema.SecretName}}},"contentId":{"type":"string","description":"The file's contentId (see secret_get)."},"index":{"type":"integer","minimum":0,"description":"Chunk index, from 0 to chunkCount - 1."},"reveal":{"type":"boolean","description":"Required (true) to download the secret's main text content; prefer secret_read_text."}},"required":["name","contentId","index"]}
            """);

        private readonly SecretService secrets;

        private readonly ChunkService chunks;

        public FileDownloadChunkTool(SecretService secrets, ChunkService chunks)
        {
            this.secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
            this.chunks = chunks ?? throw new ArgumentNullException(nameof(chunks));
        }

        public string Name => "file_download_chunk";

        public string Description => "Reads one chunk of a file (needs Read; the read is audited) as base64, with the chunk's SHA-256, the chunk count and the whole file's SHA-256 to verify against. For files beyond a few MB use file_transfer_info instead. The secret's main text content is refused unless reveal=true; prefer secret_read_text for it.";

        public JsonElement InputSchema => Schema;

        public ToolAnnotations Annotations => McpToolAnnotations.ReadOnly();

        public async Task<McpToolResult> ExecuteAsync(McpToolCall call, CancellationToken cancellationToken)
        {
            var name = call.Arguments.RequiredString("name");
            var contentId = call.Arguments.RequiredString("contentId");
            var index = call.Arguments.RequiredNonNegativeInt("index");

            var secret = await this.secrets.GetAsync(call.Caller, name);
            if (!secret.IsOk)
            {
                return McpToolResult.Failure(secret.Outcome);
            }

            var content = secret.Value.Content.FirstOrDefault(c => c.ContentName.Equals(contentId, StringComparison.Ordinal));
            if (content is null)
            {
                return McpToolResult.Failure(ServiceResult.NotFound($"Content '{contentId}' does not exist."));
            }

            if (content.IsMain && !call.Arguments.Flag("reveal"))
            {
                return McpToolResult.Error(
                    McpToolResult.RevealRequiredStatus,
                    $"Content '{contentId}' is the secret's main text value; downloading it puts the value into this conversation and sends it to the model provider. Prefer secret_read_text, or call again with reveal=true.");
            }

            if (index >= content.Chunks.Count)
            {
                return McpToolResult.Failure(ServiceResult.NotFound($"Chunk {index} does not exist; the file has {content.Chunks.Count} chunk(s)."));
            }

            var download = await this.chunks.DownloadChunkAsync(call.Caller, name, contentId, content.Chunks[index].ChunkName);
            if (!download.IsOk)
            {
                return McpToolResult.Failure(download.Outcome);
            }

            using var buffer = new MemoryStream();
            await download.Value.WriteToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();

            return McpToolResult.Success(
                $"Chunk {index} of {content.Chunks.Count} of '{contentId}' ({bytes.Length} bytes).",
                new
                {
                    name,
                    contentId,
                    index,
                    chunkCount = content.Chunks.Count,
                    length = bytes.Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    fileSha256 = string.IsNullOrEmpty(content.Hash) ? null : content.Hash,
                },
                new { data = Convert.ToBase64String(bytes) });
        }
    }
}
