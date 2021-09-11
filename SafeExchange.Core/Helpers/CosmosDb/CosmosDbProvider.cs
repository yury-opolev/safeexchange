/// <summary>
/// SafeExchange
/// </summary>

namespace SpaceOyster.SafeExchange.Core.CosmosDb
{
    using Microsoft.Azure.Cosmos;
    using Microsoft.Azure.Services.AppAuthentication;
    using Microsoft.Extensions.Logging;
    using System;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text.Json;
    using System.Threading.Tasks;

    public class CosmosDbProvider : ICosmosDbProvider, IDisposable
    {
        private bool initialized;

        private bool disposed;

        private CosmosClient cosmosClient;

        private readonly ILogger log;

        private readonly CosmosDbProviderSettings settings;

        private DatabaseAccountListKeysResult databaseKeys;

        public CosmosDbProvider(ConfigurationSettings configuration, ILogger<CosmosDbProvider> log)
        {
            if (configuration is null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            this.log = log ?? throw new ArgumentNullException(nameof(log));

            this.settings = configuration.CosmosDb;
            this.log.LogInformation($"{nameof(CosmosDbProvider)} instantiated with configuration settings: {JsonSerializer.Serialize(this.settings)}");
        }

        ~CosmosDbProvider()
        {
            this.Dispose(false);
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
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

        public async ValueTask<Container> GetNotificationSubscriptionsContainerAsync()
        {
            await this.InitializeAsync();
            return await this.GetContainerInternalAsync(CosmosDbProviderSettings.NotificationSubscriptionsContainerName);
        }

        public async ValueTask<Container> GetAccessRequestsContainerAsync()
        {
            await this.InitializeAsync();
            return await this.GetContainerInternalAsync(CosmosDbProviderSettings.AccessRequestsContainerName);
        }

        private async ValueTask<Container> GetContainerInternalAsync(string containerName)
        {
            await RetrieveCosmosDbKeysAsync();
            this.CreateCosmosClient();

            try
            {
                return this.cosmosClient.GetContainer(this.settings.DatabaseName, containerName);
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

        private void CreateCosmosClient()
        {
            if (this.cosmosClient != null)
            {
                return;
            }

            try
            {
                this.cosmosClient = new CosmosClient(this.settings.CosmosDbEndpoint, this.databaseKeys.primaryMasterKey);
            }
            catch (CosmosException cosmosException)
            {
                log.LogError($"{nameof(CreateCosmosClient)} threw {nameof(CosmosException)}: [{cosmosException.StatusCode}.{cosmosException.SubStatusCode}], {cosmosException.Message}", cosmosException);
            }
            catch (Exception exception)
            {
                log.LogError($"{nameof(CreateCosmosClient)} threw {exception.GetType()}: {exception.Message}", exception);
            }
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
                await databaseResponse.Database.CreateContainerIfNotExistsAsync(CosmosDbProviderSettings.NotificationSubscriptionsContainerName, "/PartitionKey");
                await databaseResponse.Database.CreateContainerIfNotExistsAsync(CosmosDbProviderSettings.AccessRequestsContainerName, "/PartitionKey");
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

        protected virtual void Dispose(bool disposing)
        {
            if (this.disposed)
            {
                return;
            }

            if (disposing)
            {
                // dispose managed resources
            }

            this.cosmosClient?.Dispose();

            this.disposed = true;
        }
    }
}
