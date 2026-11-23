/// <summary>
/// McpHandler
/// </summary>

namespace SafeExchange.Core.Functions.Mcp
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Telemetry;
    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    /// <summary>
    /// Functions adapter for <c>/api/v1/mcp</c>: feature flag, method check and caller resolution (a denial is
    /// answered exactly like REST, before any MCP processing), then <see cref="McpRequestDispatcher"/>.
    /// </summary>
    public sealed class McpHandler
    {
        private readonly ICallerResolver callerResolver;

        private readonly McpRequestDispatcher dispatcher;

        private readonly IOptionsMonitor<Features> features;

        public McpHandler(ICallerResolver callerResolver, McpRequestDispatcher dispatcher, IOptionsMonitor<Features> features)
        {
            this.callerResolver = callerResolver ?? throw new ArgumentNullException(nameof(callerResolver));
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            this.features = features ?? throw new ArgumentNullException(nameof(features));
        }

        public async Task<HttpResponseData> Run(HttpRequestData request, ClaimsPrincipal principal, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!this.features.CurrentValue.McpEndpoint)
            {
                return request.CreateResponse(HttpStatusCode.NotFound);
            }

            if (!string.Equals(request.Method, "post", StringComparison.OrdinalIgnoreCase))
            {
                var notAllowed = request.CreateResponse(HttpStatusCode.MethodNotAllowed);
                notAllowed.Headers.Add("Allow", "POST");
                return notAllowed;
            }

            var clientLabel = McpClientLabel.FromSessionId(FirstHeader(request, McpClientLabel.SessionIdHeaderName));
            var resolution = await this.callerResolver.ResolveAsync(principal, CallerChannels.Mcp, clientLabel);
            if (!resolution.IsAllowed)
            {
                return await resolution.Denial.ToResponseAsync(request);
            }

            log.LogInformation($"{nameof(McpHandler)} triggered by {resolution.Caller.SubjectType} (tid {TelemetryContext.Current}).");

            string body;
            using (var reader = new StreamReader(request.Body ?? Stream.Null))
            {
                body = await reader.ReadToEndAsync();
            }

            var mcpRequest = new McpRequest(body, resolution.Caller, ApiBaseUri(request.Url), FirstHeader(request, McpProtocol.ProtocolVersionHeaderName));
            var mcpResponse = await this.dispatcher.DispatchAsync(mcpRequest);

            var response = request.CreateResponse(mcpResponse.StatusCode);
            if (mcpResponse.SessionId is not null)
            {
                response.Headers.Add(McpClientLabel.SessionIdHeaderName, mcpResponse.SessionId);
            }

            if (mcpResponse.Body is not null)
            {
                response.Headers.Add("Content-Type", McpResponse.ContentType);
                await response.WriteStringAsync(mcpResponse.Body);
            }

            return response;
        }

        /// <summary>
        /// The REST API base of this host, e.g. <c>https://host/api/</c> for <c>https://host/api/v1/mcp</c>.
        /// </summary>
        public static Uri ApiBaseUri(Uri requestUrl)
        {
            ArgumentNullException.ThrowIfNull(requestUrl);
            return new Uri(requestUrl, $"/{McpRoutes.RoutePrefix}/");
        }

        private static string? FirstHeader(HttpRequestData request, string name)
            => request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    }
}
