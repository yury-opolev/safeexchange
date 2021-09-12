/// <summary>
/// SafeAccess
/// </summary>

namespace SpaceOyster.SafeExchange.Functions
{
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Azure.WebJobs.Extensions.Http;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;
    using System.Security.Claims;
    using SpaceOyster.SafeExchange.Core;
    using SpaceOyster.SafeExchange.Core.CosmosDb;

    public class SafeAccess
    {
        private SafeExchangeAccess accessHandler;

        public SafeAccess(ICosmosDbProvider cosmosDbProvider, IGraphClientProvider graphClientProvider, ConfigurationSettings configuration, GlobalFilters globalFilters) 
        {
            this.accessHandler = new SafeExchangeAccess(cosmosDbProvider, graphClientProvider, configuration, globalFilters);
        }

        [FunctionName("SafeExchange-Access")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "get", "delete", Route = "access/{secretId}")]
            HttpRequest req,
            string secretId, ClaimsPrincipal principal, ILogger log)
        {
            return await this.accessHandler.Run(req, secretId, principal, log);
        }
    }
}
