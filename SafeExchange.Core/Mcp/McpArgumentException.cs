/// <summary>
/// McpArgumentException
/// </summary>

namespace SafeExchange.Core.Mcp
{
    using System;

    /// <summary>
    /// A tool's arguments do not match its input schema. The dispatcher answers it as a tool error
    /// (<c>invalid_arguments</c>) so the model can correct the call.
    /// </summary>
    public sealed class McpArgumentException : Exception
    {
        public McpArgumentException(string message)
            : base(message)
        {
        }
    }
}
