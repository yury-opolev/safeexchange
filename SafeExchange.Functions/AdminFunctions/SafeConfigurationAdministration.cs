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
    using SpaceOyster.SafeExchange.Core.CosmosDb;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class SafeConfigurationAdministration
    {
        private SafeExchangeConfigurationAdministration configurationHandler;

        public SafeConfigurationAdministration(IGraphClientProvider graphClientProvider, ConfigurationSettings configuration, GlobalFilters globalFilters)
        {
            this.configurationHandler = new SafeExchangeConfigurationAdministration(graphClientProvider, configuration, globalFilters);
        }

        [FunctionName("SafeExchange-AdminConfiguration")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "get", Route = "adm/configuration")]
            HttpRequest req,
            ClaimsPrincipal principal, ILogger log)
        {
            return await this.configurationHandler.Run(req, principal, log);
        }
    }
}
