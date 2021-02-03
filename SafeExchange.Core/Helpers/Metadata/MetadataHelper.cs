/// SafeExchange

namespace SpaceOyster.SafeExchange.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
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
                Id = secretName,
                PartitionKey = secretName,

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
            var partitionKey = new PartitionKey(secretName);
            var itemResponse = await this.objectMetadata.ReadItemAsync<ObjectMetadata>(secretName, partitionKey);
            return itemResponse.Resource;
        }

        public async Task DeleteSecretMetadataAsync(string secretName)
        {
            var existingMetadata = await this.GetSecretMetadataAsync(secretName);
            if (existingMetadata == default(ObjectMetadata))
            {
                return;
            }

            await this.objectMetadata.DeleteItemAsync<ObjectMetadata>(secretName, new PartitionKey(secretName));
        }

        public async Task<IList<string>> GetSecretsToPurgeAsync()
        {
            var now = DateTime.UtcNow;

            QueryDefinition query = new QueryDefinition("SELECT Id FROM ObjectMetadata OM WHERE OM.ScheduleDestroy = @schedule_destroy AND OM.DestroyAt <= @destroy_at")
                .WithParameter("@schedule_destroy", true)
                .WithParameter("@destroy_at", now);

            var result = new List<string>();
            using (FeedIterator<ObjectMetadata> resultSetIterator = objectMetadata.GetItemQueryIterator<ObjectMetadata>(query))
            {
                while (resultSetIterator.HasMoreResults)
                {
                    FeedResponse<ObjectMetadata> response = await resultSetIterator.ReadNextAsync();
                    result.AddRange(response.Select(om => om.Id));
                }
            }

            return result;
        }
    }
}