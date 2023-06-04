/// <summary>
/// SafeAccess
/// </summary>

namespace SafeExchange.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class SafeAccess
    {
        private const string Version = "v2";

        private SafeExchangeAccess accessHandler;

        public SafeAccess(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, IPurger purger, IPermissionsManager permissionsManager)
        {
            this.accessHandler = new SafeExchangeAccess(dbContext, tokenHelper, globalFilters, purger, permissionsManager);
        }

        [Function("SafeExchange-Access")]
        public async Task<HttpResponseData> RunSecret(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "get", "delete", Route = $"{Version}/access/{{secretId}}")]
            HttpRequestData req,
            string secretId, ClaimsPrincipal principal, ILogger log)
        {
            return await this.accessHandler.Run(req, secretId, principal, log);
        }
    }
}
