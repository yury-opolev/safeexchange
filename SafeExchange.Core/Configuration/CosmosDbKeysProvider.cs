/// <summary>
/// CosmosDbKeysProvider
/// </summary>

namespace SafeExchange.Core.Configuration
{
    using Microsoft.Azure.Services.AppAuthentication;
    using Microsoft.Extensions.Configuration;
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Threading.Tasks;

    public class CosmosDbKeysProvider : ConfigurationProvider
    {
        public static readonly string CosmosDbKeysSectionName = "CosmosDbKeys";

        private readonly IConfiguration configuration;

        private DatabaseAccountListKeysResult databaseKeys;

        public CosmosDbKeysProvider(IConfiguration configuration)
        {
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            this.Data = this.CreateConfigurationValues();
        }

        private async Task RetrieveCosmosDbKeysAsync()
        {
            if (this.databaseKeys != null)
            {
                return;
            }

            var cosmosDbConfig = new CosmosDbConfiguration();
            this.configuration.GetSection("CosmosDb").Bind(cosmosDbConfig);

            var azureServiceTokenProvider = new AzureServiceTokenProvider();
            string accessToken = await azureServiceTokenProvider.GetAccessTokenAsync("https://management.azure.com/");
            string endpoint = $"https://management.azure.com/subscriptions/{cosmosDbConfig.SubscriptionId}/resourceGroups/{cosmosDbConfig.ResourceGroupName}/providers/Microsoft.DocumentDB/databaseAccounts/{cosmosDbConfig.AccountName}/listKeys?api-version=2019-12-12";

            HttpClient httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var result = await httpClient.PostAsync(endpoint, new StringContent(""));
            this.databaseKeys = await result.Content.ReadAsAsync<DatabaseAccountListKeysResult>();
        }

        private IDictionary<string, string> CreateConfigurationValues()
        {
            this.RetrieveCosmosDbKeysAsync().GetAwaiter().GetResult();

            var settings = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                [$"{CosmosDbKeysSectionName}:PrimaryKey"] = this.databaseKeys.primaryMasterKey,
                [$"{CosmosDbKeysSectionName}:PrimaryReadonlyKey"] = this.databaseKeys.primaryReadonlyMasterKey,
                [$"{CosmosDbKeysSectionName}:SecondaryKey"] = this.databaseKeys.secondaryMasterKey,
                [$"{CosmosDbKeysSectionName}:SecondaryReadonlyKey"] = this.databaseKeys.secondaryReadonlyMasterKey
            };

            return settings;
        }
    }
}