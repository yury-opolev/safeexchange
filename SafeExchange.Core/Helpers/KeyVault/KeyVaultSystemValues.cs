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

        private ILogger log;

        public KeyVaultSystemSettings(ILogger log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public bool IsSystemSettingName(string name)
        {
            var regex = new Regex(SystemNameRegex, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            return regex.IsMatch(name);
        }

        public async Task<TokenProviderSettings> GetTokenProviderSettingsAsync()
        {
            var keyVaultHelper = new KeyVaultHelper(Environment.GetEnvironmentVariable("STORAGE_KEYVAULT_BASEURI"), this.log);

            var existingSecretVersions = await keyVaultHelper.GetSecretVersionsAsync(TokenProviderSettingsName);
            if (!existingSecretVersions.Any())
            {
                log.LogInformation($"Cannot get TokenProviderSettings, as not exists.");
                return default(TokenProviderSettings);
            }

            var secretBundle = await keyVaultHelper.GetSecretAsync(TokenProviderSettingsName);
            return JsonSerializer.Deserialize<TokenProviderSettings>(secretBundle.Value);
        }
    }
}
