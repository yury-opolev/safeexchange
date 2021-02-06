/// SafeExchange

namespace SpaceOyster.SafeExchange.Core
{
    using System;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using SpaceOyster.SafeExchange.Core.CosmosDb;

    public class SafeExchangePurge
    {
        private readonly ICosmosDbProvider cosmosDbProvider;

        public SafeExchangePurge(ICosmosDbProvider cosmosDbProvider)
        {
            this.cosmosDbProvider = cosmosDbProvider ?? throw new ArgumentNullException(nameof(cosmosDbProvider));
        }

        public async Task Run(ILogger log)
        {
            var subjectPermissions = await this.cosmosDbProvider.GetSubjectPermissionsContainerAsync();
            var objectMetadata = await this.cosmosDbProvider.GetObjectMetadataContainerAsync();

            log.LogInformation("SafeExchange-Purge triggered.");

            var metadataHelper = new MetadataHelper(objectMetadata);
            var permissionsHelper = new PermissionsHelper(subjectPermissions, null, null);
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