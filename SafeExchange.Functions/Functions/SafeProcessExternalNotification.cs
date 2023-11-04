/// <summary>
/// SafeProcessExternalNotification
/// </summary>

namespace SafeExchange.Functions
{
    using Azure.Storage.Queues.Models;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Purger;
    using System.Threading.Tasks;

    public class SafeProcessExternalNotification
    {
        private SafeExchangeProcessExternalNotification processExternalNotificationHandler;

        private readonly ILogger log;

        public SafeProcessExternalNotification(SafeExchangeDbContext dbContext, IPurger purger, IHttpClientFactory httpClientFactory, ILogger<SafeExchangeProcessExternalNotification> log)
        {
            this.processExternalNotificationHandler = new SafeExchangeProcessExternalNotification(dbContext, purger, httpClientFactory);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-ProcessExternalNotification")]
        public async Task Run(
            [QueueTrigger("delayed-webhooks", Connection = "DelayedTasksQueue")] QueueMessage message)
        {
            await this.processExternalNotificationHandler.Run(message, this.log);
        }
    }
}
