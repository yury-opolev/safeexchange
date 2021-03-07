/// <summary>
/// SafeExchange
/// </summary>

namespace SpaceOyster.SafeExchange.Core
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;
    using SpaceOyster.SafeExchange.Core.CosmosDb;
    using System;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class SafeExchangeAccessRequest
    {
        private readonly ICosmosDbProvider cosmosDbProvider;

        public SafeExchangeAccessRequest(ICosmosDbProvider cosmosDbProvider)
        {
            this.cosmosDbProvider = cosmosDbProvider ?? throw new ArgumentNullException(nameof(cosmosDbProvider));
        }

        public async Task<IActionResult> Run(HttpRequest req, string secretId, ClaimsPrincipal principal, ILogger log)
        {
            // TODO ...
            
            return new OkObjectResult(new { status = "ok" });
        }
    }
}
