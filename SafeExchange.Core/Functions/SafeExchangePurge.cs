/// <summary>
/// SafeExchangePurge
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Purger;
    using System;

    public class SafeExchangePurge
    {
        private readonly SafeExchangeDbContext dbContext;

        private readonly IPurger purger;

        public SafeExchangePurge(SafeExchangeDbContext dbContext, IPurger purger)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.purger = purger ?? throw new ArgumentNullException(nameof(purger));
        }

        public async Task Run(ILogger log)
        {
            log.LogInformation($"{nameof(SafeExchangePurge)} triggered.");

            var secrets = await this.GetSecretsToPurgeAsync();
            foreach (var secret in secrets)
            {
                log.LogInformation($"Secret '{secret.ObjectName}' is to be purged.");
                await this.purger.PurgeIfNeededAsync(secret.ObjectName, this.dbContext);
            }
        }

        private async Task<List<ObjectMetadata>> GetSecretsToPurgeAsync()
        {
            var utcNow = DateTimeProvider.UtcNow;
            var expiredSecrets = await this.dbContext.Objects.Where(o =>
                o.KeepInStorage &&
                    ((o.ExpirationMetadata.ScheduleExpiration && o.ExpirationMetadata.ExpireAt <= utcNow) ||
                    (o.ExpirationMetadata.ExpireOnIdleTime && o.ExpireIfUnusedAt <= utcNow)))
                .ToListAsync();

            return expiredSecrets;
        }
    }
}
