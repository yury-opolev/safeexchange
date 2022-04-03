/// <summary>
/// BlobHelper
/// </summary>

namespace SafeExchange.Core.Blob
{
    using Azure.Core;
    using Azure.Identity;
    using Azure.Security.KeyVault.Keys.Cryptography;
    using Azure.Storage;
    using Azure.Storage.Blobs;
    using Azure.Storage.Blobs.Models;
    using Azure.Storage.Blobs.Specialized;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Crypto;
    using System;
    using System.IO;
    using System.Threading.Tasks;

    public class BlobHelper : IBlobHelper
    {
        private readonly ILogger<BlobHelper> log;

        private readonly ICryptoHelper cryptoHelper;

        private string containerName;
        private string cryptoKeyName;

        private TokenCredential credential;

        private BlobContainerClient blobContainerClient;

        public Uri BlobServiceUri { get; private set; }

        public Uri KeyVaultUri { get; private set; }

        public BlobHelper(ICryptoHelper cryptoHelper, ILogger<BlobHelper> log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.cryptoHelper = cryptoHelper ?? throw new ArgumentNullException(nameof(cryptoHelper));
            this.credential = new ChainedTokenCredential(new ManagedIdentityCredential(), new EnvironmentCredential());
        }

        public async Task<bool> BlobExistsAsync(string blobName)
        {
            await this.InitializeAsync();

            return await this.blobContainerClient.GetBlobBaseClient(blobName).ExistsAsync();
        }

        public async Task EncryptAndUploadBlobAsync(string blobName, Stream dataStream)
        {
            await this.InitializeAsync();

            var blob = this.blobContainerClient.GetBlobClient(blobName);
            await blob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots);
            await blob.UploadAsync(dataStream);
        }

        public async Task<Stream> DownloadAndDecryptBlobAsync(string blobName)
        {
            await this.InitializeAsync();

            var blob = this.blobContainerClient.GetBlobClient(blobName);
            var streamingResult = await blob.DownloadStreamingAsync();
            return streamingResult.Value.Content;
        }

        public async Task<bool> DeleteBlobIfExistsAsync(string blobName)
        {
            await this.InitializeAsync();

            return await this.blobContainerClient.DeleteBlobIfExistsAsync(blobName, DeleteSnapshotsOption.IncludeSnapshots);
        }

        private async ValueTask InitializeAsync()
        {
            if (this.blobContainerClient is not null)
            {
                return;
            }

            this.InitializeCryptoConfig();

            var encryptionAlgorithm = "RSA-OAEP-256";
            this.log.LogInformation($"Initializing blob container client ('{this.BlobServiceUri}', '{this.containerName}') with encryption (key '{this.cryptoKeyName}', {encryptionAlgorithm}).");

            var cryptoClientOptions = new CryptographyClientOptions()
            {
                Retry =
                {
                    Delay= TimeSpan.FromSeconds(1),
                    MaxDelay = TimeSpan.FromSeconds(8),
                    MaxRetries = 5,
                    Mode = RetryMode.Exponential
                }
            };

            var key = await this.cryptoHelper.GetOrCreateCryptoKeyAsync(this.cryptoKeyName);

            var cryptoClient = new CryptographyClient(key.Id, this.credential, cryptoClientOptions);
            var cryptoKeyResolver = new KeyResolver(this.credential, cryptoClientOptions);
            var encryptionOptions = new ClientSideEncryptionOptions(ClientSideEncryptionVersion.V1_0)
            {
                KeyEncryptionKey = cryptoClient,
                KeyResolver = cryptoKeyResolver,
                KeyWrapAlgorithm = encryptionAlgorithm
            };

            var blobClientOptions = new SpecializedBlobClientOptions() { ClientSideEncryption = encryptionOptions };
            var containerClient = new BlobServiceClient(this.BlobServiceUri, this.credential, blobClientOptions)
                .GetBlobContainerClient(this.containerName);

            var containerExists = await containerClient.ExistsAsync();
            if (!containerExists.Value)
            {
                this.log.LogInformation($"Creating blob container.");
                await containerClient.CreateIfNotExistsAsync();
            }

            this.blobContainerClient = containerClient;

            this.log.LogInformation($"Blob cointainer client initialized.");
        }

        private void InitializeCryptoConfig()
        {
            var cryptoConfig = this.cryptoHelper.CryptoConfiguration;

            this.BlobServiceUri = new Uri(cryptoConfig.BlobServiceUri);
            this.cryptoKeyName = cryptoConfig.KeyName ?? throw new ArgumentNullException(nameof(cryptoConfig.KeyName));
            this.containerName = cryptoConfig.ContainerName ?? throw new ArgumentNullException(nameof(cryptoConfig.ContainerName));
        }
    }
}
