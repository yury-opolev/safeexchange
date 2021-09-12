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

    public class SafeNotificationSubscription
    {
        private SafeExchangeNotifications notificationSubscriptionsHandler;

        public SafeNotificationSubscription(ICosmosDbProvider cosmosDbProvider, GlobalFilters globalFilters)
        {
            this.notificationSubscriptionsHandler = new SafeExchangeNotifications(cosmosDbProvider, globalFilters);
        }

        [FunctionName("SafeExchange-Notifications")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "delete", Route = "notifications")]
            HttpRequest req,
            ClaimsPrincipal principal, ILogger log)
        {
            return await this.notificationSubscriptionsHandler.Run(req, principal, log);
        }
    }
}
