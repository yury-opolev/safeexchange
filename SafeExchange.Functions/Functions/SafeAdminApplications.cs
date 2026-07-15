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
    using SafeExchange.Core.Functions.Admin;
    using System;
    using System.Threading.Tasks;

    public class SafeAdminApplications
    {
        private const string Version = "{apiVersion}";

        private readonly SafeExchangeAdminApplications handler;
        private readonly ILogger log;

        public SafeAdminApplications(
            SafeExchangeDbContext dbContext,
            GlobalFilters globalFilters,
            IOptionsMonitor<Limits> limits,
            IApplicationOwnerService ownerService,
            ILogger<SafeAdminApplications> log)
        {
            this.handler = new SafeExchangeAdminApplications(dbContext, globalFilters, limits, ownerService);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-Admin-Apps-List")]
        public async Task<HttpResponseData> RunList(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/admin/applications")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunList(request, principal, this.log);
        }

        [Function("SafeExchange-Admin-Apps-Detail")]
        public async Task<HttpResponseData> RunDetail(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/admin/applications/{{displayName}}")]
            HttpRequestData request,
            string displayName)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunDetail(request, displayName, principal, this.log);
        }

        [Function("SafeExchange-Admin-Apps-ToggleEnabled")]
        public async Task<HttpResponseData> RunToggleEnabled(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = $"{Version}/admin/applications/{{displayName}}/enabled")]
            HttpRequestData request,
            string displayName)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunToggleEnabled(request, displayName, principal, this.log);
        }

        [Function("SafeExchange-Admin-Apps-Delete")]
        public async Task<HttpResponseData> RunDelete(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = $"{Version}/admin/applications/{{displayName}}")]
            HttpRequestData request,
            string displayName)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunDelete(request, displayName, principal, this.log);
        }

        [Function("SafeExchange-Admin-Apps-ReplaceOwners")]
        public async Task<HttpResponseData> RunReplaceOwners(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = $"{Version}/admin/applications/{{displayName}}/owners")]
            HttpRequestData request,
            string displayName)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunReplaceOwners(request, displayName, principal, this.log);
        }
    }
}
