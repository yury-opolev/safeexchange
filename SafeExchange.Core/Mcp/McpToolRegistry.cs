/// <summary>
/// McpToolRegistry
/// </summary>

namespace SafeExchange.Core.Mcp
{
    using ModelContextProtocol.Protocol;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;

    /// <summary>
    /// The tools the endpoint offers, by name, in registration order.
    /// </summary>
    public sealed class McpToolRegistry
    {
        private readonly Dictionary<string, IMcpTool> toolsByName;

        public McpToolRegistry(IEnumerable<IMcpTool> tools)
        {
            ArgumentNullException.ThrowIfNull(tools);

            this.Tools = tools.ToList();
            this.toolsByName = new Dictionary<string, IMcpTool>(StringComparer.Ordinal);
            foreach (var tool in this.Tools)
            {
                if (!this.toolsByName.TryAdd(tool.Name, tool))
                {
                    throw new ArgumentException($"Duplicate MCP tool name '{tool.Name}'.", nameof(tools));
                }
            }
        }

        public IReadOnlyList<IMcpTool> Tools { get; }

        public bool TryGet(string name, [NotNullWhen(true)] out IMcpTool? tool)
            => this.toolsByName.TryGetValue(name, out tool);

        public ListToolsResult ToListToolsResult() => new()
        {
            Tools = this.Tools
                .Select(tool => new Tool
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    InputSchema = tool.InputSchema,
                    Annotations = tool.Annotations,
                })
                .ToList(),
        };
    }
}
