/// <summary>
/// ConfigurationBuilderExtensions
/// </summary>

namespace SafeExchange.Core.Configuration
{
    using Azure.Core;
    using Microsoft.Extensions.Configuration;
    using System;

    public static class ConfigurationBuilderExtensions
    {
        public static IConfigurationBuilder AddCosmosDbKeysConfiguration(this IConfigurationBuilder builder, TokenCredential tokenCredential, CosmosDbConfiguration cosmosDbConfiguration)
        {
            return builder.Add(new CosmosDbKeysSource(cosmosDbConfiguration, tokenCredential));
        }
    }
}
