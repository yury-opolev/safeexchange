/// <summary>
/// SafeExchangeProcessExternalNotification
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Azure.Storage.Queues.Models;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.DelayedTasks;
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

            var messageBody = message.Body?.ToString() ?? string.Empty;
            WebhookNotificationTaskPayload? webhookNotificationTaskPayload;
            try
            {
                webhookNotificationTaskPayload = DefaultJsonSerializer.Deserialize<WebhookNotificationTaskPayload>(messageBody);
            }
            catch
            {
                log.LogWarning($"Could not parse message body for notification processing, message will be discarded.");
                return;
            }

            if (webhookNotificationTaskPayload == null || string.IsNullOrEmpty(webhookNotificationTaskPayload.WebhookNotificationDataId))
            {
                log.LogWarning($"Notification task payload is null or data id is empty, message will be discarded.");
                return;
            }

            // TODO
            log.LogInformation($"Task payload: {webhookNotificationTaskPayload.TaskType}, {webhookNotificationTaskPayload.SubType}: {webhookNotificationTaskPayload.WebhookNotificationDataId}.");
            await Task.CompletedTask;
        }
    }
}
