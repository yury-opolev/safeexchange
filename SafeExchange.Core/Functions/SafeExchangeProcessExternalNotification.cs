/// <summary>
/// SafeExchangeProcessExternalNotification
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Azure.Storage.Queues.Models;
    using Microsoft.Extensions.Logging;
    using System;

    public class SafeExchangeProcessExternalNotification
    {
        private readonly SafeExchangeDbContext dbContext;

        private readonly IQueueHelper queueHelper;

        public SafeExchangeProcessExternalNotification(SafeExchangeDbContext dbContext, IQueueHelper queueHelper)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.queueHelper = queueHelper ?? throw new ArgumentNullException(nameof(queueHelper));
        }

        public async Task Run(QueueMessage message, ILogger log)
        {
            log.LogInformation($"{nameof(SafeExchangeProcessExternalNotification)} triggered.");

            await Task.CompletedTask;
            
            // TODO
        }
    }
}
