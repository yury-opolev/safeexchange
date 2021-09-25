/// SafeExchange

namespace SpaceOyster.SafeExchange.Core
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.Azure.KeyVault;
    using Microsoft.Azure.KeyVault.Models;
    using Microsoft.Azure.Services.AppAuthentication;
    using Microsoft.Extensions.Logging;
    using Microsoft.Rest.Azure;
    using Microsoft.Rest.TransientFaultHandling;

    public class KeyVaultHelper
    {
        private readonly string vaultBaseUri;
        private readonly AzureServiceTokenProvider azureServiceTokenProvider;
        private readonly KeyVaultClient keyVault;
        private readonly ILogger log;

        private RetryPolicy regularRetryPolicy;

        private RetryPolicy purgeRetryPolicy;

        public KeyVaultHelper(string vaultBaseUri, ILogger log)
        {
            this.vaultBaseUri = vaultBaseUri ?? throw new ArgumentNullException(nameof(vaultBaseUri));
            this.log = log ?? throw new ArgumentNullException(nameof(log));

            this.regularRetryPolicy = new RetryPolicy<HttpStatusCodeErrorDetectionStrategy>(
                new ExponentialBackoffRetryStrategy(
                    retryCount: 3,
                    minBackoff: TimeSpan.FromSeconds(1.0),
                    maxBackoff: TimeSpan.FromSeconds(4.0),
                    deltaBackoff: TimeSpan.FromSeconds(1.0)));

            this.purgeRetryPolicy = new RetryPolicy<PurgingTransientErrorDetectionStrategy>(
                new ExponentialBackoffRetryStrategy(
                    retryCount: 5,
                    minBackoff: TimeSpan.FromSeconds(1.0),
                    maxBackoff: TimeSpan.FromSeconds(8.0),
                    deltaBackoff: TimeSpan.FromSeconds(1.0)));

            this.azureServiceTokenProvider = new AzureServiceTokenProvider();
            this.keyVault = new KeyVaultClient(new KeyVaultClient.AuthenticationCallback(this.azureServiceTokenProvider.KeyVaultTokenCallback));
            this.keyVault.SetRetryPolicy(regularRetryPolicy);
        }

        public async Task<IPage<SecretItem>> GetSecretVersionsAsync(string secretName)
        {
            return await this.keyVault.GetSecretVersionsAsync(this.vaultBaseUri, secretName);
        }

        public async Task<SecretBundle> SetSecretAsync(string secretName, string value, string contentType = null, IDictionary<string, string> tags = null)
        {
            return await this.keyVault.SetSecretAsync(this.vaultBaseUri, secretName, value, tags: tags, contentType: contentType);
        }

        public async Task<SecretBundle> GetSecretAsync(string secretName)
        {
            return await this.keyVault.GetSecretAsync(this.vaultBaseUri, secretName);
        }

        public async Task<SecretBundle> TryGetDeletedSecretAsync(string secretName)
        {
            try
            {
                return await this.keyVault.GetDeletedSecretAsync(this.vaultBaseUri, secretName);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public async Task<IList<string>> TryGetDeletedSecretsAsync(int maxResults)
        {
            var result = new List<string>();
            try
            {
                var page = await this.keyVault.GetDeletedSecretsAsync(this.vaultBaseUri);
                while (true)
                {
                    foreach (var item in page)
                    {
                        result.Add(item.Identifier.Name);
                    }
                    if (string.IsNullOrEmpty(page.NextPageLink) || result.Count >= maxResults)
                    {
                        break;
                    }
                    page = await this.keyVault.GetDeletedSecretsNextAsync(page.NextPageLink);
                }
            }
            catch (Exception ex)
            {
                this.log.LogWarning($"Could not get list of deleted secrets from keyvault: {this.GetDescription(ex)}");
            }
            return result;
        }

        public async Task<DeletedSecretBundle> DeleteSecretAsync(string secretName)
        {
            var deletedSecret = await this.keyVault.DeleteSecretAsync(this.vaultBaseUri, secretName);
            return deletedSecret;
        }

        public async Task TryPurgeSecretAsync(string secretName)
        {
            try
            {
                this.keyVault.SetRetryPolicy(this.purgeRetryPolicy);
                await this.keyVault.PurgeDeletedSecretAsync(this.vaultBaseUri, secretName);
                this.log.LogInformation($"Secret '{secretName}' was purged from keyvault");
            }
            catch (Exception ex)
            {
                this.log.LogWarning($"Could not purge secret '{secretName}' from keyvault: {this.GetDescription(ex)}");
            }
            finally
            {
                this.keyVault.SetRetryPolicy(this.regularRetryPolicy);
            }
        }

        private string GetDescription(Exception ex)
        {
            var stringBuilder = new StringBuilder($"{ex.GetType()}: {ex.Message}");

            var currentException = ex;
            while (currentException.InnerException != null)
            {
                currentException = currentException.InnerException;
                stringBuilder.Append($" -> {ex.GetType()}: {ex.Message}");
            }
            return stringBuilder.ToString();
        }
    }
}
