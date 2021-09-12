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

    public class SafeExchangeNotifications
    {
        private readonly ICosmosDbProvider cosmosDbProvider;

        private readonly GlobalFilters globalFilters;

        public SafeExchangeNotifications(ICosmosDbProvider cosmosDbProvider, GlobalFilters globalFilters)
        {
            this.cosmosDbProvider = cosmosDbProvider ?? throw new ArgumentNullException(nameof(cosmosDbProvider));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
        }

        public async Task<IActionResult> Run(HttpRequest req, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetFilterResultAsync(req, principal, log);
            if (shouldReturn)
            {
                return filterResult;
            }

            var userName = TokenHelper.GetName(principal);
            log.LogInformation($"SafeExchange-Notifications triggered by {userName}, ID {TokenHelper.GetId(principal)} [{req.Method}].");

            var data = await RequestHelper.GetRequestDataAsync(req);
            var subscription = new NotificationSubscription()
            {
                UserId = userName,
                Url = data?.url,
                Auth = data?.auth,
                P256dh = data?.p256dh
            };

            if (string.IsNullOrEmpty(subscription.Url))
            {
                log.LogInformation($"{nameof(subscription.Url)} is not set.");
                return new BadRequestObjectResult(new { status = "error", error = $"{nameof(subscription.Url)} is required" });
            }

            if (req.Method.ToLower().Equals("post") && string.IsNullOrEmpty(subscription.Auth))
            {
                log.LogInformation($"{nameof(subscription.Auth)} is not set.");
                return new BadRequestObjectResult(new { status = "error", error = $"{nameof(subscription.Auth)} is required" });
            }

            if (req.Method.ToLower().Equals("post") && string.IsNullOrEmpty(subscription.P256dh))
            {
                log.LogInformation($"{nameof(subscription.P256dh)} is not set.");
                return new BadRequestObjectResult(new { status = "error", error = $"{nameof(subscription.P256dh)} is required" });
            }

            var notificationSubscriptions = await cosmosDbProvider.GetNotificationSubscriptionsContainerAsync();
            var notificationsHelper = new NotificationsHelper(notificationSubscriptions, log);
            switch (req.Method.ToLower())
            {
                case "post":
                    return await AddNotificationSubscriptionAsync(userName, subscription, notificationsHelper, log);

                case "delete":
                    return await RemoveNotificationSubscriptionAsync(userName, subscription, notificationsHelper, log);

                default:
                    return new BadRequestObjectResult(new { status = "error", error = "Request method not recognized" });
            }
        }

        private static async Task<IActionResult> AddNotificationSubscriptionAsync(string userName, NotificationSubscription subscription, NotificationsHelper notificationsHelper, ILogger log)
        {
            log.LogInformation($"{nameof(AddNotificationSubscriptionAsync)}: UserId:{userName}, Subscription: {subscription}");
            var createdSubscription = await notificationsHelper.SubscribeAsync(userName, subscription);
            return new OkObjectResult(new { status = "ok", result = new { id = createdSubscription.id } });
        }

        private static async Task<IActionResult> RemoveNotificationSubscriptionAsync(string userName, NotificationSubscription subscription, NotificationsHelper notificationsHelper, ILogger log)
        {
            log.LogInformation($"{nameof(RemoveNotificationSubscriptionAsync)}: UserId:{userName}, Subscription: {subscription}");
            var deleted = await notificationsHelper.UnsubscribeAsync(userName, subscription);
            return new OkObjectResult(new { status = "ok", result = deleted });
        }
    }
}
