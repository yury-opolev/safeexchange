namespace SafeExchange.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions.Admin;
    using System;
    using System.Threading.Tasks;

    public class SafeAdminUsers
    {
        private const string Version = "v2";

        private readonly SafeExchangeAdminUsers handler;
        private readonly ILogger log;

        public SafeAdminUsers(
            SafeExchangeDbContext dbContext,
            ITokenHelper tokenHelper,
            GlobalFilters globalFilters,
            IOptionsMonitor<Limits> limits,
            ILogger<SafeAdminUsers> log)
        {
            this.handler = new SafeExchangeAdminUsers(dbContext, tokenHelper, globalFilters, limits);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-Admin-Users-List")]
        public async Task<HttpResponseData> RunList(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/admin/users")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunList(request, principal, this.log);
        }

        [Function("SafeExchange-Admin-Users-Detail")]
        public async Task<HttpResponseData> RunDetail(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/admin/users/{{upn}}")]
            HttpRequestData request,
            string upn)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunDetail(request, upn, principal, this.log);
        }

        [Function("SafeExchange-Admin-Users-ByTelemetryId")]
        public async Task<HttpResponseData> RunByTelemetryId(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/admin/users/by-telemetry-id/{{telemetryId}}")]
            HttpRequestData request,
            string telemetryId)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunByTelemetryId(request, telemetryId, principal, this.log);
        }

        [Function("SafeExchange-Admin-Users-ToggleEnabled")]
        public async Task<HttpResponseData> RunToggleEnabled(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = $"{Version}/admin/users/{{upn}}/enabled")]
            HttpRequestData request,
            string upn)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunToggleEnabled(request, upn, principal, this.log);
        }
    }
}
