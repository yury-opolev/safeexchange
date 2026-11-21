/// <summary>
/// UnauthorizedResponse
/// </summary>

namespace SafeExchange.Core.Middleware
{
    using Microsoft.Azure.Functions.Worker.Http;
    using SafeExchange.Core.Functions.Mcp;
    using System;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// The 401 the authentication middlewares write. With the MCP endpoint enabled it also carries an RFC 9728
    /// challenge, <c>WWW-Authenticate: Bearer resource_metadata="…"</c>, that points MCP clients to the
    /// protected-resource metadata of this host; the body is the same either way.
    /// </summary>
    public static class UnauthorizedResponse
    {
        public const string ChallengeHeaderName = "WWW-Authenticate";

        public static async Task<HttpResponseData> CreateAsync(HttpRequestData request, string errorMessage, bool includeResourceMetadata)
        {
            ArgumentNullException.ThrowIfNull(request);

            var response = request.CreateResponse();
            response.StatusCode = HttpStatusCode.Unauthorized;
            if (includeResourceMetadata)
            {
                response.Headers.Add(ChallengeHeaderName, $"Bearer resource_metadata=\"{MetadataUrl(request.Url)}\"");
            }

            await response.WriteAsJsonAsync(new BaseResponseObject<object> { Status = "unauthorized", Error = errorMessage });
            return response;
        }

        public static string MetadataUrl(Uri requestUrl)
        {
            ArgumentNullException.ThrowIfNull(requestUrl);
            return new Uri(requestUrl, McpRoutes.ProtectedResourceMetadataPath).AbsoluteUri;
        }
    }
}
