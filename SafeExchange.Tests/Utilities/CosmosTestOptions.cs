/// <summary>
/// CosmosTestOptions
/// </summary>

namespace SafeExchange.Tests.Utilities
{
    using Microsoft.Azure.Cosmos;
    using Microsoft.EntityFrameworkCore.Infrastructure;
    using System;

    /// <summary>
    /// Shared Cosmos test-client configuration for integration tests that talk
    /// to the Linux vNext-preview Cosmos DB Emulator. That emulator only supports
    /// <see cref="ConnectionMode.Gateway"/> — the SDK's default <see cref="ConnectionMode.Direct"/>
    /// fails inside <c>ConsistencyReader.GetMaxReplicaSetSize</c> when it tries to
    /// negotiate the replica set. All test fixtures must route through this helper
    /// so the choice is in exactly one place.
    /// </summary>
    public static class CosmosTestOptions
    {
        public static CosmosClient CreateClient(string connectionString)
        {
            return new CosmosClient(
                connectionString,
                new CosmosClientOptions
                {
                    ConnectionMode = ConnectionMode.Gateway,
                    LimitToEndpoint = true,
                });
        }

        public static Action<CosmosDbContextOptionsBuilder> UseGateway =>
            options =>
            {
                options.ConnectionMode(ConnectionMode.Gateway);
                options.LimitToEndpoint(enable: true);
            };
    }
}
