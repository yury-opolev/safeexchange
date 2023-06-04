/// <summary>
/// SafeAccessRequest
/// </summary>

namespace SafeExchange.Functions.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class SafeAccessRequest
    {
        private const string Version = "v2";

        private SafeExchangeAccessRequest accessRequestHandler;

        public SafeAccessRequest(IConfiguration configuration, SafeExchangeDbContext dbContext, GlobalFilters globalFilters, ITokenHelper tokenHelper, IPurger purger, IPermissionsManager permissionsMaanger)
        {
            this.accessRequestHandler = new SafeExchangeAccessRequest(configuration, dbContext, globalFilters, tokenHelper, purger, permissionsMaanger);
        }

        [Function("SafeExchange-AccessRequest")]
        public async Task<HttpResponseData> RunSecret(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "patch", "delete", Route = $"{Version}/accessrequest/{{secretId}}")]
            HttpRequestData req,
            string secretId, ClaimsPrincipal principal, ILogger log)
        {
            return await this.accessRequestHandler.Run(req, secretId, principal, log);
        }

        [Function("SafeExchange-ListAccessRequests")]
        public async Task<HttpResponseData> RunListSecret(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/accessrequest-list")]
            HttpRequestData req, ClaimsPrincipal principal, ILogger log)
        {
            return await this.accessRequestHandler.RunList(req, principal, log);
        }
    }
}
