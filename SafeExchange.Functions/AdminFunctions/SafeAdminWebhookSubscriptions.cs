/// <summary>
/// SafeAdminWebhookSubscriptions
/// </summary>

namespace SafeExchange.Functions.AdminFunctions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions.Admin;
    using SafeExchange.Core;
    using System;

    public class SafeAdminWebhookSubscriptions
    {
        private const string Version = "v1";

        private SafeExchangeWebhookSubscriptions safeExchangeWebhookSubscriptionsHandler;

        private readonly ILogger log;

        public SafeAdminWebhookSubscriptions(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, ILogger<SafeAdminApplications> log)
        {
            this.safeExchangeWebhookSubscriptionsHandler = new SafeExchangeWebhookSubscriptions(dbContext, tokenHelper, globalFilters);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-WebhookSubscriptionCreate")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = $"{Version}/webhooksubscriptions")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.safeExchangeWebhookSubscriptionsHandler.Run(request, string.Empty, principal, this.log);
        }

        [Function("SafeExchange-WebhookSubscription")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "patch", "delete", Route = $"{Version}/webhooksubscriptions/{{webhookSubscriptionId}}")]
            HttpRequestData request,
            string webhookSubscriptionId)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.safeExchangeWebhookSubscriptionsHandler.Run(request, webhookSubscriptionId, principal, this.log);
        }
    }
}
