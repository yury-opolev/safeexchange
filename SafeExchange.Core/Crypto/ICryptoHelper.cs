/// <summary>
/// 
/// </summary>

namespace SafeExchange.Core.Crypto
{
    using Azure.Security.KeyVault.Keys;
    using SafeExchange.Core.Configuration;

    public interface ICryptoHelper
    {
        public CryptoConfiguration CryptoConfiguration { get; }

        public Task<KeyVaultKey> GetOrCreateCryptoKeyAsync(string cryptoKeyName);

        public Task<KeyVaultKey> CreateNewCryptoKeyVersionAsync(string cryptoKeyName);
    }
}
