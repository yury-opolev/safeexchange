/// <summary>
/// DevCryptoHelper — LOCAL SPIKE ONLY.
///
/// No-op ICryptoHelper used when SAEX_DEV_MODE=true so the app can start with no
/// Key Vault dependency. Blob encryption is bypassed by DevBlobHelper, so the key
/// methods are never exercised by the image-attachment demo; they throw if called
/// (e.g. admin key rotation), which is out of scope for the local spike.
///
/// Part of the LocalDev harness — see LocalDev/README.md.
/// </summary>

namespace SafeExchange.Core.LocalDev
{
    using Azure.Security.KeyVault.Keys;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Crypto;
    using System;
    using System.Threading.Tasks;

    public class DevCryptoHelper : ICryptoHelper
    {
        public CryptoConfiguration CryptoConfiguration { get; } = new CryptoConfiguration();

        public Task<KeyVaultKey> GetOrCreateCryptoKeyAsync(string cryptoKeyName)
            => throw new NotSupportedException("DevCryptoHelper does not support Key Vault operations (local spike).");

        public Task<KeyVaultKey> CreateNewCryptoKeyVersionAsync(string cryptoKeyName)
            => throw new NotSupportedException("DevCryptoHelper does not support Key Vault operations (local spike).");
    }
}
