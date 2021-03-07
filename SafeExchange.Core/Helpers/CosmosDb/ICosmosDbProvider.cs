/// <summary>
/// SafeExchange
/// </summary>

namespace SpaceOyster.SafeExchange.Core.CosmosDb
{
    using Microsoft.Azure.Cosmos;
    using System.Threading.Tasks;

    public interface ICosmosDbProvider
    {
        public ValueTask<Container> GetObjectMetadataContainerAsync();

        public ValueTask<Container> GetSubjectPermissionsContainerAsync();

        public ValueTask<Container> GetGroupDictionaryContainerAsync();

        public ValueTask<Container> GetNotificationSubscriptionsContainerAsync();
    }
}
