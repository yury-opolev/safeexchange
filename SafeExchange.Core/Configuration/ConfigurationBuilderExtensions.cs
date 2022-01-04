/// <summary>
/// ConfigurationBuilderExtensions
/// </summary>

namespace SafeExchange.Core.Configuration
{
    using Microsoft.Extensions.Configuration;
    using System;

    public static class ConfigurationBuilderExtensions
    {
        public static IConfigurationBuilder AddCosmosDbKeysConfiguration(this IConfigurationBuilder builder)
        {
            var interimConfig = builder.Build();
            return builder.Add(new CosmosDbKeysSource(interimConfig));
        }
    }
}
