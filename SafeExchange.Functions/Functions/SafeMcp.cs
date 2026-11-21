/// <summary>
/// SafeMcp
/// </summary>

namespace SafeExchange.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core;
    using SafeExchange.Core.Functions.Mcp;
    using System;
    using System.Threading.Tasks;

    public class SafeMcp
    {
        private readonly McpHandler handler;

        private readonly ILogger log;

        public SafeMcp(McpHandler handler, ILogger<SafeMcp> log)
        {
            this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        // GET and DELETE are routed here only to answer 405: the endpoint has no server-initiated stream and no sessions.
        [Function(McpRoutes.McpFunctionName)]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "get", "delete", Route = McpRoutes.Mcp)]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.Run(request, principal, this.log);
        }
    }
}
