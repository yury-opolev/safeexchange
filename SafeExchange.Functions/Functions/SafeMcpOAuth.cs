/// <summary>
/// SafeMcpOAuth
/// </summary>

namespace SafeExchange.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using SafeExchange.Core.Functions.Mcp;
    using SafeExchange.Core.Functions.Mcp.OAuth;
    using System;
    using System.Threading.Tasks;

    // Anonymous: SafeExchangeFunctionsStartup.IsAuthenticatedHttpTrigger excludes these functions from the auth middleware.
    public class SafeMcpOAuth
    {
        private readonly McpOAuthHandler handler;

        public SafeMcpOAuth(McpOAuthHandler handler)
        {
            this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        [Function(McpRoutes.OAuthAuthorizationServerMetadataFunctionName)]
        public Task<HttpResponseData> AuthorizationServerMetadata(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = McpRoutes.ApiRoutePrefix + McpRoutes.OAuthAuthorizationServerMetadata)]
            HttpRequestData request)
            => this.handler.Metadata(request);

        [Function(McpRoutes.OAuthOpenIdConfigurationFunctionName)]
        public Task<HttpResponseData> OpenIdConfiguration(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = McpRoutes.ApiRoutePrefix + McpRoutes.OAuthOpenIdConfiguration)]
            HttpRequestData request)
            => this.handler.Metadata(request);

        [Function(McpRoutes.OAuthRegisterFunctionName)]
        public Task<HttpResponseData> Register(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = McpRoutes.ApiRoutePrefix + McpRoutes.OAuthRegister)]
            HttpRequestData request)
            => this.handler.Register(request);

        [Function(McpRoutes.OAuthAuthorizeFunctionName)]
        public Task<HttpResponseData> Authorize(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = McpRoutes.ApiRoutePrefix + McpRoutes.OAuthAuthorize)]
            HttpRequestData request)
            => this.handler.Authorize(request);

        [Function(McpRoutes.OAuthCallbackFunctionName)]
        public Task<HttpResponseData> Callback(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = McpRoutes.ApiRoutePrefix + McpRoutes.OAuthCallback)]
            HttpRequestData request)
            => this.handler.Callback(request);

        [Function(McpRoutes.OAuthTokenFunctionName)]
        public Task<HttpResponseData> Token(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = McpRoutes.ApiRoutePrefix + McpRoutes.OAuthToken)]
            HttpRequestData request)
            => this.handler.Token(request);
    }
}
