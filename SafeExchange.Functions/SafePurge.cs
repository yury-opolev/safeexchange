/// <summary>
/// SafePurge
/// </summary>

namespace SpaceOyster.SafeExchange.Functions
{
    using Microsoft.Azure.Cosmos.Table;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Extensions.Logging;
    using SpaceOyster.SafeExchange.Core;
    using System.Threading.Tasks;

    public class SafePurge
    {
        private SafeExchangePurge purgeHandler;

        public SafePurge()
        {
            this.purgeHandler = new SafeExchangePurge();
        }

        [FunctionName("SafeExchange-Purge")]
        public async Task Run(
            [TimerTrigger("0 0 */6 * * *")] // every 6 hours
            TimerInfo timer,
            [Table("SubjectPermissions")]
            CloudTable subjectPermissionsTable,
            [Table("ObjectMetadata")]
            CloudTable objectMetadataTable,
            ILogger log)
        {
            await this.purgeHandler.Run(subjectPermissionsTable, objectMetadataTable, log);
        }
    }
}
