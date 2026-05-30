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
    using SafeExchange.Core.Applications;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.AzureAd;
    using SafeExchange.Core.LocalDev;
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
    using SafeExchange.Core.Groups;
    using SafeExchange.Core.Telemetry;
    using Microsoft.ApplicationInsights.Extensibility;

    public class SafeExchangeStartup
    {
        public static bool IsHttpTrigger(FunctionContext context)
            => context.FunctionDefinition.InputBindings.Values
                .First(a => a.Type.EndsWith("Trigger")).Type == "httpTrigger";

        // LOCAL SPIKE ONLY: when SAEX_DEV_MODE=true we substitute local-runnable
        // implementations for ICryptoHelper / IBlobHelper and point Cosmos at the
        // emulator with its well-known key. Authentication intentionally stays on
        // the real middleware so local dev exercises the same token-validation
        // path production uses (just against the staging Entra app). Never set
        // SAEX_DEV_MODE in a deployed environment.
        public static bool IsDevMode()
            => string.Equals(Environment.GetEnvironmentVariable("SAEX_DEV_MODE"), "true", StringComparison.OrdinalIgnoreCase);

        public static void ConfigureWorkerDefaults(HostBuilderContext context, IFunctionsWorkerApplicationBuilder builder)
        {
            builder.UseWhen<DefaultAuthenticationMiddleware>(IsHttpTrigger);
            builder.UseWhen<TokenFilterMiddleware>(IsHttpTrigger);

            // After auth so failed-auth requests still get the dimension,
            // but before any handler logs so downstream ILogger scopes
            // inherit the x-saex-session-id correlation.
            builder.UseWhen<SessionCorrelationMiddleware>(IsHttpTrigger);
        }

        // The categories below are emitted inside the isolated worker process —
        // most notably Azure.Identity, which logs four Information traces per
        // managed-identity token acquisition. After the 2026-05-25 move to
        // identity-based App Insights ingestion, the telemetry channel acquires
        // a token for https://monitor.azure.com/ roughly once a second, so those
        // traces ballooned to ~98% of App Insights ingestion (the self-amplifying
        // feedback loop described in commit 0794d88).
        //
        // host.json's logging.logLevel only governs the HOST process, so the
        // 'Azure.Identity: Warning' filter there never reaches these worker-side
        // logs. The worker must filter them itself, here. Warning keeps genuine
        // credential failures visible while dropping the success-path flood.
        public static void ConfigureWorkerLogging(ILoggingBuilder logging)
        {
            logging.AddFilter("Azure.Identity", LogLevel.Warning);
            logging.AddFilter("Azure.Core", LogLevel.Warning);
            logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
            logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        }

        public static void ConfigureAppConfiguration(IConfigurationBuilder configurationBuilder)
        {
            if (IsDevMode())
            {
                return;
            }

            var keyVaultBaseUri = configurationBuilder.Build()["KEYVAULT_BASEURI"];
            if (string.IsNullOrWhiteSpace(keyVaultBaseUri))
            {
                throw new ConfigurationErrorsException("KEYVAULT_BASEURI is required outside of SAEX_DEV_MODE.");
            }

            if (!Uri.TryCreate(keyVaultBaseUri, UriKind.Absolute, out var keyVaultUri))
            {
                throw new ConfigurationErrorsException($"KEYVAULT_BASEURI is not a valid absolute URI: '{keyVaultBaseUri}'.");
            }

            configurationBuilder.AddAzureKeyVault(
                keyVaultUri, DefaultCredentialProvider.CreateDefaultCredential(), new AzureKeyVaultConfigurationOptions()
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

            var defaultCredential = DefaultCredentialProvider.CreateDefaultCredential();
            var cosmosDbConfig = configuration.GetSection("CosmosDb").Get<CosmosDbConfiguration>() ?? throw new ConfigurationErrorsException("Cannot get CosmosDb configuration.");

            // Register a factory so AuditWriter can isolate its DbContext from the
            // request-scoped context shared by handlers. Sharing a context would let
            // an audit-write retry's ChangeTracker.Clear() drop pending user-facing
            // mutations (e.g., permission grants batched before the final SaveChanges).
            if (IsDevMode())
            {
                // LOCAL SPIKE: Cosmos emulator key comes from CosmosDb:PrimaryKey in
                // user-secrets — never hardcoded. Fail fast with the exact command
                // to set it so the dev experience stays self-explanatory.
                if (string.IsNullOrWhiteSpace(cosmosDbConfig.PrimaryKey))
                {
                    throw new ConfigurationErrorsException(
                        "CosmosDb:PrimaryKey is required when SAEX_DEV_MODE=true. " +
                        "Set it via user-secrets, e.g.: " +
                        "dotnet user-secrets set \"CosmosDb:PrimaryKey\" \"<emulator-key>\" --project SafeExchange.Functions");
                }

                services.AddDbContextFactory<SafeExchangeDbContext>(
                    options => options.UseCosmos(
                        cosmosDbConfig.CosmosDbEndpoint,
                        cosmosDbConfig.PrimaryKey,
                        cosmosDbConfig.DatabaseName,
                        cosmos =>
                        {
                            cosmos.ConnectionMode(Microsoft.Azure.Cosmos.ConnectionMode.Gateway);
                            cosmos.LimitToEndpoint();
                            cosmos.HttpClientFactory(() => new HttpClient(new HttpClientHandler
                            {
                                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                            }));
                        }));
            }
            else
            {
                services.AddDbContextFactory<SafeExchangeDbContext>(
                    options => options.UseCosmos(
                        cosmosDbConfig.CosmosDbEndpoint,
                        defaultCredential,
                        cosmosDbConfig.DatabaseName));
            }

            services.AddScoped<SafeExchangeDbContext>(sp =>
                sp.GetRequiredService<IDbContextFactory<SafeExchangeDbContext>>().CreateDbContext());

            services.AddSingleton<ITokenHelper, TokenHelper>();
            if (IsDevMode())
            {
                services.AddSingleton<ICryptoHelper, DevCryptoHelper>();
                services.AddSingleton<IBlobHelper, DevBlobHelper>();
                services.AddHostedService<DevDbInitializerHostedService>();
            }
            else
            {
                services.AddSingleton<ICryptoHelper, CryptoHelper>();
                services.AddSingleton<IBlobHelper, BlobHelper>();
            }

            services.AddSingleton<IConfidentialClientProvider, ConfidentialClientProvider>();
            services.AddSingleton<IGraphDataProvider, GraphDataProvider>();
            services.AddSingleton<IPurger, PurgeManager>();
            services.AddSingleton<GlobalFilters>();

            services.AddScoped<IPermissionsManager, PermissionsManager>();
            services.AddScoped<IOrphanedSecretManager, OrphanedSecretManager>();
            services.AddScoped<IGroupsManager, GroupsManager>();
            services.AddScoped<IApplicationOwnerService, ApplicationOwnerService>();

            services.AddScoped<IAuditWriter, AuditWriter>();
            services.AddScoped<IAuditPurger, AuditPurger>();

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
            services.Configure<Limits>(configuration.GetSection("Limits"));
            services.Configure<OrphanedSecretConfiguration>(configuration.GetSection("OrphanedSecret"));
            services.Configure<PinnedSecretsConfiguration>(configuration.GetSection("PinnedSecrets"));
            services.Configure<GloballyAllowedGroupsConfiguration>(configuration.GetSection("GlobalAllowLists"));
            services.Configure<AdminConfiguration>(configuration.GetSection("AdminConfiguration"));
            services.Configure<CosmosDbConfiguration>(configuration.GetSection("CosmosDb"));
            services.Configure<WebClientTelemetryConfiguration>(configuration.GetSection("WebClientTelemetry"));

            // Stamps customDimensions.saex.sessionId on every telemetry
            // item emitted within a request that carried the header. Works
            // in tandem with SessionCorrelationMiddleware (which sets the
            // AsyncLocal for the duration of the request handler).
            services.AddSingleton<ITelemetryInitializer, SessionCorrelationTelemetryInitializer>();

            // Stamps customDimensions.saex.telemetryId — a pseudonymous, weekly-rotating
            // id — on every telemetry item so traces can be correlated per user without
            // exposing UPNs or Entra object ids.
            services.AddSingleton<ITelemetryInitializer, TelemetryIdTelemetryInitializer>();
            services.AddSingleton<ITelemetryInitializer, PiiRedactionTelemetryInitializer>();
            services.AddSingleton<TelemetryIdRotator>();
        }
    }
}
