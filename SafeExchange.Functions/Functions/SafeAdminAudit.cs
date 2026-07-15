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

    public class SafeAdminAudit
    {
        private const string Version = "{apiVersion}";

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

        [Function("SafeExchange-Admin-Audit-Instance")]
        public async Task<HttpResponseData> RunInstance(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/admin/audit/{{auditInstanceId}}")]
            HttpRequestData request,
            string auditInstanceId)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunInstance(request, auditInstanceId, principal, this.log);
        }
    }
}
