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
    /// Anonymous RFC 9728 protected-resource metadata for MCP clients (404 while the MCP endpoint is disabled).
    /// </summary>
    public sealed class OAuthProtectedResourceHandler
    {
        private readonly McpProtectedResource protectedResource;

        private readonly IOptionsMonitor<Features> features;

        public OAuthProtectedResourceHandler(McpProtectedResource protectedResource, IOptionsMonitor<Features> features)
        {
            this.protectedResource = protectedResource ?? throw new ArgumentNullException(nameof(protectedResource));
            this.features = features ?? throw new ArgumentNullException(nameof(features));
        }

        public async Task<HttpResponseData> Run(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!this.features.CurrentValue.McpEndpoint)
            {
                return request.CreateResponse(HttpStatusCode.NotFound);
            }

            var response = request.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(this.protectedResource.ToMetadata(McpHandler.ApiBaseUri(request.Url)), McpJsonUtilities.DefaultOptions));
            return response;
        }
    }
}
