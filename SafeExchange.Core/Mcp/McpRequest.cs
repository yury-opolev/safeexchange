/// <summary>
/// McpRequest
/// </summary>

namespace SafeExchange.Core.Mcp
{
    using SafeExchange.Core.Callers;
    using System;

    /// <summary>
    /// One HTTP POST to the MCP endpoint, as a host hands it to <see cref="McpRequestDispatcher"/>: the raw body,
    /// the resolved caller, the base URL of this host's REST API and the <c>MCP-Protocol-Version</c> header.
    /// </summary>
    public sealed record McpRequest(string Body, Caller Caller, Uri ApiBaseUri, string? ProtocolVersion);
}
