/// <summary>
/// SafeExchange
/// </summary>

namespace SpaceOyster.SafeExchange.Functions
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

    public class SafeAccessRequest
    {
        private SafeExchangeAccessRequest accessRequestHandler;

        public SafeAccessRequest(ICosmosDbProvider cosmosDbProvider, IGraphClientProvider graphClientProvider, ConfigurationSettings configuration, GlobalFilters globalFilters)
        {
            this.accessRequestHandler = new SafeExchangeAccessRequest(cosmosDbProvider, graphClientProvider, configuration, globalFilters);
        }

        [FunctionName("SafeExchange-AccessRequest")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "patch", "delete", Route = "accessrequest")]
            HttpRequest req,
            ClaimsPrincipal principal, ILogger log)
        {
            return await this.accessRequestHandler.Run(req, principal, log);
        }
    }
}
