/// <summary>
/// McpResponse
/// </summary>

namespace SafeExchange.Core.Mcp
{
    using System.Net;

    /// <summary>
    /// What a host writes back: the status code, an <c>application/json</c> body (none for <c>202</c>) and, after
    /// <c>initialize</c>, the <c>Mcp-Session-Id</c> header value.
    /// </summary>
    public sealed record McpResponse(HttpStatusCode StatusCode, string? Body, string? SessionId = null)
    {
        public const string ContentType = "application/json";

        public static McpResponse Accepted { get; } = new(HttpStatusCode.Accepted, null);
    }
}
