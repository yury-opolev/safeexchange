/// <summary>
/// McpToolAnnotations
/// </summary>

namespace SafeExchange.Core.Mcp
{
    using ModelContextProtocol.Protocol;

    /// <summary>
    /// Behaviour hints for clients. Every tool works on SafeExchange only (closed world).
    /// </summary>
    public static class McpToolAnnotations
    {
        public static ToolAnnotations ReadOnly() => new() { ReadOnlyHint = true, OpenWorldHint = false };

        public static ToolAnnotations Additive() => new() { ReadOnlyHint = false, DestructiveHint = false, OpenWorldHint = false };

        public static ToolAnnotations Destructive() => new() { ReadOnlyHint = false, DestructiveHint = true, OpenWorldHint = false };
    }
}
