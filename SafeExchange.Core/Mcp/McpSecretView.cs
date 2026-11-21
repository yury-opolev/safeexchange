/// <summary>
/// McpSecretView
/// </summary>

namespace SafeExchange.Core.Mcp
{
    using SafeExchange.Core.Model.Dto.Output;
    using System;
    using System.Linq;

    /// <summary>
    /// The MCP shape of a secret's metadata, shared by <c>secret_get</c>, <c>secret_create</c> and
    /// <c>secret_update</c>: identifiers, settings and per-content status are data; tags, file names and content
    /// types (written by users) are untrusted.
    /// </summary>
    public static class McpSecretView
    {
        public static McpToolResult Success(string summary, ObjectMetadataOutput secret)
        {
            ArgumentNullException.ThrowIfNull(secret);

            var content = secret.Content ?? new();
            var data = new
            {
                name = secret.ObjectName,
                auditEnabled = secret.AuditEnabled,
                expirationSettings = secret.ExpirationSettings,
                content = content.Select(c => new
                {
                    contentId = c.ContentName,
                    isMain = c.IsMain,
                    isImage = c.IsImage,
                    isReady = c.IsReady,
                    size = c.Chunks?.Sum(chunk => chunk.Length) ?? 0,
                    chunkCount = c.Chunks?.Count ?? 0,
                    sha256 = string.IsNullOrEmpty(c.Hash) ? null : c.Hash,
                }).ToList(),
            };
            var untrusted = new
            {
                tags = secret.Tags ?? new(),
                files = content
                    .Where(c => !c.IsMain)
                    .Select(c => new { contentId = c.ContentName, fileName = c.FileName, contentType = c.ContentType })
                    .ToList(),
            };

            return McpToolResult.Success(summary, data, untrusted);
        }
    }
}
