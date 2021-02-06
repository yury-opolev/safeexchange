/// <summary>
/// SafePurge
/// </summary>

namespace SpaceOyster.SafeExchange.Functions
{
    using Microsoft.Azure.WebJobs;
    using Microsoft.Extensions.Logging;
    using SpaceOyster.SafeExchange.Core;
    using SpaceOyster.SafeExchange.Core.CosmosDb;
    using System.Threading.Tasks;

    public class SafePurge
    {
        private SafeExchangePurge purgeHandler;

        public SafePurge(ICosmosDbProvider cosmosDbProvider)
        {
            this.purgeHandler = new SafeExchangePurge(cosmosDbProvider);
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
