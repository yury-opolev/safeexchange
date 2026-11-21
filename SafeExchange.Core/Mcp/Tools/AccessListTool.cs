/// <summary>
/// AccessListTool
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

    public sealed class AccessListTool : IMcpTool
    {
        private static readonly JsonElement Schema = McpSchema.Parse($$$"""
            {"type":"object","properties":{"name":{{{McpSchema.SecretName}}}},"required":["name"]}
            """);

        private readonly AccessService access;

        public AccessListTool(AccessService access)
        {
            this.access = access ?? throw new ArgumentNullException(nameof(access));
        }

        public string Name => "access_list";

        public string Description => "Lists who can access a secret and with which permissions (needs Read), plus your own effective permissions. subjectType: 0 = user, 1 = group, 100 = application.";

        public JsonElement InputSchema => Schema;

        public ToolAnnotations Annotations => McpToolAnnotations.ReadOnly();

        public async Task<McpToolResult> ExecuteAsync(McpToolCall call, CancellationToken cancellationToken)
        {
            var name = call.Arguments.RequiredString("name");
            var result = await this.access.ListAsync(call.Caller, name);
            if (!result.IsOk)
            {
                return McpToolResult.Failure(result.Outcome);
            }

            var entries = result.Value.AccessList;
            return McpToolResult.Success(
                $"{entries.Count} access entr{(entries.Count == 1 ? "y" : "ies")} on secret '{name}'.",
                new { name, count = entries.Count, callerEffectivePermissions = result.Value.CallerEffectivePermissions },
                new
                {
                    accessList = entries.Select(e => new
                    {
                        subjectType = e.SubjectType,
                        subjectName = e.SubjectName,
                        subjectId = e.SubjectId,
                        canRead = e.CanRead,
                        canWrite = e.CanWrite,
                        canGrantAccess = e.CanGrantAccess,
                        canRevokeAccess = e.CanRevokeAccess,
                    }).ToList(),
                });
        }
    }
}
