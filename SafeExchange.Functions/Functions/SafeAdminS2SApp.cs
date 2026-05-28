namespace SafeExchange.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core;
    using SafeExchange.Core.Applications;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions.Admin;
    using System;
    using System.Threading.Tasks;

    public class SafeAdminS2SApp
    {
        private const string Version = "v2";

        private readonly SafeExchangeAdminS2SApp handler;
        private readonly ILogger log;

        public SafeAdminS2SApp(
            SafeExchangeDbContext dbContext,
            GlobalFilters globalFilters,
            IApplicationOwnerService ownerService,
            ILogger<SafeAdminS2SApp> log)
        {
            this.handler = new SafeExchangeAdminS2SApp(dbContext, globalFilters, ownerService);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-Admin-AppsSearch-Detail")]
        public async Task<HttpResponseData> RunDetail(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/admin/applications/{{displayName}}")]
            HttpRequestData request,
            string displayName)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunDetail(request, displayName, principal, this.log);
        }

        [Function("SafeExchange-Admin-AppsSearch-ReplaceOwners")]
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
