/// <summary>
/// AuditReadTool
/// </summary>

namespace SafeExchange.Core.Mcp.Tools
{
    using ModelContextProtocol.Protocol;
    using SafeExchange.Core.Services;
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class AuditReadTool : IMcpTool
    {
        private static readonly JsonElement Schema = McpSchema.Parse($$$"""
            {"type":"object","properties":{"name":{{{McpSchema.SecretName}}},"from":{"type":"string","format":"date-time","description":"Inclusive lower bound (UTC)."},"to":{"type":"string","format":"date-time","description":"Exclusive upper bound (UTC)."},"pageSize":{"type":"integer","minimum":1,"maximum":500,"default":100},"continuation":{"type":"string","description":"nextContinuation of the previous page."},"direction":{"type":"string","enum":["asc","desc"],"default":"asc"}},"required":["name"]}
            """);

        private readonly AuditQueryService audit;

        public AuditReadTool(AuditQueryService audit)
        {
            this.audit = audit ?? throw new ArgumentNullException(nameof(audit));
        }

        public string Name => "audit_read";

        public string Description => "Reads one page of a secret's audit trail (needs Read; only for secrets created with auditEnabled). Consecutive reads of the same file are merged into one event.";

        public JsonElement InputSchema => Schema;

        public ToolAnnotations Annotations => McpToolAnnotations.ReadOnly();

        public async Task<McpToolResult> ExecuteAsync(McpToolCall call, CancellationToken cancellationToken)
        {
            var name = call.Arguments.RequiredString("name");
            var direction = call.Arguments.OptionalString("direction");
            if (direction is not (null or "asc" or "desc"))
            {
                throw new McpArgumentException("'direction' must be 'asc' or 'desc'.");
            }

            var query = new AuditPageQuery
            {
                From = call.Arguments.OptionalUtcDateTime("from"),
                To = call.Arguments.OptionalUtcDateTime("to"),
                PageSize = call.Arguments.OptionalNonNegativeInt("pageSize") ?? AuditPageQuery.DefaultPageSize,
                Continuation = call.Arguments.OptionalString("continuation"),
                Descending = direction == "desc",
            };

            var result = await this.audit.ReadAsync(call.Caller, name, query);
            if (!result.IsOk)
            {
                return McpToolResult.Failure(result.Outcome);
            }

            var page = result.Value;
            if (!page.AuditEnabled)
            {
                return McpToolResult.Success($"Secret '{name}' has no audit trail.", new { name, auditEnabled = false, count = 0 });
            }

            return McpToolResult.Success(
                $"{page.Events.Count} audit event(s) of secret '{name}'{(page.NextContinuation is null ? "." : "; pass nextContinuation for more.")}",
                new { name, auditEnabled = true, count = page.Events.Count, nextContinuation = page.NextContinuation },
                new { events = page.Events });
        }
    }
}
