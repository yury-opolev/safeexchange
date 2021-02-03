/// <summary>
/// SafeExchange
/// </summary>

namespace SpaceOyster.SafeExchange.Core.CosmosDb
{
    using System;

    public class CosmosDbProviderSettings
    {
        public const string GroupDictionaryContainerName = "GroupDictionary";

        public const string ObjectMetadataContainerName = "ObjectMetadata";

        public const string SubjectPermissionsContainerName = "SubjectPermissions";

        public const string DefaultPartitionKeyName = "PartitionKey";

        public string SubscriptionId { get; set; }

        public string ResourceGroupName { get; set; }

        public string AccountName { get; set; }

        public string CosmosDbEndpoint { get; set; }

        public string DatabaseName { get; set; }
    }
}
