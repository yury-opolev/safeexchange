/// <summary>
/// SafeS2SApps — HTTP trigger entry points for the self-service S2S apps surface.
/// Two routes for now (POST /s2sapps and GET /s2sapps/mine); follow-up commits
/// add the remaining verbs.
/// </summary>

namespace SafeExchange.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core;
    using SafeExchange.Core.Applications;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using System;
    using System.Threading.Tasks;

    public class SafeS2SApps
    {
        private const string Version = "v2";

        private readonly SafeExchangeS2SApps handler;
        private readonly ILogger log;

        public SafeS2SApps(
            SafeExchangeDbContext dbContext,
            ITokenHelper tokenHelper,
            GlobalFilters globalFilters,
            IApplicationOwnerService ownerService,
            IOptionsMonitor<Features> features,
            ILogger<SafeS2SApps> log)
        {
            this.handler = new SafeExchangeS2SApps(dbContext, tokenHelper, globalFilters, ownerService, features);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-S2SApps-Register")]
        public async Task<HttpResponseData> RunRegister(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = $"{Version}/s2sapps")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunRegister(request, principal, this.log);
        }

        [Function("SafeExchange-S2SApps-Mine")]
        public async Task<HttpResponseData> RunListMine(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/s2sapps/mine")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunListMine(request, principal, this.log);
        }
    }
}
