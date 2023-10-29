/// <summary>
/// MigrationsHelper
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using Azure;
    using Microsoft.Azure.Cosmos;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Model;
    using System;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;

    internal class MigrationsHelper : IMigrationsHelper
    {
        private readonly CosmosDbConfiguration dbConfiguration;

        private readonly CosmosDbKeys dbKeys;

        private readonly ILogger log;

        public MigrationsHelper(CosmosDbConfiguration dbConfiguration, CosmosDbKeys dbKeys, ILogger<MigrationsHelper> log)
        {
            this.dbConfiguration = dbConfiguration ?? throw new ArgumentNullException(nameof(dbConfiguration));
            this.dbKeys = dbKeys ?? throw new ArgumentNullException(nameof(dbKeys));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public async Task RunMigrationAsync(string migrationId)
        {
            this.log.LogInformation($"Starting migration '{migrationId}'.");

            try
            {
                if ("00001".Equals(migrationId, StringComparison.InvariantCultureIgnoreCase))
                {
                    await this.RunMigration00001Async();
                    return;
                }

                if ("00002".Equals(migrationId, StringComparison.InvariantCultureIgnoreCase))
                {
                    await this.RunMigration00002Async();
                }

                if ("00003".Equals(migrationId, StringComparison.InvariantCultureIgnoreCase))
                {
                    await this.RunMigration00003Async();
                }
            }
            finally
            {
                this.log.LogInformation($"Migration '{migrationId}' finished.");
            }
        }

        private async Task RunMigration00001Async()
        {
            using CosmosClient client = new CosmosClient(this.dbConfiguration.CosmosDbEndpoint, new AzureKeyCredential(this.dbKeys.PrimaryKey));
            var database = client.GetDatabase(this.dbConfiguration.DatabaseName);
            var container = database.GetContainer(nameof(SubjectPermissions));

            var query = new QueryDefinition("SELECT * FROM c WHERE NOT CONTAINS(c.id, @subStr1) AND NOT CONTAINS(c.id, @subStr2)")
                .WithParameter("@subStr1", "|0|")
                .WithParameter("@subStr2", "|100|");

            using FeedIterator<MigrationItem00001> feed =
                container.GetItemQueryIterator<MigrationItem00001>(queryDefinition: query);

            while (feed.HasMoreResults)
            {
                FeedResponse<MigrationItem00001> response = await feed.ReadNextAsync();
                foreach (MigrationItem00001 item in response)
                {
                    if (string.IsNullOrEmpty(item.id) || Regex.Matches(item.id, "[|]").Count != 1)
                    {
                        this.log.LogInformation($"Skipping item '{item.id}', {nameof(item.id)} is not suitable for migration.");
                        continue;
                    }

                    await MigrateItem00001Async(container, item);
                }
            }
        }

        private async Task RunMigration00002Async()
        {
            using CosmosClient client = new CosmosClient(this.dbConfiguration.CosmosDbEndpoint, new AzureKeyCredential(this.dbKeys.PrimaryKey));
            var database = client.GetDatabase(this.dbConfiguration.DatabaseName);
            var container = database.GetContainer(nameof(ObjectMetadata));

            var query = new QueryDefinition("SELECT * FROM c WHERE NOT IS_DEFINED(c.ExpireIfUnusedAt)");

            using FeedIterator<MigrationItem00002> feed =
                container.GetItemQueryIterator<MigrationItem00002>(queryDefinition: query);

            while (feed.HasMoreResults)
            {
                FeedResponse<MigrationItem00002> response = await feed.ReadNextAsync();
                foreach (MigrationItem00002 item in response)
                {
                    await MigrateItem00002Async(container, item);
                }
            }
        }

        private async Task RunMigration00003Async()
        {
            using CosmosClient client = new CosmosClient(this.dbConfiguration.CosmosDbEndpoint, new AzureKeyCredential(this.dbKeys.PrimaryKey));
            var database = client.GetDatabase(this.dbConfiguration.DatabaseName);
            var container = database.GetContainer("Users");

            var query = new QueryDefinition("SELECT * FROM c WHERE NOT IS_DEFINED(c.ReceiveExternalNotifications)");

            using FeedIterator<MigrationItem00003> feed =
                container.GetItemQueryIterator<MigrationItem00003>(queryDefinition: query);

            while (feed.HasMoreResults)
            {
                FeedResponse<MigrationItem00003> response = await feed.ReadNextAsync();
                foreach (MigrationItem00003 item in response)
                {
                    await MigrateItem00003Async(container, item);
                }
            }
        }

        private async Task MigrateItem00001Async(Container container, MigrationItem00001 item)
        {
            this.log.LogInformation($"{nameof(MigrateItem00002Async)}, item '{item.id}'.");

            MigrationItem00001 newItem = new MigrationItem00001(item);

            newItem.SubjectType = 0;
            newItem.id = item.id.Replace("|", $"|{item.SubjectType}|");

            try
            {
                await container.UpsertItemAsync(newItem);
                await container.DeleteItemAsync<MigrationItem00001>(item.id, new PartitionKey(item.PartitionKey));
            }
            catch
            {
                // no-op
            }
        }

        private async Task MigrateItem00002Async(Container container, MigrationItem00002 item)
        {
            this.log.LogInformation($"{nameof(MigrateItem00002Async)}, item '{item.id}'.");

            var response = await container.ReadItemStreamAsync(item.id, new PartitionKey(item.PartitionKey));
            using var streamReader = new StreamReader(response.Content);

            var contentString = await streamReader.ReadToEndAsync();
            var newContentString = contentString.Replace("\"KeepInStorage", "\"ExpireIfUnusedAt\": \"0001-01-01T00:00:00\", \"KeepInStorage");

            using var updatedItemStream = new MemoryStream(Encoding.UTF8.GetBytes(newContentString));
            using var updateResponse = await container.ReplaceItemStreamAsync(updatedItemStream, item.id, new PartitionKey(item.PartitionKey));

            this.log.LogInformation($"Item '{item.id}' migration {(updateResponse.IsSuccessStatusCode ? "successful" : "unsuccessful")}.");
        }

        private async Task MigrateItem00003Async(Container container, MigrationItem00003 item)
        {
            this.log.LogInformation($"{nameof(MigrateItem00003Async)}, item '{item.id}'.");

            var response = await container.ReadItemStreamAsync(item.id, new PartitionKey(item.PartitionKey));
            using var streamReader = new StreamReader(response.Content);

            var contentString = await streamReader.ReadToEndAsync();
            var newContentString = contentString.Replace("\"Enabled", "\"ReceiveExternalNotifications\": true, \"Enabled");

            using var updatedItemStream = new MemoryStream(Encoding.UTF8.GetBytes(newContentString));
            using var updateResponse = await container.ReplaceItemStreamAsync(updatedItemStream, item.id, new PartitionKey(item.PartitionKey));

            this.log.LogInformation($"Item '{item.id}' migration {(updateResponse.IsSuccessStatusCode ? "successful" : "unsuccessful")}.");
        }
    }
}
