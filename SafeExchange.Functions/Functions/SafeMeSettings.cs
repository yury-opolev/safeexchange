/// <summary>
/// SafeMeSettings — HTTP trigger entry points for the caller's own settings (GET and PATCH /me/settings).
/// </summary>

namespace SafeExchange.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using System;
    using System.Threading.Tasks;

    public class SafeMeSettings
    {
        private const string Version = "api/{apiVersion}";

        private readonly SafeExchangeMeSettings handler;

        private readonly ILogger log;

        public SafeMeSettings(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, ILogger<SafeMeSettings> log)
        {
            this.handler = new SafeExchangeMeSettings(dbContext, tokenHelper, globalFilters);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-Me-Settings-Get")]
        public async Task<HttpResponseData> RunGet(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/me/settings")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunGet(request, principal, this.log);
        }

        [Function("SafeExchange-Me-Settings-Update")]
        public async Task<HttpResponseData> RunUpdate(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = $"{Version}/me/settings")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunUpdate(request, principal, this.log);
        }
    }
}
