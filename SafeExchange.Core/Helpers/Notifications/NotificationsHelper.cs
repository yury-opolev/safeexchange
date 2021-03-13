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
    using System.Threading.Tasks;

    public class NotificationsHelper
    {
        private Container notificationSubscriptions;

        private ILogger log;

        public NotificationsHelper(Container notificationSubscriptions, ILogger logger)
        {
            this.notificationSubscriptions = notificationSubscriptions ?? throw new ArgumentNullException(nameof(notificationSubscriptions));
            this.log = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<NotificationSubscription> SubscribeAsync(string userId, NotificationSubscription subscription)
        {
            this.log.LogInformation($"{nameof(SubscribeAsync)}: UserId = {userId}, Subscription = {subscription}");

            var existingSubscription = await this.TryGetExistingSubscription(userId, subscription.Url);
            if (existingSubscription != default(NotificationSubscription))
            {
                this.log.LogInformation($"Subscription with given endpoint already exists for {userId}.");
                return subscription;
            }

            subscription.UserId = userId;
            subscription.PartitionKey = NotificationsHelper.GetPartitionKey(userId);

            await this.notificationSubscriptions.UpsertItemAsync(subscription);
            this.log.LogInformation($"Subscription {subscription} was added.");

            return subscription;
        }

        public async Task<bool> UnsubscribeAsync(string userId, NotificationSubscription subscription)
        {
            this.log.LogInformation($"{nameof(UnsubscribeAsync)}: UserId = {userId}, Subscription = {subscription}");

            var existingSubscription = await this.TryGetExistingSubscription(userId, subscription.Url);
            if (existingSubscription == default(NotificationSubscription))
            {
                this.log.LogInformation($"Subscription with given endpoint not exists for {userId}.");
                return true;
            }

            await this.notificationSubscriptions.DeleteItemAsync<NotificationSubscription>(existingSubscription.id, new PartitionKey(existingSubscription.PartitionKey));
            this.log.LogInformation($"Subscription {subscription} was deleted.");

            return true;
        }

        public async Task RemoveAllUserSubscriptionsAsync(string userId)
        {
            this.log.LogInformation($"{nameof(RemoveAllUserSubscriptionsAsync)}: UserId = {userId}");

            var rows = await this.TryGetAllExistingSubscriptions(userId);
            foreach (var row in rows)
            {
                await this.notificationSubscriptions.DeleteItemAsync<NotificationSubscription>(row.id, new PartitionKey(row.PartitionKey));
            }
        }

        public async Task TryNotifyAsync(string userId, NotificationMessage message)
        {
            await Task.CompletedTask;
        }

        private async Task<NotificationSubscription> TryGetExistingSubscription(string userId, string Url)
        {
            var query = new QueryDefinition("SELECT NS.id FROM NotificationSubscriptions NS WHERE NS.UserId = @user_id AND NS.Url = @url")
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

        private static string GetPartitionKey(string userId)
        {
            if (string.IsNullOrEmpty(userId))
            {
                return "-";
            }

            return userId.ToUpper().Substring(0, 1);
        }
    }
}
