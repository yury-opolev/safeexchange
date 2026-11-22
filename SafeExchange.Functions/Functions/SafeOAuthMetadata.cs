/// <summary>
/// SafeOAuthMetadata
/// </summary>

namespace SafeExchange.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using SafeExchange.Core.Functions.Mcp;
    using System;
    using System.Threading.Tasks;

    public class SafeOAuthMetadata
    {
        private readonly OAuthProtectedResourceHandler handler;

        public SafeOAuthMetadata(OAuthProtectedResourceHandler handler)
        {
            this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        // Anonymous: SafeExchangeFunctionsStartup.IsAuthenticatedHttpTrigger excludes this function from the auth middleware.
        [Function(McpRoutes.ProtectedResourceMetadataFunctionName)]
        public Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = McpRoutes.ApiRoutePrefix + McpRoutes.ProtectedResourceMetadata)]
            HttpRequestData request)
            => this.handler.Run(request);
    }
}
