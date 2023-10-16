/// <summary>
/// QueueConfiguration
/// </summary>

namespace SafeExchange.Core.Configuration
{
    using System;

    public class QueueConfiguration
    {
        public Uri QueueServiceUri { get; set; }

        public string QueueName { get; set; }

        public bool UseClientSideEncryption { get; set; }

        public string? ClientSideEncryptionKeyName { get; set; }
    }
}