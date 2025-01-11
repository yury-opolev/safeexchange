/// <summary>
/// MigrationsHelper
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using AngleSharp.Dom;
    using Azure;
    using Azure.Core;
    using Azure.Identity;
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
        private readonly TokenCredential tokenCredential;

        private readonly CosmosDbConfiguration dbConfiguration;

        private readonly ILogger log;

        public MigrationsHelper(CosmosDbConfiguration dbConfiguration, ILogger<MigrationsHelper> log)
        {
            this.tokenCredential = new DefaultAzureCredential();
            this.dbConfiguration = dbConfiguration ?? throw new ArgumentNullException(nameof(dbConfiguration));
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

                if ("00004".Equals(migrationId, StringComparison.InvariantCultureIgnoreCase))
                {
                    await this.RunMigration00004Async();
                }

                if ("00005".Equals(migrationId, StringComparison.InvariantCultureIgnoreCase))
                {
                    await this.RunMigration00005Async();
                }

                if ("00006".Equals(migrationId, StringComparison.InvariantCultureIgnoreCase))
                {
                    await this.RunMigration00006Async();
                }

                this.log.LogInformation($"Migration '{migrationId}' does not exist, skipping any actions.");
            }
            finally
            {
                this.log.LogInformation($"Migration '{migrationId}' finished.");
            }
        }

        private async Task RunMigration00001Async()
        {
            using CosmosClient client = new CosmosClient(this.dbConfiguration.CosmosDbEndpoint, this.tokenCredential);
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
            using CosmosClient client = new CosmosClient(this.dbConfiguration.CosmosDbEndpoint, this.tokenCredential);
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
            using CosmosClient client = new CosmosClient(this.dbConfiguration.CosmosDbEndpoint, this.tokenCredential);
            var database = client.GetDatabase(this.dbConfiguration.DatabaseName);

            var container1 = database.GetContainer("Users");
            var query1 = new QueryDefinition("SELECT * FROM c WHERE NOT IS_DEFINED(c.ReceiveExternalNotifications)");
            using FeedIterator<MigrationItem00003> feed1 =
                container1.GetItemQueryIterator<MigrationItem00003>(queryDefinition: query1);

            while (feed1.HasMoreResults)
            {
                FeedResponse<MigrationItem00003> response1 = await feed1.ReadNextAsync();
                foreach (MigrationItem00003 item1 in response1)
                {
                    await MigrateItem00003_01_Async(container1, item1);
                }
            }

            var container2 = database.GetContainer("Applications");
            var query2 = new QueryDefinition("SELECT * FROM c WHERE NOT IS_DEFINED(c.ExternalNotificationsReader)");
            using FeedIterator<MigrationItem00003> feed2 =
                container2.GetItemQueryIterator<MigrationItem00003>(queryDefinition: query2);

            while (feed2.HasMoreResults)
            {
                FeedResponse<MigrationItem00003> response2 = await feed2.ReadNextAsync();
                foreach (MigrationItem00003 item2 in response2)
                {
                    await MigrateItem00003_02_Async(container2, item2);
                }
            }
        }

        private async Task RunMigration00004Async()
        {
            using CosmosClient client = new CosmosClient(this.dbConfiguration.CosmosDbEndpoint, this.tokenCredential);
            var database = client.GetDatabase(this.dbConfiguration.DatabaseName);

            // TODO: migrate existing subject permissions to add new subject type 'group'
            var container1 = database.GetContainer("GroupDictionary");
            var query1 = new QueryDefinition("SELECT * FROM c WHERE NOT IS_DEFINED(c.DisplayName)");
            using FeedIterator<MigrationItem00004_1> feed1 =
                container1.GetItemQueryIterator<MigrationItem00004_1>(queryDefinition: query1);

            var groupMails = new List<string>();
            while (feed1.HasMoreResults)
            {
                FeedResponse<MigrationItem00004_1> response1 = await feed1.ReadNextAsync();
                foreach (MigrationItem00004_1 item1 in response1)
                {
                    await MigrateItem00004_01_Async(container1, item1);
                    groupMails.Add(item1.GroupMail);
                }
            }

            var container2 = database.GetContainer("SubjectPermissions");
            var query2 = new QueryDefinition("SELECT * FROM c WHERE c.SubjectType = 0");
            using FeedIterator<MigrationItem00004_2> feed2 =
                container2.GetItemQueryIterator<MigrationItem00004_2>(queryDefinition: query2);

            while (feed2.HasMoreResults)
            {
                FeedResponse<MigrationItem00004_2> response2 = await feed2.ReadNextAsync();
                foreach (MigrationItem00004_2 item2 in response2)
                {
                    if (groupMails.Contains(item2.SubjectName))
                    {
                        await MigrateItem00004_02_Async(container2, item2);
                    }
                }
            }
        }

        private async Task RunMigration00005Async()
        {
            using CosmosClient client = new CosmosClient(this.dbConfiguration.CosmosDbEndpoint, this.tokenCredential);
            var database = client.GetDatabase(this.dbConfiguration.DatabaseName);
            var container = database.GetContainer("GroupDictionary");
            var query = new QueryDefinition("SELECT * FROM c WHERE NOT IS_DEFINED(c.LastUsedAt)");

            using FeedIterator<MigrationItem00005> feed =
                container.GetItemQueryIterator<MigrationItem00005>(queryDefinition: query);

            while (feed.HasMoreResults)
            {
                FeedResponse<MigrationItem00005> response = await feed.ReadNextAsync();
                foreach (MigrationItem00005 item in response)
                {
                    await MigrateItem00005Async(container, item);
                }
            }
        }

        private async Task RunMigration00006Async()
        {
            using CosmosClient client = new CosmosClient(this.dbConfiguration.CosmosDbEndpoint, this.tokenCredential);
            var database = client.GetDatabase(this.dbConfiguration.DatabaseName);

            var container = database.GetContainer("SubjectPermissions");
            var query = new QueryDefinition("SELECT * FROM c WHERE NOT IS_DEFINED(c.SubjectId)");
            using FeedIterator<MigrationItem00006_1> feed =
                container.GetItemQueryIterator<MigrationItem00006_1>(queryDefinition: query);
            while (feed.HasMoreResults)
            {
                FeedResponse<MigrationItem00006_1> response = await feed.ReadNextAsync();
                foreach (MigrationItem00006_1 item in response)
                {
                    await MigrateItem00006_1_Async(container, item);
                }
            }

            var container2 = database.GetContainer("AccessRequests");
            var query2 = new QueryDefinition("SELECT * FROM c");
            using FeedIterator<MigrationItem00006_2> feed2 =
                container2.GetItemQueryIterator<MigrationItem00006_2>(queryDefinition: query2);
            while (feed2.HasMoreResults)
            {
                FeedResponse<MigrationItem00006_2> response2 = await feed2.ReadNextAsync();
                foreach (MigrationItem00006_2 item2 in response2)
                {
                    // TODO: set SubjectId from SubjectName for request recipients
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

        private async Task MigrateItem00003_01_Async(Container container, MigrationItem00003 item)
        {
            this.log.LogInformation($"{nameof(MigrateItem00003_01_Async)}, item '{item.id}'.");

            var response = await container.ReadItemStreamAsync(item.id, new PartitionKey(item.PartitionKey));
            using var streamReader = new StreamReader(response.Content);

            var contentString = await streamReader.ReadToEndAsync();
            var newContentString = contentString.Replace("\"Enabled", "\"ReceiveExternalNotifications\": true, \"Enabled");

            using var updatedItemStream = new MemoryStream(Encoding.UTF8.GetBytes(newContentString));
            using var updateResponse = await container.ReplaceItemStreamAsync(updatedItemStream, item.id, new PartitionKey(item.PartitionKey));

            this.log.LogInformation($"Item '{item.id}' migration {(updateResponse.IsSuccessStatusCode ? "successful" : "unsuccessful")}.");
        }

        private async Task MigrateItem00004_01_Async(Container container, MigrationItem00004_1 item)
        {
            this.log.LogInformation($"{nameof(MigrateItem00004_01_Async)}, item '{item.id}'.");

            var response = await container.ReadItemStreamAsync(item.id, new PartitionKey(item.PartitionKey));
            using var streamReader = new StreamReader(response.Content);

            var contentString = await streamReader.ReadToEndAsync();
            var newContentString = contentString.Replace("\"GroupMail", $"\"DisplayName\": \"{item.GroupMail}\", \"GroupMail");

            using var updatedItemStream = new MemoryStream(Encoding.UTF8.GetBytes(newContentString));
            using var updateResponse = await container.ReplaceItemStreamAsync(updatedItemStream, item.id, new PartitionKey(item.PartitionKey));

            this.log.LogInformation($"Item '{item.id}' migration {(updateResponse.IsSuccessStatusCode ? "successful" : "unsuccessful")}.");
        }

        private async Task MigrateItem00004_02_Async(Container container, MigrationItem00004_2 permissionsItem)
        {
            this.log.LogInformation($"{nameof(MigrateItem00004_02_Async)}, item '{permissionsItem.id}'.");

            var response = await container.ReadItemStreamAsync(permissionsItem.id, new PartitionKey(permissionsItem.PartitionKey));
            using var streamReader = new StreamReader(response.Content);

            var newPermissionsItem = new MigrationItem00004_2(permissionsItem)
            {
                SubjectType = 1,
                id = $"{permissionsItem.SecretName}|{1}|{permissionsItem.SubjectName}"
            };

            var contentString = await streamReader.ReadToEndAsync();

            var newContentString = contentString.Replace("\"SubjectType\":0,", "\"SubjectType\":1,");
            newContentString = newContentString.Replace(permissionsItem.id, newPermissionsItem.id);

            using var updatedItemStream = new MemoryStream(Encoding.UTF8.GetBytes(newContentString));
            using var updateResponse = await container.ReplaceItemStreamAsync(updatedItemStream, permissionsItem.id, new PartitionKey(permissionsItem.PartitionKey));

            this.log.LogInformation($"Item '{permissionsItem.id}' -> '{newPermissionsItem.id}', replace {(updateResponse.IsSuccessStatusCode ? "successful" : "unsuccessful")}.");
        }

        private async Task MigrateItem00003_02_Async(Container container, MigrationItem00003 item)
        {
            this.log.LogInformation($"{nameof(MigrateItem00003_02_Async)}, item '{item.id}'.");

            var response = await container.ReadItemStreamAsync(item.id, new PartitionKey(item.PartitionKey));
            using var streamReader = new StreamReader(response.Content);

            var contentString = await streamReader.ReadToEndAsync();
            var newContentString = contentString.Replace("\"Enabled", "\"ExternalNotificationsReader\": false, \"Enabled");

            using var updatedItemStream = new MemoryStream(Encoding.UTF8.GetBytes(newContentString));
            using var updateResponse = await container.ReplaceItemStreamAsync(updatedItemStream, item.id, new PartitionKey(item.PartitionKey));

            this.log.LogInformation($"Item '{item.id}' migration {(updateResponse.IsSuccessStatusCode ? "successful" : "unsuccessful")}.");
        }

        private async Task MigrateItem00005Async(Container container, MigrationItem00005 item)
        {
            this.log.LogInformation($"{nameof(MigrateItem00005Async)}, item '{item.id}'.");

            var response = await container.ReadItemStreamAsync(item.id, new PartitionKey(item.PartitionKey));
            using var streamReader = new StreamReader(response.Content);

            var contentString = await streamReader.ReadToEndAsync();
            var newContentString = contentString.Replace("\"CreatedAt", "\"LastUsedAt\": \"0001-01-01T00:00:00\", \"CreatedAt");

            using var updatedItemStream = new MemoryStream(Encoding.UTF8.GetBytes(newContentString));
            using var updateResponse = await container.ReplaceItemStreamAsync(updatedItemStream, item.id, new PartitionKey(item.PartitionKey));

            this.log.LogInformation($"Item '{item.id}' migration {(updateResponse.IsSuccessStatusCode ? "successful" : "unsuccessful")}.");
        }

        private async Task MigrateItem00006_1_Async(Container container, MigrationItem00006_1 item)
        {
            this.log.LogInformation($"{nameof(MigrateItem00006_1_Async)}, item '{item.id}'.");

            MigrationItem00006_1 newItem = new MigrationItem00006_1(item);

            newItem.SubjectId = item.SubjectName;

            try
            {
                await container.UpsertItemAsync(newItem);
                this.log.LogInformation($"Item '{item.id}' migration finished successfully.");
            }
            catch (Exception ex)
            {
                // no-op
                this.log.LogWarning($"Item '{item.id}' migration finished with {ex.GetType()}: '{ex.Message}'.");
            }
        }
    }
}
