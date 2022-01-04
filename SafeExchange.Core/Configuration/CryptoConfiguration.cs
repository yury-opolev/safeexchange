/// <summary>
/// CryptoConfiguration
/// </summary>

namespace SafeExchange.Core.Configuration
{
    using System;

    public class CryptoConfiguration
    {
        public string BlobServiceUri { get; set; }

        public string KeyName { get; set; }

        public string ContainerName { get; set; }
    }
}
