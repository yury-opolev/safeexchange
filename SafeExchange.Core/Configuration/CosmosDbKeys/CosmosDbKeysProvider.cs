/// <summary>
/// CosmosDbKeysProvider
/// </summary>

namespace SafeExchange.Core.Configuration
{
    using Azure.Core;
    using Microsoft.Extensions.Configuration;
    using System;
    using System.Collections.Generic;
    using System.Configuration;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Threading.Tasks;

    public class CosmosDbKeysProvider : ConfigurationProvider, IDisposable
    {
        public static readonly string CosmosDbKeysSectionName = "CosmosDbKeys";

        private DatabaseAccountListKeysResult? databaseKeys;

        private readonly CosmosDbConfiguration cosmosDbConfiguration;

        private readonly TokenCredential tokenCredential;

        private readonly CancellationTokenSource cancellationToken = new CancellationTokenSource();

        private bool disposed;

        public CosmosDbKeysProvider(CosmosDbConfiguration cosmosDbConfiguration, TokenCredential tokenCredential)
        {
            this.cosmosDbConfiguration = cosmosDbConfiguration ?? throw new ArgumentNullException(nameof(cosmosDbConfiguration));
            this.tokenCredential = tokenCredential ?? throw new ArgumentNullException(nameof(tokenCredential));
        }

        /// <inheritdoc />
        public override void Load()
        {
            this.Data = this.CreateConfigurationValues();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!this.disposed)
                {
                    this.cancellationToken.Cancel();
                    this.cancellationToken.Dispose();
                }

                this.disposed = true;
            }
        }

        private IDictionary<string, string?> CreateConfigurationValues()
        {
            this.RetrieveCosmosDbKeysAsync().Wait(this.cancellationToken.Token);

            var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [$"{CosmosDbKeysSectionName}:PrimaryKey"] = this.databaseKeys?.primaryMasterKey,
                [$"{CosmosDbKeysSectionName}:PrimaryReadonlyKey"] = this.databaseKeys?.primaryReadonlyMasterKey,
                [$"{CosmosDbKeysSectionName}:SecondaryKey"] = this.databaseKeys?.secondaryMasterKey,
                [$"{CosmosDbKeysSectionName}:SecondaryReadonlyKey"] = this.databaseKeys?.secondaryReadonlyMasterKey
            };

            return settings;
        }

        private async Task RetrieveCosmosDbKeysAsync()
        {
            if (this.databaseKeys != null)
            {
                return;
            }

            string? responseContent;
            HttpResponseMessage responseMessage;
            try
            {
                AccessToken accessToken = await this.tokenCredential.GetTokenAsync(new TokenRequestContext(new[] { "https://management.azure.com/.default" }), this.cancellationToken.Token);
                string endpoint = $"https://management.azure.com/subscriptions/{cosmosDbConfiguration.SubscriptionId}/resourceGroups/{cosmosDbConfiguration.ResourceGroupName}/providers/Microsoft.DocumentDB/databaseAccounts/{cosmosDbConfiguration.AccountName}/listKeys?api-version=2019-12-12";

                HttpClient httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
                responseMessage = await httpClient.PostAsync(endpoint, new StringContent(""), this.cancellationToken.Token);
                responseContent = await responseMessage.Content.ReadAsStringAsync(this.cancellationToken.Token);
            }
            catch (Exception exception)
            {
                throw new ConfigurationErrorsException("Could not retrieve database keys.", exception);
            }

            if (responseMessage.StatusCode != System.Net.HttpStatusCode.OK)
            {
                throw new ConfigurationErrorsException("Could not retrieve database keys, reponse is not successful.");
            }

            if (string.IsNullOrEmpty(responseContent))
            {
                throw new ConfigurationErrorsException("Could not retrieve database keys, reponse is empty.");
            }

            this.databaseKeys = DefaultJsonSerializer.Deserialize<DatabaseAccountListKeysResult>(responseContent);
        }
    }
}