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

    public class SafeAdminApplicationsSearch
    {
        private const string Version = "v2";

        private readonly SafeExchangeAdminApplicationsSearch handler;
        private readonly ILogger log;

        public SafeAdminApplicationsSearch(
            SafeExchangeDbContext dbContext,
            GlobalFilters globalFilters,
            IOptionsMonitor<Limits> limits,
            ILogger<SafeAdminApplicationsSearch> log)
        {
            this.handler = new SafeExchangeAdminApplicationsSearch(dbContext, globalFilters, limits);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-Admin-AppsSearch-List")]
        public async Task<HttpResponseData> RunList(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/admin/applications")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunList(request, principal, this.log);
        }

        [Function("SafeExchange-Admin-AppsSearch-ToggleEnabled")]
        public async Task<HttpResponseData> RunToggleEnabled(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = $"{Version}/admin/applications/{{displayName}}/enabled")]
            HttpRequestData request,
            string displayName)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunToggleEnabled(request, displayName, principal, this.log);
        }

        [Function("SafeExchange-Admin-AppsSearch-Delete")]
        public async Task<HttpResponseData> RunDelete(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = $"{Version}/admin/applications/{{displayName}}")]
            HttpRequestData request,
            string displayName)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunDelete(request, displayName, principal, this.log);
        }
    }

    public class SafeAdminAudit
    {
        private const string Version = "v2";

        private readonly SafeExchangeAdminAudit handler;
        private readonly ILogger log;

        public SafeAdminAudit(
            SafeExchangeDbContext dbContext,
            GlobalFilters globalFilters,
            IOptionsMonitor<Limits> limits,
            ILogger<SafeAdminAudit> log)
        {
            this.handler = new SafeExchangeAdminAudit(dbContext, globalFilters, limits);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-Admin-Audit-Search")]
        public async Task<HttpResponseData> RunSearchAnchors(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/admin/audit")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunSearchAnchors(request, principal, this.log);
        }
    }

}
