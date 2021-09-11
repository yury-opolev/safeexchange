/// <summary>
/// SafeExchange
/// </summary>

namespace SpaceOyster.SafeExchange.Core
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using SpaceOyster.SafeExchange.Core.CosmosDb;

    public class ConfigurationSettings
    {
        public Features Features { get; set; }

        public string WhitelistedGroups { get; set; }

        public CosmosDbProviderSettings CosmosDb { get; set; }

        public string AdminGroups { get; set; }

        private ILogger logger;

        private KeyVaultSystemSettings systemSettings;

        public ConfigurationSettings(ILogger logger)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.systemSettings = new KeyVaultSystemSettings(logger);
            this.RestoreSettingsAsync().GetAwaiter().GetResult();
        }

        public async Task RestoreSettingsAsync()
        {
            var settings = await systemSettings.GetConfigurationSettingsAsync();
            if (settings is default(ConfigurationSettings))
            {
                this.RestoreFromEnvironment();
                await this.PersistSettingsAsync();
                return;
            }

            this.logger.Log(LogLevel.Information, "Restoring settings from keyvault.");

            this.Features = settings.Features;
            this.WhitelistedGroups = settings.WhitelistedGroups;
            this.CosmosDb = settings.CosmosDb;
        }

        public async Task PersistSettingsAsync()
        {
            this.logger.Log(LogLevel.Information, "Persisting settings to keyvault.");

            await systemSettings.SetConfigurationSettingsAsync(this);
        }

        private void RestoreFromEnvironment()
        {
            this.logger.Log(LogLevel.Information, "Restoring settings from environment.");

            var useNotifications = Environment.GetEnvironmentVariable("FEATURES-USE-NOTIFICATIONS") ?? "False";
            var useGroupsAuthorization = Environment.GetEnvironmentVariable("FEATURES-USE-GROUP-AUTHORIZATION") ?? "False";

            this.Features = new Features()
            {
                UseNotifications = bool.Parse(useNotifications),
                UseGroupsAuthorization = bool.Parse(useGroupsAuthorization)
            };

            this.WhitelistedGroups = Environment.GetEnvironmentVariable("GLOBAL_GROUPS_WHITELIST") ?? string.Empty;
            var cosmosDbSettingsJson = Environment.GetEnvironmentVariable("COSMOS_DB_SETTINGS");
            this.CosmosDb = string.IsNullOrEmpty(cosmosDbSettingsJson) ?
                throw new ArgumentException("Cosmos DB configuration is not set, check environment value for 'COSMOS_DB_SETTINGS'.") :
                JsonSerializer.Deserialize<CosmosDbProviderSettings>(cosmosDbSettingsJson);

            this.AdminGroups = string.Empty;
        }
    }
}
