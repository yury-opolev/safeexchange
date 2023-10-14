/// <summary>
/// Startup
/// </summary>

namespace SafeExchange.Core
{
    using Azure.Extensions.AspNetCore.Configuration.Secrets;
    using Azure.Identity;
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
    using SafeExchange.Core.Crypto;
    using System.Text.Json.Serialization;
    using System.Text.Json;
    using SafeExchange.Core.Middleware;
    using SafeExchange.Core.Migrations;
    using Microsoft.Extensions.Logging;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Extensions.Hosting;
    using SafeExchange.Core.DelayedTasks;
    using SafeExchange.Core.WebhookNotifications;

    public class SafeExchangeStartup
    {
        public static bool IsHttpTrigger(FunctionContext context)
            => context.FunctionDefinition.InputBindings.Values
                .First(a => a.Type.EndsWith("Trigger")).Type == "httpTrigger";

        public static void ConfigureWorkerDefaults(HostBuilderContext context, IFunctionsWorkerApplicationBuilder builder)
        {
            builder.UseWhen<DefaultAuthenticationMiddleware>(IsHttpTrigger);
            builder.UseWhen<TokenFilterMiddleware>(IsHttpTrigger);
        }

        public static void ConfigureAppConfiguration(IConfigurationBuilder configurationBuilder)
        {
            var interimConfiguration = configurationBuilder.Build();
            var keyVaultUri = new Uri(interimConfiguration["KEYVAULT_BASEURI"]);

            configurationBuilder.AddAzureKeyVault(
                keyVaultUri, new DefaultAzureCredential(), new AzureKeyVaultConfigurationOptions()
                {
                    Manager = new KeyVaultSecretManager(),
                    ReloadInterval = TimeSpan.FromMinutes(5)
                });

            configurationBuilder.AddCosmosDbKeysConfiguration();
        }

        public static void ConfigureServices(IConfiguration configuration, IServiceCollection services)
        {
            services.AddScoped<ITokenMiddlewareCore, TokenMiddlewareCore>();
            services.AddSingleton<ITokenValidationParametersProvider, TokenValidationParametersProvider>();

            var cosmosDbConfig = new CosmosDbConfiguration();
            configuration.GetSection("CosmosDb").Bind(cosmosDbConfig);

            var cosmosDbKeys = new CosmosDbKeys();
            configuration.GetSection(CosmosDbKeysProvider.CosmosDbKeysSectionName).Bind(cosmosDbKeys);

            services.AddDbContext<SafeExchangeDbContext>(
                options => options.UseCosmos(
                    cosmosDbConfig.CosmosDbEndpoint,
                    cosmosDbKeys.PrimaryKey,
                    cosmosDbConfig.DatabaseName));

            services.AddSingleton<ITokenHelper, TokenHelper>();
            services.AddSingleton<ICryptoHelper, CryptoHelper>();
            services.AddSingleton<IBlobHelper, BlobHelper>();
            services.AddSingleton<IConfidentialClientProvider, ConfidentialClientProvider>();
            services.AddSingleton<IGraphDataProvider, GraphDataProvider>();
            services.AddSingleton<IPurger, PurgeManager>();
            services.AddSingleton<GlobalFilters>();

            services.AddScoped<IPermissionsManager, PermissionsManager>();

            services.AddScoped<IDelayedTaskScheduler, NullDelayedTaskScheduler>();
            services.AddScoped<IWebhookNotificator, NullWebhookNotificator>();

            services.AddScoped<IMigrationsHelper>((serviceProvider) =>
            {
                var log = serviceProvider.GetRequiredService<ILogger<MigrationsHelper>>();
                return new MigrationsHelper(cosmosDbConfig, cosmosDbKeys, log);
            });

            services.Configure<JsonSerializerOptions>(options =>
            {
                options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            });
        }
    }
}
