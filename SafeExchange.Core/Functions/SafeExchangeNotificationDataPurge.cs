/// <summary>
/// SafeExchangeNotificationDataPurge
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Purger;
    using System;

    public class SafeExchangeNotificationDataPurge
    {
        private readonly SafeExchangeDbContext dbContext;

        private readonly IPurger purger;

        public SafeExchangeNotificationDataPurge(SafeExchangeDbContext dbContext, IPurger purger)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.purger = purger ?? throw new ArgumentNullException(nameof(purger));
        }

        public async Task Run(ILogger log)
        {
            log.LogInformation($"{nameof(SafeExchangeNotificationDataPurge)} triggered.");

            var notificationDataItems = await this.GetNotificationDataToPurgeAsync();
            foreach (var notificationDataItem in notificationDataItems)
            {
                log.LogInformation($"Notification data '{notificationDataItem.Id}' is to be purged.");
                await this.purger.PurgeIfNeededAsync(string.Empty, this.dbContext);
            }
        }

        private async Task<List<WebhookNotificationData>> GetNotificationDataToPurgeAsync()
        {
            var utcNow = DateTimeProvider.UtcNow;
            var expiredNotificationData = await this.dbContext.WebhookNotificationData.Where(nd => nd.ExpireAt <= utcNow)
                .ToListAsync();

            return expiredNotificationData;
        }
    }
}
