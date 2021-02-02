/// <summary>
/// SafeExchange
/// </summary>

namespace SafeExchange.Core.Helpers.CosmosDb
{
    using Microsoft.Azure.Cosmos;
    using Microsoft.Azure.Services.AppAuthentication;
    using Microsoft.Extensions.Logging;
    using System;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text.Json;
    using System.Threading.Tasks;

    public class CosmosDbProvider
    {
        private bool initialized;

        private readonly ILogger log;

        private readonly CosmosDbProviderSettings settings;

        private DatabaseAccountListKeysResult databaseKeys;

        public CosmosDbProvider(ILogger log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));

            var settingsEnvironmentVariableName = "COSMOS_DB_SETTINGS";
            var settingsJson = Environment.GetEnvironmentVariable(settingsEnvironmentVariableName);

            if (string.IsNullOrEmpty(settingsJson))
            {
                throw new ArgumentException($"{nameof(settingsJson)} is empty, check configuration value for '{settingsEnvironmentVariableName}'.");
            }

            this.settings = JsonSerializer.Deserialize<CosmosDbProviderSettings>(settingsJson);

            this.log.LogInformation($"{nameof(CosmosDbProvider)} instantiated with settings: {settingsJson}");
        }

        public async ValueTask<Container> GetObjectMetadataContainerAsync()
        {
            await this.InitializeAsync();
            return await this.GetContainerInternalAsync(CosmosDbProviderSettings.ObjectMetadataContainerName);
        }

        public async ValueTask<Container> GetSubjectPermissionsContainerAsync()
        {
            await this.InitializeAsync();
            return await this.GetContainerInternalAsync(CosmosDbProviderSettings.SubjectPermissionsContainerName);
        }

        public async ValueTask<Container> GetGroupDictionaryContainerAsync()
        {
            await this.InitializeAsync();
            return await this.GetContainerInternalAsync(CosmosDbProviderSettings.GroupDictionaryContainerName);
        }

        private async ValueTask<Container> GetContainerInternalAsync(string containerName)
        {
            await RetrieveCosmosDbKeysAsync();

            try
            {
                var client = new CosmosClient(this.settings.CosmosDbEndpoint, this.databaseKeys.primaryMasterKey);
                return client.GetContainer(this.settings.DatabaseName, containerName);
            }
            catch (CosmosException cosmosException)
            {
                log.LogError($"{nameof(GetContainerInternalAsync)} threw {nameof(CosmosException)}: [{cosmosException.StatusCode}.{cosmosException.SubStatusCode}], {cosmosException.Message}", cosmosException);
            }
            catch (Exception exception)
            {
                log.LogError($"{nameof(GetContainerInternalAsync)} threw {exception.GetType()}: {exception.Message}", exception);
            }

            return null;
        }

        private async ValueTask InitializeAsync()
        {
            if (this.initialized)
            {
                return;
            }

            await this.RetrieveCosmosDbKeysAsync();
            try
            {
                var client = new CosmosClient(this.settings.CosmosDbEndpoint, this.databaseKeys.primaryMasterKey);
                var databaseResponse = await client.CreateDatabaseIfNotExistsAsync(this.settings.DatabaseName);

                await databaseResponse.Database.CreateContainerIfNotExistsAsync(CosmosDbProviderSettings.ObjectMetadataContainerName, "/PartitionKey");
                await databaseResponse.Database.CreateContainerIfNotExistsAsync(CosmosDbProviderSettings.SubjectPermissionsContainerName, "/PartitionKey");
                await databaseResponse.Database.CreateContainerIfNotExistsAsync(CosmosDbProviderSettings.GroupDictionaryContainerName, "/PartitionKey");
            }
            catch (CosmosException cosmosException)
            {
                log.LogError($"{nameof(GetContainerInternalAsync)} threw {nameof(CosmosException)}: [{cosmosException.StatusCode}.{cosmosException.SubStatusCode}], {cosmosException.Message}", cosmosException);
            }
            catch (Exception exception)
            {
                log.LogError($"{nameof(GetContainerInternalAsync)} threw {exception.GetType()}: {exception.Message}", exception);
            }

            this.initialized = true;
        }

        private async ValueTask RetrieveCosmosDbKeysAsync()
        {
            if (this.databaseKeys != null)
            {
                return;
            }

            log.LogInformation($"{nameof(RetrieveCosmosDbKeysAsync)}...");

            try
            {
                var azureServiceTokenProvider = new AzureServiceTokenProvider();
                string accessToken = await azureServiceTokenProvider.GetAccessTokenAsync("https://management.azure.com/");
                string endpoint = $"https://management.azure.com/subscriptions/{this.settings.SubscriptionId}/resourceGroups/{this.settings.ResourceGroupName}/providers/Microsoft.DocumentDB/databaseAccounts/{this.settings.AccountName}/listKeys?api-version=2019-12-12";

                HttpClient httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var result = await httpClient.PostAsync(endpoint, new StringContent(""));
                this.databaseKeys = await result.Content.ReadAsAsync<DatabaseAccountListKeysResult>();
            }
            catch (Exception exception)
            {
                log.LogError($"{nameof(RetrieveCosmosDbKeysAsync)} threw {exception.GetType()}: {exception.Message}", exception);
            }
        }
    }
}
