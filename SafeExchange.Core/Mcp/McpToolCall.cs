/// <summary>
/// McpToolCall
/// </summary>

namespace SafeExchange.Core.Mcp
{
    using SafeExchange.Core.Callers;
    using System;

    /// <summary>
    /// One <c>tools/call</c>: who calls, with which arguments, and the base URL of this host's REST API
    /// (e.g. <c>https://host/api/</c>), which <c>file_transfer_info</c> needs.
    /// </summary>
    public sealed record McpToolCall(Caller Caller, McpArguments Arguments, Uri ApiBaseUri);
}
