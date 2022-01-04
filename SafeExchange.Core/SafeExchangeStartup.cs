/// <summary>
/// Startup
/// </summary>

using Microsoft.Azure.Functions.Extensions.DependencyInjection;

[assembly: FunctionsStartup(typeof(SafeExchange.Core.SafeExchangeStartup))]
namespace SafeExchange.Core
{
    using Azure.Extensions.AspNetCore.Configuration.Secrets;
    using Azure.Identity;
    using Microsoft.Azure.Functions.Extensions.DependencyInjection;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using SafeExchange.Core.AzureAd;
    using SafeExchange.Core.Blob;
    using SafeExchange.Core.Graph;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Filters;
    using System;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Purger;

    public class SafeExchangeStartup : FunctionsStartup
    {
        public override void ConfigureAppConfiguration(IFunctionsConfigurationBuilder builder)
        {
            var interimConfiguration = builder.ConfigurationBuilder.Build();
            var keyVaultUri = new Uri(interimConfiguration["KEYVAULT_BASEURI"]);

            builder.ConfigurationBuilder.AddAzureKeyVault(
                keyVaultUri, new DefaultAzureCredential(), new AzureKeyVaultConfigurationOptions()
                {
                    Manager = new KeyVaultSecretManager(),
                    ReloadInterval = TimeSpan.FromMinutes(5)
                });

            builder.ConfigurationBuilder.AddCosmosDbKeysConfiguration();
        }

        public override void Configure(IFunctionsHostBuilder builder)
        {
            var configuration = builder.GetContext().Configuration;

            var cosmosDbConfig = new CosmosDbConfiguration();
            configuration.GetSection("CosmosDb").Bind(cosmosDbConfig);

            var cosmosDbKeys = new CosmosDbKeys();
            configuration.GetSection(CosmosDbKeysProvider.CosmosDbKeysSectionName).Bind(cosmosDbKeys);

            builder.Services.AddDbContext<SafeExchangeDbContext>(
                options => options.UseCosmos(
                    cosmosDbConfig.CosmosDbEndpoint,
                    cosmosDbKeys.PrimaryKey,
                    cosmosDbConfig.DatabaseName));

            builder.Services.AddSingleton<ITokenHelper, TokenHelper>();
            builder.Services.AddSingleton<IBlobHelper, BlobHelper>();
            builder.Services.AddSingleton<IConfidentialClientProvider, ConfidentialClientProvider>();
            builder.Services.AddSingleton<IGraphDataProvider, GraphDataProvider>();
            builder.Services.AddSingleton<IPurger, PurgeManager>();
            builder.Services.AddSingleton<GlobalFilters>();

            builder.Services.AddScoped<IPermissionsManager, PermissionsManager>();
        }
    }
}
