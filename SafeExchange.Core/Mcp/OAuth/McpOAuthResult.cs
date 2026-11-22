/// <summary>
/// McpOAuthResult
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System.Net;
    using System.Text.Json.Nodes;

    /// <summary>
    /// A host-agnostic broker response: a status, an optional JSON body and an optional redirect <see cref="Location"/>.
    /// </summary>
    public sealed record McpOAuthResult(HttpStatusCode StatusCode, string? Body = null, string? Location = null)
    {
        public static McpOAuthResult NotFound { get; } = new(HttpStatusCode.NotFound);

        public static McpOAuthResult Json(HttpStatusCode statusCode, string body) => new(statusCode, body);

        public static McpOAuthResult Redirect(string location) => new(HttpStatusCode.Found, null, location);

        public static McpOAuthResult Error(HttpStatusCode statusCode, string error, string description)
            => Json(statusCode, new JsonObject { ["error"] = error, ["error_description"] = description }.ToJsonString());
    }
}
