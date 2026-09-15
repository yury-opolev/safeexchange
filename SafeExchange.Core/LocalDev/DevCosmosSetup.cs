/// <summary>
/// DevCosmosSetup — LOCAL SPIKE ONLY.
///
/// Owns the SAEX_DEV_MODE Cosmos wiring so emulator-specific concerns stay out of
/// ordinary application startup.
///
/// The emulator connection authenticates its server like any other TLS connection:
/// nothing here installs a certificate-validation callback. An HttpClientHandler
/// configured here used to carry DangerousAcceptAnyServerCertificateValidator, which
/// disabled certificate-chain and hostname validation for whatever endpoint the
/// configuration named — an emulator with a self-signed certificate was the
/// intention, but the branch trusted any server it reached. Trusting the emulator's
/// certificate locally replaces that bypass; see LocalDev/README.md.
///
/// Regression tests: SafeExchange.Tests/Tests/DevModeCosmosTlsTests.cs.
/// </summary>

namespace SafeExchange.Core.LocalDev
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.EntityFrameworkCore.Infrastructure;
    using Microsoft.Extensions.DependencyInjection;
    using SafeExchange.Core.Configuration;
    using System;
    using System.Configuration;

    public static class DevCosmosSetup
    {
        /// <summary>
        /// Registers the <see cref="SafeExchangeDbContext"/> factory against the local
        /// Cosmos emulator, using the emulator account key from configuration.
        /// </summary>
        public static void AddDevCosmosDbContextFactory(IServiceCollection services, CosmosDbConfiguration cosmosDbConfig)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(cosmosDbConfig);

            ValidateEndpoint(cosmosDbConfig.CosmosDbEndpoint);
            ValidatePrimaryKey(cosmosDbConfig.PrimaryKey);

            services.AddDbContextFactory<SafeExchangeDbContext>(
                options => options.UseCosmos(
                    cosmosDbConfig.CosmosDbEndpoint,
                    cosmosDbConfig.PrimaryKey,
                    cosmosDbConfig.DatabaseName,
                    ConfigureCosmosOptions));
        }

        /// <summary>
        /// Cosmos client options for the emulator. Transport settings only — server
        /// authentication is left to the platform's certificate validation.
        /// </summary>
        internal static void ConfigureCosmosOptions(CosmosDbContextOptionsBuilder cosmos)
        {
            // The Linux vNext-preview emulator is Gateway-only, and LimitToEndpoint stops
            // the SDK from chasing regional endpoints that do not exist locally.
            cosmos.ConnectionMode(Microsoft.Azure.Cosmos.ConnectionMode.Gateway);
            cosmos.LimitToEndpoint();

            // No HttpClientFactory on purpose. A handler configured here is where the
            // certificate-validation bypass used to live; leaving the SDK's default
            // client in place means a TLS failure stays a TLS failure, with no
            // insecure handler to fall back to.
        }

        /// <summary>
        /// Development mode exists to talk to a local emulator, so the endpoint has to be
        /// one. That keeps an accidental SAEX_DEV_MODE=true from pointing this
        /// key-authenticated connection at a real account — containment, not a substitute
        /// for authenticating the server, which certificate validation does.
        /// </summary>
        internal static void ValidateEndpoint(string endpoint)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
            {
                throw new ConfigurationErrorsException(
                    $"CosmosDb:CosmosDbEndpoint is not a valid absolute URI: '{endpoint}'.");
            }

            if (!endpointUri.IsLoopback)
            {
                throw new ConfigurationErrorsException(
                    $"CosmosDb:CosmosDbEndpoint must be a loopback address when SAEX_DEV_MODE=true, but was '{endpoint}'. " +
                    "Development mode targets the local Cosmos emulator - unset SAEX_DEV_MODE to connect to a real account.");
            }
        }

        /// <summary>
        /// The emulator key comes from user-secrets, never from source. Fail fast with the
        /// exact command to set it so the dev experience stays self-explanatory.
        /// </summary>
        internal static void ValidatePrimaryKey(string primaryKey)
        {
            if (string.IsNullOrWhiteSpace(primaryKey))
            {
                throw new ConfigurationErrorsException(
                    "CosmosDb:PrimaryKey is required when SAEX_DEV_MODE=true. " +
                    "Set it via user-secrets, e.g.: " +
                    "dotnet user-secrets set \"CosmosDb:PrimaryKey\" \"<emulator-key>\" --project SafeExchange.Functions");
            }
        }
    }
}
