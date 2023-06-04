/// <summary>
/// SafeAdminApplications
/// </summary>

namespace SafeExchange.Functions.AdminFunctions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Extensions.Logging;
    using System.Security.Claims;
    using SafeExchange.Core.Crypto;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions.Admin;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core;

    public class SafeAdminApplications
    {
        private const string Version = "v2";

        private SafeExchangeApplications safeExchangeApplicationsHandler;

        public SafeAdminApplications(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, ICryptoHelper cryptoHelper, GlobalFilters globalFilters, IPurger purger, IPermissionsManager permissionsManager)
        {
            this.safeExchangeApplicationsHandler = new SafeExchangeApplications(dbContext, tokenHelper, cryptoHelper, globalFilters);
        }

        [Function("SafeExchange-Application")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "get", "patch", "delete", Route = $"{Version}/applications/{{applicationId}}")]
            HttpRequestData request,
            string operationName, ClaimsPrincipal principal, ILogger log)
        {
            return await this.safeExchangeApplicationsHandler.Run(request, operationName, principal, log);
        }

        [Function("SafeExchange-ApplicationList")]
        public async Task<HttpResponseData> RunListApplications(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/applications-list")]
            HttpRequestData request, ClaimsPrincipal principal, ILogger log)
        {
            return await this.safeExchangeApplicationsHandler.RunList(request, principal, log);
        }
    }
}
