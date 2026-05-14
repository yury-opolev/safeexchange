/// <summary>
/// AuditPurger — sweeps SecretAuditAnchors whose RetentionExpiresAt has elapsed,
/// removing the anchor and all events in its partition.
/// </summary>

namespace SafeExchange.Core.Audit
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using System;
    using System.Linq;
    using System.Threading.Tasks;

    public sealed class AuditPurger : IAuditPurger
    {
        private readonly SafeExchangeDbContext dbContext;

        private readonly ILogger<AuditPurger> log;

        public AuditPurger(SafeExchangeDbContext dbContext, ILogger<AuditPurger> log)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public async Task<int> PurgeExpiredAsync()
        {
            var now = DateTimeProvider.UtcNow;
            var expiredAnchors = await this.dbContext.SecretAuditAnchors
                .Where(a => a.RetentionExpiresAt != null && a.RetentionExpiresAt <= now && a.DeletedAt != null)
                .ToListAsync();

            var deletedCount = 0;
            foreach (var anchor in expiredAnchors)
            {
                var events = await this.dbContext.SecretAuditEvents
                    .Where(e => e.AuditInstanceId == anchor.AuditInstanceId)
                    .ToListAsync();
                this.dbContext.SecretAuditEvents.RemoveRange(events);
                this.dbContext.SecretAuditAnchors.Remove(anchor);
                await this.dbContext.SaveChangesAsync();
                this.log.LogInformation($"Purged audit instance {anchor.AuditInstanceId}: {events.Count} event(s) and anchor removed.");
                deletedCount++;
            }
            return deletedCount;
        }
    }
}
