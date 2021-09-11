/// <summary>
/// SafeListSecrets
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

    public class SafeListSecrets
    {
        private SafeExchangeListSecrets listSecretsHandler;

        public SafeListSecrets(ICosmosDbProvider cosmosDbProvider, IGraphClientProvider graphClientProvider, ConfigurationSettings configuration)
        {
            this.listSecretsHandler = new SafeExchangeListSecrets(cosmosDbProvider, graphClientProvider, configuration);
        }

        [FunctionName("SafeExchange-ListSecrets")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "secrets")]
            HttpRequest req,
            ClaimsPrincipal principal, GlobalFilters globalFilters, ILogger log)
        {
            return await this.listSecretsHandler.Run(req, principal, globalFilters, log);
        }
    }
}
