/// <summary>
/// SafeExchange
/// </summary>

namespace SpaceOyster.SafeExchange.Core
{
    using Microsoft.Azure.Cosmos;
    using Microsoft.Extensions.Logging;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Text.Json;
    using System.Threading.Tasks;
    using WebPush;

    public class NotificationsHelper
    {
        private Container notificationSubscriptions;

        private VapidOptions options;

        private JsonSerializerOptions serializerOptions;

        private ILogger log;

        public NotificationsHelper(Container notificationSubscriptions, ILogger logger)
        {
            this.notificationSubscriptions = notificationSubscriptions ?? throw new ArgumentNullException(nameof(notificationSubscriptions));
            this.log = logger ?? throw new ArgumentNullException(nameof(logger));
            this.serializerOptions = new JsonSerializerOptions() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        }

        public async ValueTask<NotificationSubscription> SubscribeAsync(string userId, NotificationSubscription subscription)
        {
            this.log.LogInformation($"{nameof(SubscribeAsync)}: UserId = {userId}, Subscription = {subscription}");

            var existingSubscription = await this.TryGetExistingSubscription(userId, subscription.Url);
            if (existingSubscription != default(NotificationSubscription))
            {
                this.log.LogInformation($"Subscription with given endpoint already exists for {userId}.");
                return existingSubscription;
            }

            subscription.id = GetNewId();
            subscription.UserId = userId;
            subscription.PartitionKey = NotificationsHelper.GetPartitionKey(userId);

            await this.notificationSubscriptions.UpsertItemAsync(subscription);
            this.log.LogInformation($"Subscription {subscription} was added.");

            return subscription;
        }

        public async ValueTask<bool> UnsubscribeAsync(string userId, NotificationSubscription subscription)
        {
            this.log.LogInformation($"{nameof(UnsubscribeAsync)}: UserId = {userId}, Subscription = {subscription}");

            var existingSubscription = await this.TryGetExistingSubscription(userId, subscription.Url);
            if (existingSubscription == default(NotificationSubscription))
            {
                this.log.LogInformation($"Subscription with given endpoint not exists for {userId}.");
                return true;
            }
            else
            {
                this.log.LogInformation($"Found corresponding subscription with id {existingSubscription.id}.");
            }

            await this.notificationSubscriptions.DeleteItemAsync<NotificationSubscription>(existingSubscription.id, new PartitionKey(existingSubscription.PartitionKey));
            this.log.LogInformation($"Subscription {subscription} was deleted.");

            return true;
        }

        public async ValueTask RemoveAllUserSubscriptionsAsync(string userId)
        {
            this.log.LogInformation($"{nameof(RemoveAllUserSubscriptionsAsync)}: UserId = {userId}");

            var rows = await this.TryGetAllExistingSubscriptions(userId);
            foreach (var row in rows)
            {
                await this.notificationSubscriptions.DeleteItemAsync<NotificationSubscription>(row.id, new PartitionKey(row.PartitionKey));
            }
        }

        public async ValueTask TryNotifyAsync(string userId, NotificationMessage message)
        {
            var existingSubscriptions = await this.TryGetAllExistingSubscriptions(userId);
            if (!existingSubscriptions.Any())
            {
                this.log.LogInformation($"User {userId} does not have any notification subscriptions.");
            }

            foreach (var subscription in existingSubscriptions)
            {
                await this.TryNotifyInternalAsync(subscription, message);
            }
        }

        private async ValueTask TryNotifyInternalAsync(NotificationSubscription subscription, NotificationMessage message)
        {
            var vapidOptions = await this.GetVapidOptionsAsync();
            var pushSubscription = new PushSubscription(subscription.Url, subscription.P256dh, subscription.Auth);
            var vapidDetails = new VapidDetails(vapidOptions.Subject, vapidOptions.PublicKey, vapidOptions.PrivateKey);
            var webPushClient = new WebPushClient();

            try
            {
                var payload = JsonSerializer.Serialize(message, this.serializerOptions);
                await webPushClient.SendNotificationAsync(pushSubscription, payload, vapidDetails);
                this.log.LogInformation($"Notification for subscription {subscription} is sent.");
            }
            catch (WebPushException wpException)
            {
                if (wpException.StatusCode.Equals(HttpStatusCode.Gone))
                {
                    this.log.LogWarning($"Notification subscription {subscription} is no longer valid.");
                    await this.UnsubscribeAsync(subscription.UserId, subscription);
                }
            }
            catch (Exception ex)
            {
                this.log.LogError(ex, $"Error sending webpush notification to {subscription.UserId}.");
            }
        }

        private async ValueTask<NotificationSubscription> TryGetExistingSubscription(string userId, string Url)
        {
            var query = new QueryDefinition("SELECT NS.id, NS.PartitionKey FROM NotificationSubscriptions NS WHERE NS.UserId = @user_id AND NS.Url = @url")
                .WithParameter("@user_id", userId)
                .WithParameter("@url", Url);

            var result = new List<NotificationSubscription>();
            using (var resultSetIterator = this.notificationSubscriptions.GetItemQueryIterator<NotificationSubscription>(query))
            {
                while (resultSetIterator.HasMoreResults)
                {
                    var response = await resultSetIterator.ReadNextAsync();
                    result.AddRange(response);
                }
            }

            return result.FirstOrDefault();
        }

        private async Task<IList<NotificationSubscription>> TryGetAllExistingSubscriptions(string userId)
        {
            var query = new QueryDefinition("SELECT NS.id FROM NotificationSubscriptions NS WHERE NS.UserId = @user_id")
                .WithParameter("@user_id", userId);

            var result = new List<NotificationSubscription>();
            using (var resultSetIterator = this.notificationSubscriptions.GetItemQueryIterator<NotificationSubscription>(query))
            {
                while (resultSetIterator.HasMoreResults)
                {
                    var response = await resultSetIterator.ReadNextAsync();
                    result.AddRange(response);
                }
            }

            return result;
        }

        private static string GetNewId()
        {
            return $"{Guid.NewGuid()}";
        }

        private static string GetPartitionKey(string userId)
        {
            if (string.IsNullOrEmpty(userId))
            {
                return "-";
            }

            return userId.ToUpper().Substring(0, 1);
        }

        private async ValueTask<VapidOptions> GetVapidOptionsAsync()
        {
            if (this.options == null)
            {
                this.options = await this.InitVapidOptionsAsync();
            }

            return this.options;
        }

        private async Task<VapidOptions> InitVapidOptionsAsync()
        {
            var systemSettings = new KeyVaultSystemSettings(this.log);
            return await systemSettings.GetVapidOptionsAsync();
        }
    }
}
