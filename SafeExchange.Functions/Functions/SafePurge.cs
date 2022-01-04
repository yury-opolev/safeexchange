/// <summary>
/// SafePurge
/// </summary>

namespace SafeExchange.Functions
{
    using Microsoft.Azure.WebJobs;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Purger;
    using System.Threading.Tasks;

    public class SafePurge
    {
        private SafeExchangePurge purgeHandler;

        public SafePurge(SafeExchangeDbContext dbContext, IPurger purger)
        {
            this.purgeHandler = new SafeExchangePurge(dbContext, purger);
        }

        [FunctionName("SafeExchange-Purge")]
        public async Task Run(
            [TimerTrigger("0 0 */6 * * *")] // every 6 hours
            TimerInfo timer,
            ILogger log)
        {
            await this.purgeHandler.Run(log);
        }
    }
}
