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
        private bool isInitialized;

        private ConfigurationData data;

        private ILogger logger;

        private KeyVaultSystemSettings systemSettings;

        public ConfigurationSettings(ILogger<ConfigurationSettings> logger)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.systemSettings = new KeyVaultSystemSettings(logger);
        }

        public async ValueTask<ConfigurationData> GetDataAsync()
        {
            await this.InitializeAsync();
            return this.data;
        }

        public async Task RestoreSettingsAsync()
        {
            var configurationData = await systemSettings.GetConfigurationSettingsAsync();
            if (configurationData is default(ConfigurationData))
            {
                this.RestoreFromEnvironment();
                await this.PersistSettingsAsync();
                return;
            }

            this.logger.Log(LogLevel.Information, "Restoring configuration from keyvault.");
            this.data = configurationData;
        }

        public async Task PersistSettingsAsync()
        {
            this.logger.Log(LogLevel.Information, "Persisting configuration to keyvault.");
            await systemSettings.SetConfigurationSettingsAsync(this.data);
        }

        private void RestoreFromEnvironment()
        {
            this.logger.Log(LogLevel.Information, "Restoring configuration from environment.");
            var useNotifications = Environment.GetEnvironmentVariable("FEATURES-USE-NOTIFICATIONS") ?? "False";
            var useGroupsAuthorization = Environment.GetEnvironmentVariable("FEATURES-USE-GROUP-AUTHORIZATION") ?? "False";

            this.data = new ConfigurationData()
            {
                Features = new Features()
                {
                    UseNotifications = bool.Parse(useNotifications),
                    UseGroupsAuthorization = bool.Parse(useGroupsAuthorization)
                },
                WhitelistedGroups = Environment.GetEnvironmentVariable("GLOBAL_GROUPS_WHITELIST") ?? string.Empty,
                CosmosDb =
                    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COSMOS_DB_SETTINGS")) ?
                        throw new ArgumentException("Cosmos DB configuration is not set, check environment value for 'COSMOS_DB_SETTINGS'.") :
                        JsonSerializer.Deserialize<CosmosDbProviderSettings>(Environment.GetEnvironmentVariable("COSMOS_DB_SETTINGS")),
                AdminGroups = string.Empty
            };
        }

        private async ValueTask InitializeAsync()
        {
            if (this.isInitialized)
            {
                return;
            }

            await this.RestoreSettingsAsync();
            this.isInitialized = true;
        }
    }
}
