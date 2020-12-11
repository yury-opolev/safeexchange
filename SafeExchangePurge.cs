/// SafeExchange

namespace SpaceOyster.SafeExchange
{
    using System;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Table;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Extensions.Logging;

    public class SafeExchangePurge
    {
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
            log.LogInformation("SafeExchange-Purge triggered.");

            var metadataHelper = new MetadataHelper(objectMetadataTable);
            var permissionsHelper = new PermissionsHelper(subjectPermissionsTable, null, null);
            var keyVaultHelper = new KeyVaultHelper(Environment.GetEnvironmentVariable("STORAGE_KEYVAULT_BASEURI"), log);
            var purgeHelper = new PurgeHelper(keyVaultHelper, permissionsHelper, metadataHelper, log);

            var secretNames = await metadataHelper.GetSecretsToPurgeAsync();
            foreach (var secretName in secretNames)
            {
                log.LogInformation($"Secret '{secretName}' is to be purged.");
                await purgeHelper.TryPurgeAsync(secretName);
            }

            await Task.Delay(TimeSpan.FromSeconds(10));

            log.LogInformation($"Purging deleted secrets from keyvault.");
            var deletedSecrets = await keyVaultHelper.TryGetDeletedSecretsAsync(10000);
            foreach (var secretName in deletedSecrets)
            {
                await keyVaultHelper.TryPurgeSecretAsync(secretName);
            }
        }
    }
}