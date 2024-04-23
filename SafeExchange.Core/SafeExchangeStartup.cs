/// <summary>
/// Startup
/// </summary>

namespace SafeExchange.Core
{
    using Azure.Extensions.AspNetCore.Configuration.Secrets;
    using Azure.Identity;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using SafeExchange.Core.AzureAd;
    using SafeExchange.Core.Blob;
    using SafeExchange.Core.Graph;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Crypto;
    using SafeExchange.Core.Middleware;
    using SafeExchange.Core.Migrations;
    using SafeExchange.Core.DelayedTasks;
    using System;
    using System.Text.Json.Serialization;
    using System.Text.Json;
    using Microsoft.Extensions.Options;
    using System.Configuration;

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
        }

        public static void ConfigureServices(IConfiguration configuration, IServiceCollection services)
        {
            services.AddHttpClient();

            services.AddScoped<ITokenMiddlewareCore, TokenMiddlewareCore>();
            services.AddSingleton<ITokenValidationParametersProvider, TokenValidationParametersProvider>();

            var defaultAzureCredential = new DefaultAzureCredential();
            var cosmosDbConfig = configuration.GetSection("CosmosDb").Get<CosmosDbConfiguration>() ?? throw new ConfigurationErrorsException("Cannot get CosmosDb configuration.");
            services.AddDbContext<SafeExchangeDbContext>(
                options => options.UseCosmos(
                    cosmosDbConfig.CosmosDbEndpoint,
                    defaultAzureCredential,
                    cosmosDbConfig.DatabaseName));

            services.AddSingleton<ITokenHelper, TokenHelper>();
            services.AddSingleton<ICryptoHelper, CryptoHelper>();
            services.AddSingleton<IBlobHelper, BlobHelper>();
            services.AddSingleton<IConfidentialClientProvider, ConfidentialClientProvider>();
            services.AddSingleton<IGraphDataProvider, GraphDataProvider>();
            services.AddSingleton<IPurger, PurgeManager>();
            services.AddSingleton<GlobalFilters>();

            services.AddScoped<IPermissionsManager, PermissionsManager>();

            services.AddScoped<IQueueHelper, QueueHelper>();
            services.AddScoped<IDelayedTaskScheduler, DelayedTaskScheduler>();

            services.AddScoped<IMigrationsHelper>((serviceProvider) =>
            {
                var log = serviceProvider.GetRequiredService<ILogger<MigrationsHelper>>();
                var cosmosDbConfig = serviceProvider.GetRequiredService<IOptions<CosmosDbConfiguration>>();
                return new MigrationsHelper(cosmosDbConfig.Value, log);
            });

            services.Configure<JsonSerializerOptions>(options =>
            {
                options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            });

            services.Configure<Features>(configuration.GetSection("Features"));
            services.Configure<GloballyAllowedGroupsConfiguration>(configuration.GetSection("GlobalAllowLists"));
            services.Configure<AdminConfiguration>(configuration.GetSection("AdminConfiguration"));
            services.Configure<CosmosDbConfiguration>(configuration.GetSection("CosmosDb"));
        }
    }
}
