/// <summary>
/// SecretListTool
/// </summary>

namespace SafeExchange.Core.Mcp.Tools
{
    using ModelContextProtocol.Protocol;
    using SafeExchange.Core.Services;
    using System;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class SecretListTool : IMcpTool
    {
        private static readonly JsonElement Schema = McpSchema.Parse("""
            {"type":"object","properties":{"nameFilter":{"type":"string","description":"Optional case-insensitive part of the secret name."}}}
            """);

        private readonly SecretService secrets;

        public SecretListTool(SecretService secrets)
        {
            this.secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        }

        public string Name => "secret_list";

        public string Description => "Lists the secrets you can read, with your direct and effective permissions on each.";

        public JsonElement InputSchema => Schema;

        public ToolAnnotations Annotations => McpToolAnnotations.ReadOnly();

        public async Task<McpToolResult> ExecuteAsync(McpToolCall call, CancellationToken cancellationToken)
        {
            var nameFilter = call.Arguments.OptionalString("nameFilter");
            var result = await this.secrets.ListAsync(call.Caller, null);
            if (!result.IsOk)
            {
                return McpToolResult.Failure(result.Outcome);
            }

            var items = result.Value
                .Where(s => string.IsNullOrEmpty(nameFilter) || s.ObjectName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var tagged = items.Where(s => s.Tags.Count > 0).Select(s => new { objectName = s.ObjectName, tags = s.Tags }).ToList();

            return McpToolResult.Success(
                $"{items.Count} secret(s).",
                new
                {
                    count = items.Count,
                    secrets = items.Select(s => new
                    {
                        objectName = s.ObjectName,
                        canRead = s.CanRead,
                        canWrite = s.CanWrite,
                        canGrantAccess = s.CanGrantAccess,
                        canRevokeAccess = s.CanRevokeAccess,
                        callerEffectivePermissions = s.CallerEffectivePermissions,
                    }).ToList(),
                },
                tagged.Count > 0 ? new { tags = tagged } : null);
        }
    }
}
