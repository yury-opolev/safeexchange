/// <summary>
/// SafeAccess
/// </summary>

namespace SafeExchange.Functions
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Azure.WebJobs.Extensions.Http;
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

        [FunctionName("SafeExchange-Access")]
        public async Task<IActionResult> RunSecret(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "get", "delete", Route = $"{Version}/access/{{secretId}}")]
            HttpRequest req,
            string secretId, ClaimsPrincipal principal, ILogger log)
        {
            return await this.accessHandler.Run(req, secretId, principal, log);
        }
    }
}
