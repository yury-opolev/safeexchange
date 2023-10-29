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

        public SafeExchangeProcessExternalNotification(SafeExchangeDbContext dbContext)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public async Task Run(QueueMessage message, ILogger log)
        {
            log.LogInformation($"{nameof(SafeExchangeProcessExternalNotification)} triggered.");

            // TODO
            log.LogInformation($"Message body: {message.Body?.ToString()}.");
            await Task.CompletedTask;
        }
    }
}
