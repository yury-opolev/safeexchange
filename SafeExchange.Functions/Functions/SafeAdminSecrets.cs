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

    public class SafeAdminSecrets
    {
        private const string Version = "v2";

        private readonly SafeExchangeAdminSecrets handler;
        private readonly ILogger log;

        public SafeAdminSecrets(
            SafeExchangeDbContext dbContext,
            GlobalFilters globalFilters,
            IOptionsMonitor<Limits> limits,
            ILogger<SafeAdminSecrets> log)
        {
            this.handler = new SafeExchangeAdminSecrets(dbContext, globalFilters, limits);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-Admin-Secrets-List")]
        public async Task<HttpResponseData> RunList(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/admin/secret-list")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunList(request, principal, this.log);
        }

        [Function("SafeExchange-Admin-Secrets-Detail")]
        public async Task<HttpResponseData> RunDetail(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/admin/secret/{{secretName}}")]
            HttpRequestData request,
            string secretName)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunDetail(request, secretName, principal, this.log);
        }

        [Function("SafeExchange-Admin-Secrets-Access")]
        public async Task<HttpResponseData> RunAccess(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/admin/secret/{{secretName}}/access")]
            HttpRequestData request,
            string secretName)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunAccess(request, secretName, principal, this.log);
        }
    }
}
