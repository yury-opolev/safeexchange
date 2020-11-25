/// SafeExchange

namespace SpaceOyster.SafeExchange
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Table;

    public class MetadataHelper
    {
        private CloudTable objectMetadataTable;

        private bool initialized = false;

        public MetadataHelper(CloudTable objectMetadataTable)
        {
            this.objectMetadataTable = objectMetadataTable ?? throw new ArgumentNullException(nameof(objectMetadataTable));
        }

        public async Task InitializeAsync()
        {
            if (this.initialized)
            {
                return;
            }

            await this.objectMetadataTable.CreateIfNotExistsAsync();
            this.initialized = true;
        }

        public async Task SetSecretMetadataAsync(string secretName, string setBy, bool setDestroyValues, bool destroyAfterRead, bool scheduleDestroy, DateTime destroyAt)
        {
            var now = DateTime.UtcNow;

            await this.InitializeAsync();
            var existingRow = await this.objectMetadataTable
                .ExecuteAsync(TableOperation.Retrieve<ObjectMetadata>(
                    MetadataHelper.GetPartitionKey(secretName),
                    MetadataHelper.GetRowKey()));

            var createdAt = now;
            var createdBy = setBy;

            var newDestroyAfterRead = setDestroyValues ? destroyAfterRead : false;
            var newScheduleDestroy = setDestroyValues ? scheduleDestroy : false;
            var newDestroyAt = setDestroyValues ? destroyAt : now;

            if (existingRow.Result is ObjectMetadata existingMetadata)
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
                PartitionKey = MetadataHelper.GetPartitionKey(secretName),
                RowKey = MetadataHelper.GetRowKey(),

                CreatedAt = createdAt,
                CreatedBy = createdBy,

                ModifiedAt = now,
                ModifiedBy = setBy,

                DestroyAfterRead = newDestroyAfterRead,
                ScheduleDestroy = newScheduleDestroy,
                DestroyAt = newDestroyAt
            };

            await this.objectMetadataTable.ExecuteAsync(TableOperation.InsertOrMerge(objectMetadata));
        }

        public async Task<ObjectMetadata> GetSecretMetadataAsync(string secretName)
        {
            await this.InitializeAsync();
            var existingRow = await this.objectMetadataTable
                .ExecuteAsync(TableOperation.Retrieve<ObjectMetadata>(
                    MetadataHelper.GetPartitionKey(secretName),
                    MetadataHelper.GetRowKey()));

            if (existingRow.Result is ObjectMetadata existingMetadata)
            {
                return existingMetadata;
            }

            return default(ObjectMetadata);
        }

        public async Task DeleteSecretMetadataAsync(string secretName)
        {
            await this.InitializeAsync();
            var existingRow = await this.objectMetadataTable
                .ExecuteAsync(TableOperation.Retrieve<ObjectMetadata>(
                    MetadataHelper.GetPartitionKey(secretName),
                    MetadataHelper.GetRowKey()));

            if (!(existingRow.Result is ObjectMetadata existingMetadata))
            {
                return;
            }

            await this.objectMetadataTable.ExecuteAsync(TableOperation.Delete(existingMetadata));
        }

        public async Task<IList<string>> GetSecretsToPurgeAsync()
        {
            var now = DateTime.UtcNow;
            var result = new List<string>();

            var scheduleDestroyFilter = TableQuery.GenerateFilterConditionForBool("ScheduleDestroy", QueryComparisons.Equal, true);
            var destroyAtFilter = TableQuery.GenerateFilterConditionForDate("DestroyAt", QueryComparisons.LessThanOrEqual, now);

            var query = new TableQuery<ObjectMetadata>()
                .Where(TableQuery.CombineFilters(scheduleDestroyFilter, TableOperators.And, destroyAtFilter))
                .Select(new string[] { "PartitionKey" });
            TableContinuationToken continuationToken = null;

            do
            {
                var page = await this.objectMetadataTable.ExecuteQuerySegmentedAsync(query, continuationToken);
                continuationToken = page.ContinuationToken;
                if (page.Results != null)
                {
                    foreach (var row in page.Results)
                    {
                        var secretName = Base64Helper.Base64ToString(row.PartitionKey);
                        result.Add(secretName);
                    }
                }
            }
            while (continuationToken != null);

            return result;
        }

        private static string GetPartitionKey(string secretName)
        {
            return Base64Helper.StringToBase64(secretName);
        }

        private static string GetRowKey()
        {
            return string.Empty;
        }
    }
}