/// SafeExchange

namespace SpaceOyster.SafeExchange.Core
{
    using System;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;

    public class PurgeHelper
    {
        private KeyVaultHelper keyVaultHelper;

        private PermissionsHelper permissionsHelper;

        private MetadataHelper metadataHelper;

        private AccessRequestHelper accessRequestHelper;

        private ILogger log;

        public PurgeHelper(KeyVaultHelper keyVaultHelper, PermissionsHelper permissionsHelper, MetadataHelper metadataHelper, AccessRequestHelper accessRequestHelper, ILogger log)
        {
            this.keyVaultHelper = keyVaultHelper ?? throw new ArgumentNullException(nameof(keyVaultHelper));
            this.permissionsHelper = permissionsHelper ?? throw new ArgumentNullException(nameof(permissionsHelper));
            this.metadataHelper = metadataHelper ?? throw new ArgumentNullException(nameof(metadataHelper));
            this.accessRequestHelper = accessRequestHelper ?? throw new ArgumentNullException(nameof(accessRequestHelper));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public async Task<bool> TryPurgeAsync(string secretName)
        {
            var now = DateTime.UtcNow;
            var objectMetadata = await this.metadataHelper.GetSecretMetadataAsync(secretName);
            if ((objectMetadata?.ScheduleDestroy == true) && objectMetadata.DestroyAt <= now)
            {
                await this.DestroyAsync(secretName);
                return true;
            }

            return false;
        }

        public async Task DestroyAsync(string secretName)
        {
            this.log.LogInformation($"Destroying '{secretName}'");

            await permissionsHelper.DeleteAllPermissionsAsync(secretName);
            log.LogInformation($"All permissions to access secret '{secretName}' deleted");

            await metadataHelper.DeleteSecretMetadataAsync(secretName);
            log.LogInformation($"Metadata for secret '{secretName}' deleted");

            await keyVaultHelper.DeleteSecretAsync(secretName);
            log.LogInformation($"Secret '{secretName}' was deleted from the keyvault");

            await this.accessRequestHelper.DeleteAllAccessRequestsAsync(secretName);
            log.LogInformation($"All access requests to secret '{secretName}' were deleted");
        }
    }
}