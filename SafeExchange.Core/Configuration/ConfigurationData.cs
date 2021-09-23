
namespace SpaceOyster.SafeExchange.Core
{
    using SpaceOyster.SafeExchange.Core.CosmosDb;

    public class ConfigurationData
    {
        public Features Features { get; set; }

        public string WhitelistedGroups { get; set; }

        public CosmosDbProviderSettings CosmosDb { get; set; }

        public string AdminGroups { get; set; }

        public string AdminUsers { get; set; }
    }
}
