/// <summary>
/// SafeAccess
/// </summary>

namespace SpaceOyster.SafeExchange.Functions.AdminFunctions
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Azure.WebJobs.Extensions.Http;
    using Microsoft.Extensions.Logging;
    using SpaceOyster.SafeExchange.Core;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class SafeAdministratorStatus
    {
        private SafeExchangeAdministratorStatus statusHandler;

        public SafeAdministratorStatus(GlobalFilters globalFilters)
        {
            this.statusHandler = new SafeExchangeAdministratorStatus(globalFilters);
        }

        [FunctionName("SafeExchange-AdminStatus")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "adm/status")]
            HttpRequest req,
            ClaimsPrincipal principal, ILogger log)
        {
            return await this.statusHandler.Run(req, principal, log);
        }
    }
}
