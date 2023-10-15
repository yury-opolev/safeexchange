/// <summary>
/// QueueHelper
/// </summary>

namespace SafeExchange.Core
{
	using System;
    using Azure.Core;
    using Azure.Identity;
    using Azure.Storage.Queues;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Configuration;

    public class QueueHelper
	{
        private readonly QueueConfiguration queueConfiguration;

        private readonly ILogger<QueueHelper> log;

        private TokenCredential credential;

        private QueueClient queueClient;

        public QueueHelper(IConfiguration configuration, ILogger<QueueHelper> log)
        {
            if (configuration is null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            this.queueConfiguration = new QueueConfiguration();
            configuration.GetSection("Queue").Bind(this.queueConfiguration);

            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.credential = new ChainedTokenCredential(new ManagedIdentityCredential(), new EnvironmentCredential());
        }

        public async ValueTask EnqueueAsync<T>(T messageObject, TimeSpan visibilityTimeout)
        {
            await this.InitializeAsync();

            var serializedMessage = DefaultJsonSerializer.Serialize(messageObject);
            await this.queueClient.SendMessageAsync(serializedMessage, visibilityTimeout);
        }

        private async ValueTask InitializeAsync()
        {
            if (this.queueClient is not null)
            {
                return;
            }

            this.log.LogInformation($"Initializing queue client ('{this.queueConfiguration.QueueServiceUri}', queue name '{this.queueConfiguration.QueueName}').");

            var queueClientOptions = new QueueClientOptions() { MessageEncoding = QueueMessageEncoding.Base64 };
            var queueClient = new QueueClient(
                new Uri(this.queueConfiguration.QueueServiceUri, this.queueConfiguration.QueueName),
                this.credential, queueClientOptions);

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