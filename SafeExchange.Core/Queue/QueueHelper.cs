/// <summary>
/// QueueHelper
/// </summary>

namespace SafeExchange.Core
{
	using System;
    using Azure.Core;
    using Azure.Identity;
    using Azure.Security.KeyVault.Keys.Cryptography;
    using Azure.Storage;
    using Azure.Storage.Queues;
    using Azure.Storage.Queues.Specialized;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Crypto;

    public class QueueHelper : IQueueHelper
	{
        private readonly QueueConfiguration queueConfiguration;

        private readonly ILogger<QueueHelper> log;

        private readonly ICryptoHelper cryptoHelper;

        private readonly TokenCredential credential;

        private QueueClient queueClient;

        public QueueHelper(IConfiguration configuration, ICryptoHelper cryptoHelper, ILogger<QueueHelper> log)
        {
            if (configuration is null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            this.queueConfiguration = new QueueConfiguration();
            configuration.GetSection("Queue").Bind(this.queueConfiguration);

            this.cryptoHelper = cryptoHelper ?? throw new ArgumentNullException(nameof(cryptoHelper));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.credential = new ChainedTokenCredential(new ManagedIdentityCredential(), new EnvironmentCredential());
        }

        public async ValueTask EnqueueMessageAsync<T>(T messageObject, TimeSpan visibilityTimeout) where T: class
        {
            await this.InitializeAsync();

            var serializedMessage = DefaultJsonSerializer.Serialize(messageObject);
            await this.queueClient.SendMessageAsync(serializedMessage, visibilityTimeout);
        }

        public async ValueTask<(bool succeeded, T? result)> TryPopMessageAsync<T>() where T: class
        {
            await this.InitializeAsync();

            var messageResponse = await this.queueClient.ReceiveMessageAsync(visibilityTimeout: TimeSpan.Zero);
            if (!messageResponse.HasValue)
            {
                return (false, default);
            }
            
            var message = messageResponse.Value;
            var messageObject = DefaultJsonSerializer.Deserialize<T>(message.MessageText);
            await this.queueClient.DeleteMessageAsync(message.MessageId, message.PopReceipt);

            return (true, messageObject);
        }

        private async ValueTask InitializeAsync()
        {
            if (this.queueClient is not null)
            {
                return;
            }

            this.log.LogInformation($"Initializing queue client ('{this.queueConfiguration.QueueServiceUri}', queue name '{this.queueConfiguration.QueueName}').");

            var queueClient = new QueueClient(new Uri(this.queueConfiguration.QueueServiceUri, this.queueConfiguration.QueueName), this.credential)

            if (this.queueConfiguration.UseClientSideEncryption)
            {
                var encryptionAlgorithm = "RSA-OAEP-256";
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

                var key = await this.cryptoHelper.GetOrCreateCryptoKeyAsync(this.queueConfiguration.ClientSideEncryptionKeyName);

                var cryptoClient = new CryptographyClient(key.Id, this.credential, cryptoClientOptions);
                var cryptoKeyResolver = new KeyResolver(this.credential, cryptoClientOptions);
                var encryptionOptions = new ClientSideEncryptionOptions(ClientSideEncryptionVersion.V2_0)
                {
                    KeyEncryptionKey = cryptoClient,
                    KeyResolver = cryptoKeyResolver,
                    KeyWrapAlgorithm = encryptionAlgorithm
                };

                queueClient = queueClient.WithClientSideEncryptionOptions(encryptionOptions);
            }

            var queueExists = await queueClient.ExistsAsync();
            if (!queueExists.Value)
            {
                this.log.LogInformation($"Creating queue.");
                await queueClient.CreateIfNotExistsAsync();
            }

            this.queueClient = queueClient;
            this.log.LogInformation($"Queue client initialized.");
        }
	}
}