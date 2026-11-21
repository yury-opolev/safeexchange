/// <summary>
/// AccessChangeTool
/// </summary>

namespace SafeExchange.Core.Mcp.Tools
{
    using ModelContextProtocol.Protocol;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Services;
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class AccessChangeTool : IMcpTool
    {
        private static readonly JsonElement Schema = McpSchema.Parse($$$"""
            {"type":"object","properties":{"name":{{{McpSchema.SecretName}}},"grant":{{{McpSchema.SubjectPermissions}}},"revoke":{{{McpSchema.SubjectPermissions}}}},"required":["name"]}
            """);

        private readonly AccessService access;

        public AccessChangeTool(AccessService access)
        {
            this.access = access ?? throw new ArgumentNullException(nameof(access));
        }

        public string Name => "access_change";

        public string Description => "Grants and revokes permissions on a secret in one change, with the same rules as the web app: grant needs GrantAccess, revoke needs RevokeAccess, and only callers holding RevokeAccess can grant it. Each entry names a subject and the permissions (canRead, canWrite, canGrantAccess, canRevokeAccess) to add or remove.";

        public JsonElement InputSchema => Schema;

        public ToolAnnotations Annotations => McpToolAnnotations.Destructive();

        public async Task<McpToolResult> ExecuteAsync(McpToolCall call, CancellationToken cancellationToken)
        {
            var name = call.Arguments.RequiredString("name");
            var grant = call.Arguments.BindProperty<List<SubjectPermissionsInput>>("grant") ?? new();
            var revoke = call.Arguments.BindProperty<List<SubjectPermissionsInput>>("revoke") ?? new();
            if (grant.Count == 0 && revoke.Count == 0)
            {
                throw new McpArgumentException("Give at least one entry in 'grant' or 'revoke'.");
            }

            var result = await this.access.PatchAsync(call.Caller, name, new AccessUpdateInput { Add = grant, Remove = revoke });
            return result.IsOk
                ? McpToolResult.Success(
                    $"Applied {grant.Count} grant(s) and {revoke.Count} revoke(s) on secret '{name}'.",
                    new { name, granted = grant.Count, revoked = revoke.Count })
                : McpToolResult.Failure(result);
        }
    }
}
