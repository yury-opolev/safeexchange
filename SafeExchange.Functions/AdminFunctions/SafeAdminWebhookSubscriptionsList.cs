/// <summary>
/// SafeAdminWebhookSubscriptionsList
/// </summary>

namespace SafeExchange.Functions.AdminFunctions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core;
    using SafeExchange.Core.Functions;

    public class SafeAdminWebhookSubscriptionsList
    {
        private const string Version = "v2";

        private SafeExchangeWebhookSubscriptionsList safeExchangeWebhookSubscriptionsListHandler;

        private readonly ILogger log;

        public SafeAdminWebhookSubscriptionsList(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, ILogger<SafeAdminApplications> log)
        {
            this.safeExchangeWebhookSubscriptionsListHandler = new SafeExchangeWebhookSubscriptionsList(dbContext, tokenHelper, globalFilters);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-WebhookSubscriptionList")]
        public async Task<HttpResponseData> RunListApplications(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/webhooksubscriptions-list")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.safeExchangeWebhookSubscriptionsListHandler.RunList(request, principal, this.log);
        }
    }
}
