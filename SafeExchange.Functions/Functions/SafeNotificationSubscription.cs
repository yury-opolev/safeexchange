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

        private readonly ILogger log;

        public SafeNotificationSubscription(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, ILogger<SafeNotificationSubscription> log)
        {
            this.notificationSubscriptionHandler = new SafeExchangeNotificationSubscription(dbContext, tokenHelper, globalFilters);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-NotificationSubscription")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "delete", Route = $"{Version}/notificationsub/web")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.notificationSubscriptionHandler.Run(request, principal, this.log);
        }
    }
}
