/// <summary>
/// SafeExternalNotificationDetails
/// </summary>


namespace SafeExchange.Functions.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.DelayedTasks;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core;
    using Microsoft.Extensions.Configuration;

    public class SafeExternalNotificationDetails
    {
        private const string Version = "v1";

        private SafeExchangeExternalNotificationDetails notificationDetailsHandler;

        private readonly ILogger log;

        public SafeExternalNotificationDetails(IConfiguration configuration, SafeExchangeDbContext dbContext, IPurger purger, ITokenHelper tokenHelper, GlobalFilters globalFilters, ILogger<SafeExternalNotificationDetails> log)
        {
            this.notificationDetailsHandler = new SafeExchangeExternalNotificationDetails(configuration, dbContext, purger, tokenHelper, globalFilters);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-ExternalNotificationDetails")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/notificationdetails/{{webhookNotificationDataId}}")]
            HttpRequestData request,
            string webhookNotificationDataId)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.notificationDetailsHandler.Run(webhookNotificationDataId, request, principal, this.log);
        }
    }
}
