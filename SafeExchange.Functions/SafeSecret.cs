/// <summary>
/// SafeSecret
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

    public class SafeSecret
    {
        private SafeExchangeSecret secretHandler;

        public SafeSecret(ICosmosDbProvider cosmosDbProvider, IGraphClientProvider graphClientProvider, ConfigurationSettings configuration)
        {
            this.secretHandler = new SafeExchangeSecret(cosmosDbProvider, graphClientProvider, configuration);
        }

        [FunctionName("SafeExchange-Secret")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "get", "patch", "delete", Route = "secrets/{secretId}")]
            HttpRequest req,
            string secretId, ClaimsPrincipal principal, GlobalFilters globalFilters, ILogger log)
        {
            return await this.secretHandler.Run(req, secretId, principal, globalFilters, log);
        }
    }
}
