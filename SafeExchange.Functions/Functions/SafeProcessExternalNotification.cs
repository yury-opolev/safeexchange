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
    using System.Threading.Tasks;

    public class SafeProcessExternalNotification
    {
        private SafeExchangeProcessExternalNotification processExternalNotificationHandler;

        private readonly ILogger log;

        public SafeProcessExternalNotification(SafeExchangeDbContext dbContext, IQueueHelper queueHelper, ILogger<SafeExchangeProcessExternalNotification> log)
        {
            this.processExternalNotificationHandler = new SafeExchangeProcessExternalNotification(dbContext, queueHelper);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-ProcessExternalNotification")]
        public async Task Run(
            [QueueTrigger("myqueue-items", Connection = "testazfuncqueue_STORAGE")] QueueMessage message)
        {
            await this.processExternalNotificationHandler.Run(message, this.log);
        }
    }
}
