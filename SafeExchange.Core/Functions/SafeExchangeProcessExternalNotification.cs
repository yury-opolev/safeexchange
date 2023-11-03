/// <summary>
/// SafeExchangeProcessExternalNotification
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Azure.Storage.Queues.Models;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.DelayedTasks;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Purger;
    using System;
    using System.Net.Http.Json;

    public class SafeExchangeProcessExternalNotification
    {
        private readonly IHttpClientFactory httpClientFactory;

        private readonly IPurger purger;

        private readonly SafeExchangeDbContext dbContext;

        public SafeExchangeProcessExternalNotification(SafeExchangeDbContext dbContext, IPurger purger, IHttpClientFactory httpClientFactory)
        {
            this.purger = purger ?? throw new ArgumentNullException(nameof(purger));
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
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

            if (webhookNotificationTaskPayload.TaskType != DelayedTaskType.ExternalNotification)
            {
                log.LogWarning($"Notification task payload {nameof(webhookNotificationTaskPayload.TaskType)} is '{webhookNotificationTaskPayload.TaskType}', message will be discarded.");
                return;
            }

            if (!WebhookNotificationTaskPayload.AccessRequestCreatedSubType.Equals(webhookNotificationTaskPayload.SubType, StringComparison.OrdinalIgnoreCase))
            {
                log.LogWarning($"Notification task payload {nameof(webhookNotificationTaskPayload.SubType)} is '{webhookNotificationTaskPayload.SubType}', message will be discarded.");
                return;
            }

            var notificationData = await this.dbContext.WebhookNotificationData.FindAsync(webhookNotificationTaskPayload.WebhookNotificationDataId);
            if (notificationData == null)
            {
                log.LogWarning($"Notification data is null or {nameof(notificationData.EventId)} or {nameof(notificationData.WebhookSubscriptionId)} is empty, message will be discarded.");
                return;
            }

            if (string.IsNullOrEmpty(notificationData.EventId) || string.IsNullOrEmpty(notificationData.WebhookSubscriptionId))
            {
                log.LogWarning($"Notification data {nameof(notificationData.EventId)} or {nameof(notificationData.WebhookSubscriptionId)} is empty, message will be discarded and notification data will be removed.");
                await this.purger.PurgeNotificationDataAsync(notificationData.Id, this.dbContext);
                return;
            }

            var webhookSubscription = await this.dbContext.WebhookSubscriptions.FirstOrDefaultAsync(whs => whs.Id.Equals(notificationData.WebhookSubscriptionId));
            if (webhookSubscription == null || !webhookSubscription.Enabled)
            {
                log.LogWarning($"Webhook subscription '{notificationData.WebhookSubscriptionId}' does not exist or is not enabled, message will be discarded and notification data will be removed.");
                await this.purger.PurgeNotificationDataAsync(notificationData.Id, this.dbContext);
                return;
            }

            var accessRequest = await this.dbContext.AccessRequests.FindAsync(notificationData.EventId);
            if (accessRequest == null || accessRequest.Status != RequestStatus.InProgress)
            {
                log.LogWarning($"Access request '{notificationData.EventId}' does not exist or is not in progress, message will be discarded and notification data will be removed.");
                await this.purger.PurgeNotificationDataAsync(notificationData.Id, this.dbContext);
                return;
            }

            await this.TryProcessExternalNotificationAsync(accessRequest, webhookSubscription, notificationData.Id, log);
        }

        private async Task TryProcessExternalNotificationAsync(AccessRequest accessRequest, WebhookSubscription webhookSubscription, string notificationDataId, ILogger log)
        {
            log.LogInformation($"Processing notification, webhook subscription url '{webhookSubscription.Url}', access request {accessRequest.Id} to {accessRequest.ObjectName}, permission {accessRequest.Permission}.");

            try
            {
                using var httpClient = this.httpClientFactory.CreateClient();
                var content = JsonContent.Create(new WebhookPayloadData() { NotificationDataId = notificationDataId });
                var responseMessage = await httpClient.PostAsync(webhookSubscription.Url, content);
                log.LogInformation($"Webhook subscription url call finished with status code: {responseMessage.StatusCode}.");
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, $"Exception in {nameof(TryProcessExternalNotificationAsync)}: {ex.GetType()}: {ex.Message}, keeping notification data.");
            }
        }
    }
}
