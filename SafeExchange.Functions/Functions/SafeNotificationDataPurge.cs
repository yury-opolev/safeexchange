/// <summary>
/// SafeNotificationDataPurge
/// </summary>

namespace SafeExchange.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Purger;
    using System.Threading.Tasks;

    public class SafeNotificationDataPurge
    {
        private SafeExchangeNotificationDataPurge purgeHandler;

        private readonly ILogger log;

        public SafeNotificationDataPurge(SafeExchangeDbContext dbContext, IPurger purger, ILogger<SafePurge> log)
        {
            this.purgeHandler = new SafeExchangeNotificationDataPurge(dbContext, purger);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-NotificationDataPurge")]
        public async Task Run(
            [TimerTrigger("0 15 */6 * * *")] // every 6 hours at [hour]:15:00
            TimerInfo timer)
        {
            await this.purgeHandler.Run(this.log);
        }
    }
}
