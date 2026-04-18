/// <summary>
/// SafeTelemetryConfig
/// </summary>

namespace SafeExchange.Functions.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core;
    using SafeExchange.Core.Functions;

    public class SafeTelemetryConfig
    {
        private const string Version = "v2";

        private SafeExchangeTelemetryConfig safeExchangeTelemetryConfigHandler;

        private readonly ILogger log;

        public SafeTelemetryConfig(
            SafeExchangeDbContext dbContext,
            ITokenHelper tokenHelper,
            GlobalFilters globalFilters,
            IOptions<WebClientTelemetryConfiguration> telemetryOptions,
            ILogger<SafeTelemetryConfig> log)
        {
            this.safeExchangeTelemetryConfigHandler = new SafeExchangeTelemetryConfig(dbContext, tokenHelper, globalFilters, telemetryOptions);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-TelemetryConfig")]
        public async Task<HttpResponseData> RunTelemetryConfig(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/telemetry/config")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.safeExchangeTelemetryConfigHandler.Run(request, principal, this.log);
        }
    }
}
