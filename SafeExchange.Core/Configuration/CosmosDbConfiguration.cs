/// <summary>
/// CosmosDbConfiguration
/// </summary>

namespace SafeExchange.Core.Configuration
{
    using System;

    public class CosmosDbConfiguration
    {
        public string SubscriptionId { get; set; }

        public string ResourceGroupName { get; set; }

        public string AccountName { get; set; }

        public string CosmosDbEndpoint { get; set; }

        public string DatabaseName { get; set; }

        public string PrimaryKey { get; set; }
    }
}
