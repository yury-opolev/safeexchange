/// <summary>
/// 
/// </summary>

namespace SafeExchange.Core.Crypto
{
    using Azure.Core;
    using Azure.Identity;
    using Azure.Security.KeyVault.Keys;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Configuration;
    using System;
    using System.Threading.Tasks;

    public class CryptoHelper : ICryptoHelper
    {
        private readonly IConfiguration configuration;

        private readonly ILogger<CryptoHelper> log;

        private TokenCredential credential;

        private KeyClient keyClient;

        public CryptoConfiguration CryptoConfiguration { get; }

        public Uri KeyVaultUri { get; private set; }

        public CryptoHelper(IConfiguration configuration, ILogger<CryptoHelper> log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            this.credential = new ChainedTokenCredential(new ManagedIdentityCredential(), new EnvironmentCredential());

            this.KeyVaultUri = new Uri(this.configuration["KEYVAULT_BASEURI"]);
            var keyClientOptions = new KeyClientOptions()
            {
                Retry =
                {
                    Delay= TimeSpan.FromSeconds(1),
                    MaxDelay = TimeSpan.FromSeconds(8),
                    MaxRetries = 5,
                    Mode = RetryMode.Exponential
                }
            };

            this.KeyVaultUri = new Uri(this.configuration["KEYVAULT_BASEURI"]);
            this.keyClient = new KeyClient(this.KeyVaultUri, this.credential, keyClientOptions);

            this.CryptoConfiguration = new CryptoConfiguration();
            this.configuration.GetSection("Crypto").Bind(this.CryptoConfiguration);
        }

        public async Task<KeyVaultKey> GetOrCreateCryptoKeyAsync(string cryptoKeyName)
        {
            var cryptoKeyVersions = await this.GetCryptoKeyVersionsAsync(cryptoKeyName);
            if (cryptoKeyVersions.Count == 0)
            {
                await this.CreateCryptoKeyAsync(cryptoKeyName);
                cryptoKeyVersions = await this.GetCryptoKeyVersionsAsync(cryptoKeyName);
            }

            this.log.LogInformation($"Retrieved key '{cryptoKeyName}' versions count {cryptoKeyVersions.Count}");

            return await this.keyClient.GetKeyAsync(cryptoKeyName);
        }

        public async Task<KeyVaultKey> CreateNewCryptoKeyVersionAsync(string cryptoKeyName)
        {
            return await this.CreateCryptoKeyAsync(cryptoKeyName);
        }

        private async Task<IList<KeyProperties>> GetCryptoKeyVersionsAsync(string cryptoKeyName)
        {
            var result = new List<KeyProperties>();
            await foreach (var keyProperties in this.keyClient.GetPropertiesOfKeyVersionsAsync(cryptoKeyName))
            {
                result.Add(keyProperties);
            }

            return result;
        }

        private async Task<KeyVaultKey> CreateCryptoKeyAsync(string cryptoKeyName)
        {
            var options = new CreateRsaKeyOptions(cryptoKeyName)
            {
                KeySize = 4096
            };

            var key = await this.keyClient.CreateRsaKeyAsync(options);
            this.log.LogInformation($"Created RSA key '{cryptoKeyName}', size {options.KeySize}, (version: {key.Value.Properties.Version}");

            return key;
        }
    }
}
