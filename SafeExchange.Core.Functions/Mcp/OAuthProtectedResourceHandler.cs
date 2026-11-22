/// <summary>
/// OAuthProtectedResourceHandler
/// </summary>

namespace SafeExchange.Core.Functions.Mcp
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Options;
    using ModelContextProtocol;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Mcp;
    using System;
    using System.Net;
    using System.Text.Json;
    using System.Threading.Tasks;

    /// <summary>
    /// Anonymous RFC 9728 protected-resource metadata for MCP clients, <c>Cache-Control: no-store</c> (404 while the MCP
    /// endpoint is disabled or for a base URL that <see cref="McpBaseUriGuard"/> refuses).
    /// </summary>
    public sealed class OAuthProtectedResourceHandler
    {
        private readonly McpProtectedResource protectedResource;

        private readonly IOptionsMonitor<Features> features;

        private readonly McpBaseUriGuard baseUriGuard;

        public OAuthProtectedResourceHandler(McpProtectedResource protectedResource, IOptionsMonitor<Features> features, McpBaseUriGuard baseUriGuard)
        {
            this.protectedResource = protectedResource ?? throw new ArgumentNullException(nameof(protectedResource));
            this.features = features ?? throw new ArgumentNullException(nameof(features));
            this.baseUriGuard = baseUriGuard ?? throw new ArgumentNullException(nameof(baseUriGuard));
        }

        public async Task<HttpResponseData> Run(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var apiBaseUri = McpHandler.ApiBaseUri(request.Url);
            if (!this.features.CurrentValue.McpEndpoint || !this.baseUriGuard.IsAllowed(apiBaseUri))
            {
                return request.CreateResponse(HttpStatusCode.NotFound);
            }

            var response = request.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            response.Headers.Add("Cache-Control", "no-store");
            await response.WriteStringAsync(JsonSerializer.Serialize(this.protectedResource.ToMetadata(apiBaseUri), McpJsonUtilities.DefaultOptions));
            return response;
        }
    }
}
