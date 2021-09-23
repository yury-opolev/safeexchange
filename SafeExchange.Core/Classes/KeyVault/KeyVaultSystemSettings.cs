/// <summary>
/// SafeExchange
/// </summary>

namespace SpaceOyster.SafeExchange.Core
{
    using Microsoft.Extensions.Logging;
    using System;
    using System.Linq;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;

    public class KeyVaultSystemSettings
    {
        private const string SystemNameRegex = @"^system\-setting\-\d+$";

        private const string TokenProviderSettingsName = "system-setting-000001";

        private const string VapidOptionsSettingsName = "system-setting-000002";

        private const string ConfigurationSettingsName = "system-setting-000003";

        private ILogger log;

        private static KeyVaultHelper KeyVaultHelper;

        public KeyVaultSystemSettings(ILogger log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public static bool IsSystemSettingName(string name)
        {
            var regex = new Regex(SystemNameRegex, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            return regex.IsMatch(name);
        }

        public static KeyVaultHelper GetKeyVaultHelper(ILogger logger)
        {
            if (KeyVaultSystemSettings.KeyVaultHelper is null)
            {
                KeyVaultSystemSettings.KeyVaultHelper = new KeyVaultHelper(Environment.GetEnvironmentVariable("STORAGE_KEYVAULT_BASEURI"), logger);
            }

            return KeyVaultSystemSettings.KeyVaultHelper;
        }

        public async Task<TokenProviderSettings> GetTokenProviderSettingsAsync()
        {
            var keyVaultHelper = GetKeyVaultHelper(this.log);
            var existingSecretVersions = await keyVaultHelper.GetSecretVersionsAsync(TokenProviderSettingsName);
            if (!existingSecretVersions.Any())
            {
                log.LogInformation($"Cannot get {nameof(TokenProviderSettings)}, as not exists.");
                return default(TokenProviderSettings);
            }

            var secretBundle = await keyVaultHelper.GetSecretAsync(TokenProviderSettingsName);
            return JsonSerializer.Deserialize<TokenProviderSettings>(secretBundle.Value);
        }

        public async Task<VapidOptions> GetVapidOptionsAsync()
        {
            var keyVaultHelper = GetKeyVaultHelper(this.log);
            var existingSecretVersions = await keyVaultHelper.GetSecretVersionsAsync(VapidOptionsSettingsName);
            if (!existingSecretVersions.Any())
            {
                log.LogInformation($"Cannot get {nameof(VapidOptions)}, as not exists.");
                return default(VapidOptions);
            }

            var secretBundle = await keyVaultHelper.GetSecretAsync(VapidOptionsSettingsName);
            return JsonSerializer.Deserialize<VapidOptions>(secretBundle.Value);
        }

        public async Task SetVapidOptionsAsync(VapidOptions vapidOptions)
        {
            var keyVaultHelper = GetKeyVaultHelper(this.log);
            await keyVaultHelper.SetSecretAsync(VapidOptionsSettingsName, JsonSerializer.Serialize(vapidOptions));
        }

        public async Task<ConfigurationData> GetConfigurationSettingsAsync()
        {
            var keyVaultHelper = GetKeyVaultHelper(this.log);
            var existingSecretVersions = await keyVaultHelper.GetSecretVersionsAsync(ConfigurationSettingsName);
            if (!existingSecretVersions.Any())
            {
                log.LogInformation($"Cannot get {nameof(ConfigurationSettings)}, as not exists.");
                return default(ConfigurationData);
            }

            var secretBundle = await keyVaultHelper.GetSecretAsync(ConfigurationSettingsName);
            return JsonSerializer.Deserialize<ConfigurationData>(secretBundle.Value);
        }

        public async Task SetConfigurationSettingsAsync(ConfigurationData data)
        {
            var keyVaultHelper = GetKeyVaultHelper(this.log);
            await keyVaultHelper.SetSecretAsync(ConfigurationSettingsName, JsonSerializer.Serialize(data));
        }
    }
}
