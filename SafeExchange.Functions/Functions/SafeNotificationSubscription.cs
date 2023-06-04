/// <summary>
/// SafeAccess
/// </summary>

namespace SafeExchange.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
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

        [Function("SafeExchange-NotificationSubscription")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "delete", Route = $"{Version}/notificationsub/web")]
            HttpRequestData req, ClaimsPrincipal principal, ILogger log)
        {
            return await this.notificationSubscriptionHandler.Run(req, principal, log);
        }
    }
}
