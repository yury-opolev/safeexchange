/// <summary>
/// WhoAmITool
/// </summary>

namespace SafeExchange.Core.Mcp.Tools
{
    using ModelContextProtocol.Protocol;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class WhoAmITool : IMcpTool
    {
        private static readonly JsonElement Schema = McpSchema.Parse("""{"type":"object","properties":{}}""");

        public string Name => "whoami";

        public string Description => "Shows who you are to SafeExchange: subject type (User or Application), subject id (the id permissions use), display name, channel and MCP client label.";

        public JsonElement InputSchema => Schema;

        public ToolAnnotations Annotations => McpToolAnnotations.ReadOnly();

        public Task<McpToolResult> ExecuteAsync(McpToolCall call, CancellationToken cancellationToken)
        {
            var caller = call.Caller;
            return Task.FromResult(McpToolResult.Success(
                $"You are {caller.SubjectType} '{caller.SubjectId}'.",
                new { subjectType = caller.SubjectType.ToString(), subjectId = caller.SubjectId, channel = caller.Channel },
                new { displayName = caller.DisplayName, clientLabel = caller.ClientLabel }));
        }
    }
}
