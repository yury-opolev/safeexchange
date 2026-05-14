/// <summary>
/// SafeExchangePurge
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Purger;
    using System;

    public class SafeExchangePurge
    {
        private readonly Features features;

        private readonly SafeExchangeDbContext dbContext;

        private readonly IPurger purger;

        private readonly IAuditWriter auditWriter;

        public SafeExchangePurge(IConfiguration configuration, SafeExchangeDbContext dbContext, IPurger purger, IAuditWriter auditWriter)
        {
            if (configuration is null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            this.features = new Features();
            configuration.GetSection("Features").Bind(this.features);

            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.purger = purger ?? throw new ArgumentNullException(nameof(purger));
            this.auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        }

        public async Task Run(ILogger log)
        {
            log.LogInformation($"{nameof(SafeExchangePurge)} triggered.");

            var secrets = await this.GetSecretsToPurgeAsync();
            foreach (var secret in secrets)
            {
                log.LogInformation($"Secret '{secret.ObjectName}' is to be purged.");
                await this.purger.PurgeIfNeededAsync(secret.ObjectName, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);
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
