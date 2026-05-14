/// <summary>
/// SafeAuditPurge — daily timer-triggered function that sweeps SecretAuditAnchors
/// whose retention window has expired.
/// </summary>

namespace SafeExchange.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Audit;
    using System;
    using System.Threading.Tasks;

    public class SafeAuditPurge
    {
        private readonly IAuditPurger purger;

        private readonly ILogger<SafeAuditPurge> log;

        public SafeAuditPurge(IAuditPurger purger, ILogger<SafeAuditPurge> log)
        {
            this.purger = purger ?? throw new ArgumentNullException(nameof(purger));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-AuditPurge")]
        public async Task Run(
            [TimerTrigger("0 0 3 * * *")] // daily at 03:00 UTC
            TimerInfo timer)
        {
            this.log.LogInformation($"{nameof(SafeAuditPurge)} triggered.");
            var purged = await this.purger.PurgeExpiredAsync();
            this.log.LogInformation($"{nameof(SafeAuditPurge)} completed; purged {purged} expired audit instance(s).");
        }
    }
}
