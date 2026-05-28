namespace SafeExchange.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions.Admin;
    using System;
    using System.Threading.Tasks;

    public class SafeAdminSecretAudit
    {
        private const string Version = "v2";

        private readonly SafeExchangeAdminSecretAudit handler;
        private readonly ILogger log;

        public SafeAdminSecretAudit(
            SafeExchangeDbContext dbContext,
            GlobalFilters globalFilters,
            ILogger<SafeAdminSecretAudit> log)
        {
            this.handler = new SafeExchangeAdminSecretAudit(dbContext, globalFilters);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-Admin-Secret-Audit")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/admin/secret/{{secretId}}/audit")]
            HttpRequestData request,
            string secretId)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.Run(request, secretId, principal, this.log);
        }
    }
}
