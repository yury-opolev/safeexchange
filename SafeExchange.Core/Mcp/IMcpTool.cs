/// <summary>
/// IMcpTool
/// </summary>

namespace SafeExchange.Core.Mcp
{
    using ModelContextProtocol.Protocol;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// One MCP tool. Implementations are host-agnostic: they call the SafeExchange services with the caller of
    /// the request, so permission checks, validation and audit are exactly REST's.
    /// </summary>
    public interface IMcpTool
    {
        string Name { get; }

        string Description { get; }

        /// <summary>
        /// JSON Schema (an object schema) of the tool's arguments.
        /// </summary>
        JsonElement InputSchema { get; }

        ToolAnnotations Annotations { get; }

        /// <summary>
        /// Runs the tool. Throws <see cref="McpArgumentException"/> for arguments that do not match the schema.
        /// </summary>
        Task<McpToolResult> ExecuteAsync(McpToolCall call, CancellationToken cancellationToken);
    }
}
