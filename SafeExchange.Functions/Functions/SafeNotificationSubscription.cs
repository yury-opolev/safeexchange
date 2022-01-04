/// <summary>
/// SafeAccess
/// </summary>

namespace SafeExchange.Functions
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Azure.WebJobs.Extensions.Http;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class SafeNotificationSubscription
    {
        private const string Version = "v2";

        private SafeExchangeNotificationSubscription notificationSubscriptionHandler;

        public SafeNotificationSubscription(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters)
        {
            this.notificationSubscriptionHandler = new SafeExchangeNotificationSubscription(dbContext, tokenHelper, globalFilters);
        }

        [FunctionName("SafeExchange-NotificationSubscription")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "delete", Route = $"{Version}/notificationsub/web")]
            HttpRequest req, ClaimsPrincipal principal, ILogger log)
        {
            return await this.notificationSubscriptionHandler.Run(req, principal, log);
        }
    }
}
