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

    public class SafeListAccessRequests
    {
        private SafeExchangeListAccessRequests accessRequestsListHandler;

        public SafeListAccessRequests(ICosmosDbProvider cosmosDbProvider, IGraphClientProvider graphClientProvider, ConfigurationSettings configuration, GlobalFilters globalFilters)
        {
            this.accessRequestsListHandler = new SafeExchangeListAccessRequests(cosmosDbProvider, graphClientProvider, configuration, globalFilters);
        }

        [FunctionName("SafeExchange-ListAccessRequests")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "accessrequests")]
            HttpRequest req,
            ClaimsPrincipal principal, ILogger log)
        {
            return await this.accessRequestsListHandler.Run(req, principal, log);
        }
    }
}
