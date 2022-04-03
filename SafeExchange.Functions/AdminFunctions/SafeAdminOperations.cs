/// <summary>
/// SafeAdminOperations
/// </summary>

namespace SafeExchange.Functions
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Azure.WebJobs.Extensions.Http;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core;
    using SafeExchange.Core.Crypto;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions.Admin;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class SafeAdminOperations
    {
        private const string Version = "v2";

        private SafeExchangeAdminOperations adminOperationsHandler;

        public SafeAdminOperations(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, ICryptoHelper cryptoHelper, GlobalFilters globalFilters, IPurger purger, IPermissionsManager permissionsManager)
        {
            this.adminOperationsHandler = new SafeExchangeAdminOperations(dbContext, tokenHelper, cryptoHelper, globalFilters);
        }

        [FunctionName("SafeExchange-AdminOperations")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = $"{Version}/admops/{{operationName}}")]
            HttpRequest req,
            string operationName, ClaimsPrincipal principal, ILogger log)
        {
            return await this.adminOperationsHandler.Run(req, operationName, principal, log);
        }
    }
}
