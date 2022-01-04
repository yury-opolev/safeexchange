/// <summary>
/// CosmosDbKeysSource
/// </summary>

namespace SafeExchange.Core.Configuration
{
    using Microsoft.Extensions.Configuration;
    using System;

    public class CosmosDbKeysSource : IConfigurationSource
    {
        private readonly IConfiguration configuration;

        public CosmosDbKeysSource(IConfiguration configuration)
        {
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public IConfigurationProvider Build(IConfigurationBuilder builder) => new CosmosDbKeysProvider(this.configuration);
    }
}
