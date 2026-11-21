/// <summary>
/// FileTransferInfoTool
/// </summary>

namespace SafeExchange.Core.Mcp.Tools
{
    using ModelContextProtocol.Protocol;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Describes the REST chunk endpoints so an agent can move large files with its own token. Does no I/O and
    /// never returns a token.
    /// </summary>
    public sealed class FileTransferInfoTool : IMcpTool
    {
        public const string ContentIdPlaceholder = "{contentId}";

        private static readonly JsonElement Schema = McpSchema.Parse($$$"""
            {"type":"object","properties":{"name":{{{McpSchema.SecretName}}},"contentId":{"type":"string","description":"The file's contentId; omit when uploading a new file."},"direction":{"type":"string","enum":["upload","download"]}},"required":["name","direction"]}
            """);

        private readonly McpProtectedResource protectedResource;

        public FileTransferInfoTool(McpProtectedResource protectedResource)
        {
            this.protectedResource = protectedResource ?? throw new ArgumentNullException(nameof(protectedResource));
        }

        public string Name => "file_transfer_info";

        public string Description => "Explains how to upload or download a large file directly over the SafeExchange REST API with your own access token (az CLI, S2S certificate, managed identity): URLs for this server, required headers, the API scope and an example az + curl sequence. Returns no token and moves no data.";

        public JsonElement InputSchema => Schema;

        public ToolAnnotations Annotations => McpToolAnnotations.ReadOnly();

        public Task<McpToolResult> ExecuteAsync(McpToolCall call, CancellationToken cancellationToken)
        {
            var name = call.Arguments.RequiredString("name");
            var contentId = call.Arguments.OptionalString("contentId");
            var direction = call.Arguments.RequiredString("direction");
            if (direction is not ("upload" or "download"))
            {
                throw new McpArgumentException("'direction' must be 'upload' or 'download'.");
            }

            var secretUrl = new Uri(call.ApiBaseUri, $"v2/secret/{Uri.EscapeDataString(name)}").AbsoluteUri;
            var contentUrl = $"{secretUrl}/content/{(string.IsNullOrEmpty(contentId) ? ContentIdPlaceholder : Uri.EscapeDataString(contentId))}";
            var userScope = this.protectedResource.Scopes.FirstOrDefault() ?? string.Empty;
            var tokenCommand = $"TOKEN=$(az account get-access-token --scope \"{userScope}\" --query accessToken -o tsv)";
            var scopes = new
            {
                user = userScope,
                application = this.protectedResource.ApplicationScope,
                note = "Users: request the user scope (the API must allow your client, e.g. the Azure CLI). S2S applications: use the client-credentials flow with the application scope.",
            };

            object data = direction == "upload"
                ? new
                {
                    direction,
                    name,
                    contentId,
                    scopes,
                    steps = UploadSteps(secretUrl, contentUrl),
                    example = string.Join('\n', tokenCommand, UploadExample(contentUrl)),
                }
                : new
                {
                    direction,
                    name,
                    contentId,
                    scopes,
                    steps = DownloadSteps(secretUrl, contentUrl),
                    example = string.Join('\n', tokenCommand, DownloadExample(contentUrl)),
                };

            return Task.FromResult(McpToolResult.Success(
                $"How to {direction} a file of secret '{name}' over REST with your own token.",
                data));
        }

        private static List<object> UploadSteps(string secretUrl, string contentUrl) => new()
        {
            new
            {
                step = "Create the file (skip if you already have a contentId): result.contentName is the contentId.",
                method = "POST",
                url = $"{secretUrl}/content",
                headers = new Dictionary<string, string> { ["Authorization"] = "Bearer <your own token>", ["Content-Type"] = "application/json" },
                body = "{\"contentType\":\"application/octet-stream\",\"fileName\":\"<file name>\",\"isImage\":false}",
            },
            new
            {
                step = "Upload the chunks in order (e.g. 4 MB each). The first response's result.accessTicket is the upload ticket; send it with every later chunk.",
                method = "POST",
                url = $"{contentUrl}/chunk",
                headers = new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer <your own token>",
                    ["Content-Type"] = "application/octet-stream",
                    ["X-SafeExchange-Chunk-Hash"] = "<SHA-256 hex of this chunk>",
                    ["X-SafeExchange-Ticket"] = "<upload ticket; omit on the first chunk>",
                },
                body = "<chunk bytes>",
            },
            new
            {
                step = "Commit: the server compares the whole-file SHA-256 and makes the file available.",
                method = "PATCH",
                url = $"{contentUrl}/commit",
                headers = new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer <your own token>",
                    ["Content-Type"] = "application/json",
                    ["X-SafeExchange-Ticket"] = "<upload ticket>",
                },
                body = "{\"hash\":\"<SHA-256 hex of the whole file>\"}",
            },
        };

        private static List<object> DownloadSteps(string secretUrl, string contentUrl) => new()
        {
            new
            {
                step = "Read the file's SHA-256: result.content[].hash of this contentId.",
                method = "GET",
                url = secretUrl,
                headers = new Dictionary<string, string> { ["Authorization"] = "Bearer <your own token>" },
            },
            new
            {
                step = "Download the whole file and compare its SHA-256.",
                method = "GET",
                url = $"{contentUrl}/all",
                headers = new Dictionary<string, string> { ["Authorization"] = "Bearer <your own token>" },
            },
        };

        private static string UploadExample(string contentUrl) => string.Join('\n',
            "split -b 4m ./file.bin chunk_ && TICKET=\"\"",
            "for f in chunk_*; do",
            "  H=$(sha256sum \"$f\" | cut -d' ' -f1)",
            $"  R=$(curl -sSf -X POST \"{contentUrl}/chunk\" -H \"Authorization: Bearer $TOKEN\" -H \"Content-Type: application/octet-stream\" -H \"X-SafeExchange-Chunk-Hash: $H\" ${{TICKET:+-H \"X-SafeExchange-Ticket: $TICKET\"}} --data-binary @\"$f\")",
            "  TICKET=$(echo \"$R\" | jq -r .result.accessTicket)",
            "done",
            $"curl -sSf -X PATCH \"{contentUrl}/commit\" -H \"Authorization: Bearer $TOKEN\" -H \"X-SafeExchange-Ticket: $TICKET\" -H \"Content-Type: application/json\" -d \"{{\\\"hash\\\":\\\"$(sha256sum ./file.bin | cut -d' ' -f1)\\\"}}\"");

        private static string DownloadExample(string contentUrl) => string.Join('\n',
            $"curl -sSf \"{contentUrl}/all\" -H \"Authorization: Bearer $TOKEN\" -o ./file.bin",
            "sha256sum ./file.bin");
    }
}
