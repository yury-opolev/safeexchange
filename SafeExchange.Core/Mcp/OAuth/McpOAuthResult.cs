/// <summary>
/// McpOAuthResult
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System.Net;
    using System.Text.Json.Nodes;

    /// <summary>
    /// A host-agnostic broker response: a status, an optional body (JSON unless <see cref="ContentType"/> says HTML), an
    /// optional redirect <see cref="Location"/>, an optional <see cref="ConsentCookie"/> to set and an optional
    /// <c>Retry-After</c> in seconds.
    /// </summary>
    public sealed record McpOAuthResult(HttpStatusCode StatusCode, string? Body = null, string? Location = null)
    {
        public const string JsonContentType = "application/json";

        public const string HtmlContentType = "text/html; charset=utf-8";

        public static McpOAuthResult NotFound { get; } = new(HttpStatusCode.NotFound);

        public string ContentType { get; init; } = JsonContentType;

        public ConsentCookie? Cookie { get; init; }

        public int? RetryAfterSeconds { get; init; }

        public bool IsHtml => this.ContentType == HtmlContentType;

        public static McpOAuthResult Json(HttpStatusCode statusCode, string body) => new(statusCode, body);

        public static McpOAuthResult Html(HttpStatusCode statusCode, string html) => new(statusCode, html) { ContentType = HtmlContentType };

        public static McpOAuthResult Redirect(string location) => new(HttpStatusCode.Found, null, location);

        public static McpOAuthResult Error(HttpStatusCode statusCode, string error, string description)
            => Json(statusCode, new JsonObject { ["error"] = error, ["error_description"] = description }.ToJsonString());
    }
}
