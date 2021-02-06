/// SafeExchange

namespace SpaceOyster.SafeExchange.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos;

    public class MetadataHelper
    {
        private Container objectMetadata;

        public MetadataHelper(Container objectMetadata)
        {
            this.objectMetadata = objectMetadata ?? throw new ArgumentNullException(nameof(objectMetadata));
        }

        public async Task SetSecretMetadataAsync(string secretName, string setBy, bool setDestroyValues, bool destroyAfterRead, bool scheduleDestroy, DateTime destroyAt)
        {
            var now = DateTime.UtcNow;

            var createdAt = now;
            var createdBy = setBy;

            var newDestroyAfterRead = setDestroyValues ? destroyAfterRead : false;
            var newScheduleDestroy = setDestroyValues ? scheduleDestroy : false;
            var newDestroyAt = setDestroyValues ? destroyAt : now;

            var existingMetadata = await this.GetSecretMetadataAsync(secretName);
            if (existingMetadata != default(ObjectMetadata))
            {
                createdAt = existingMetadata.CreatedAt;
                createdBy = existingMetadata.CreatedBy;

                if (!setDestroyValues)
                {
                    newDestroyAfterRead = existingMetadata.DestroyAfterRead;
                    newScheduleDestroy = existingMetadata.ScheduleDestroy;
                    newDestroyAt = existingMetadata.DestroyAt;
                }
            }

            var objectMetadata = new ObjectMetadata()
            {
                id = secretName,
                PartitionKey = MetadataHelper.GetPartitionKey(secretName),

                ObjectName = secretName,

                CreatedAt = createdAt,
                CreatedBy = createdBy,

                ModifiedAt = now,
                ModifiedBy = setBy,

                DestroyAfterRead = newDestroyAfterRead,
                ScheduleDestroy = newScheduleDestroy,
                DestroyAt = newDestroyAt
            };

            await this.objectMetadata.UpsertItemAsync(objectMetadata);
        }

        public async Task<ObjectMetadata> GetSecretMetadataAsync(string secretName)
        {
            var partitionKey = new PartitionKey(MetadataHelper.GetPartitionKey(secretName));

            try
            {
                var itemResponse = await this.objectMetadata.ReadItemAsync<ObjectMetadata>(secretName, partitionKey);
                return itemResponse.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return default(ObjectMetadata);
            }
        }

        public async Task DeleteSecretMetadataAsync(string secretName)
        {
            var existingMetadata = await this.GetSecretMetadataAsync(secretName);
            if (existingMetadata == default(ObjectMetadata))
            {
                return;
            }

            await this.objectMetadata.DeleteItemAsync<ObjectMetadata>(secretName, new PartitionKey(MetadataHelper.GetPartitionKey(secretName)));
        }

        public async Task<IList<string>> GetSecretsToPurgeAsync()
        {
            var now = DateTime.UtcNow;

            var query = new QueryDefinition("SELECT id FROM ObjectMetadata OM WHERE OM.ScheduleDestroy = @schedule_destroy AND OM.DestroyAt <= @destroy_at")
                .WithParameter("@schedule_destroy", true)
                .WithParameter("@destroy_at", now);

            var result = new List<string>();
            using (var resultSetIterator = objectMetadata.GetItemQueryIterator<ObjectMetadata>(query))
            {
                while (resultSetIterator.HasMoreResults)
                {
                    var response = await resultSetIterator.ReadNextAsync();
                    result.AddRange(response.Select(om => om.id));
                }
            }

            return result;
        }

        private static string GetPartitionKey(string secretName)
        {
            if (string.IsNullOrEmpty(secretName))
            {
                return "-";
            }

            return secretName.ToUpper().Substring(0, 1);
        }
    }
}