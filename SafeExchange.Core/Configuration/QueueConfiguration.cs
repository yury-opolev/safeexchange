/// <summary>
/// QueueConfiguration
/// </summary>

namespace SafeExchange.Core.Configuration
{
    using System;

    public class QueueConfiguration
    {
        public Uri QueueServiceUri { get; set; }

        public string QueueName => "delayed-webhooks";

        public bool UseClientSideEncryption { get; set; } = false;

        public string? ClientSideEncryptionKeyName { get; set; } = null;
    }
}