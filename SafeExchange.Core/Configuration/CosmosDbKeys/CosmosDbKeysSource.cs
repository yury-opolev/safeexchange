/// <summary>
/// CosmosDbKeysSource
/// </summary>

namespace SafeExchange.Core.Configuration
{
    using Azure.Core;
    using Microsoft.Extensions.Configuration;
    using System;

    public class CosmosDbKeysSource : IConfigurationSource
    {
        private readonly CosmosDbConfiguration cosmosDbConfiguration;

        private readonly TokenCredential tokenCredential;

        public CosmosDbKeysSource(CosmosDbConfiguration cosmosDbConfiguration, TokenCredential tokenCredential)
        {
            this.cosmosDbConfiguration = cosmosDbConfiguration ?? throw new ArgumentNullException(nameof(cosmosDbConfiguration));
            this.tokenCredential = tokenCredential ?? throw new ArgumentNullException(nameof(tokenCredential));
        }

        public IConfigurationProvider Build(IConfigurationBuilder builder) => new CosmosDbKeysProvider(this.cosmosDbConfiguration, this.tokenCredential);
    }
}
