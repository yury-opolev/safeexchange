/// <summary>
/// SafeWellKnownRoot
/// </summary>

namespace SafeExchange.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using SafeExchange.Core.Functions.Mcp;
    using SafeExchange.Core.Functions.Mcp.OAuth;
    using System;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// OAuth discovery documents at the host root. The platform answers 502 to paths outside the route prefix, and
    /// MCP clients probe the root well-known URLs first (a 5xx there is fatal, a 404 moves on to the next candidate).
    /// The documents are the ones served under <c>/api/.well-known/*</c>, with the same feature gating and the same
    /// <c>https://host/api</c> issuer and resource; every other <c>/.well-known/*</c> path answers a plain 404.
    /// </summary>
    // Anonymous: SafeExchangeFunctionsStartup.IsAuthenticatedHttpTrigger excludes these functions from the auth middleware.
    public class SafeWellKnownRoot
    {
        private readonly McpOAuthHandler oAuthHandler;

        private readonly OAuthProtectedResourceHandler protectedResourceHandler;

        public SafeWellKnownRoot(McpOAuthHandler oAuthHandler, OAuthProtectedResourceHandler protectedResourceHandler)
        {
            this.oAuthHandler = oAuthHandler ?? throw new ArgumentNullException(nameof(oAuthHandler));
            this.protectedResourceHandler = protectedResourceHandler ?? throw new ArgumentNullException(nameof(protectedResourceHandler));
        }

        [Function(McpRoutes.WellKnownAuthorizationServerApiFunctionName)]
        public Task<HttpResponseData> AuthorizationServerApi(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = McpRoutes.WellKnownAuthorizationServerApi)]
            HttpRequestData request)
            => this.oAuthHandler.Metadata(request);

        [Function(McpRoutes.WellKnownOpenIdConfigurationApiFunctionName)]
        public Task<HttpResponseData> OpenIdConfigurationApi(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = McpRoutes.WellKnownOpenIdConfigurationApi)]
            HttpRequestData request)
            => this.oAuthHandler.Metadata(request);

        [Function(McpRoutes.WellKnownProtectedResourceApiMcpFunctionName)]
        public Task<HttpResponseData> ProtectedResourceApiMcp(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = McpRoutes.WellKnownProtectedResourceApiMcp)]
            HttpRequestData request)
            => this.protectedResourceHandler.Run(request);

        // The specific routes above are literal, so the host's route table prefers them over this catch-all.
        [Function(McpRoutes.WellKnownNotFoundFunctionName)]
        public HttpResponseData NotFound(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = McpRoutes.WellKnownNotFound)]
            HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return request.CreateResponse(HttpStatusCode.NotFound);
        }
    }
}
