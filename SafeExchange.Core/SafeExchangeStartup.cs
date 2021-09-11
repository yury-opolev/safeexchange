/// SafeExchange

using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using SpaceOyster.SafeExchange.Core.CosmosDb;

namespace SpaceOyster.SafeExchange.Core
{
    public static class SafeExchangeStartup
    {
        public static void ConfigureFunctionServices(IFunctionsHostBuilder builder)
        {
            builder.Services.AddSingleton<IGraphClientProvider, GraphClientProvider>();
            builder.Services.AddSingleton<ICosmosDbProvider, CosmosDbProvider>();
            builder.Services.AddSingleton<ConfigurationSettings>();
            builder.Services.AddSingleton<GlobalFilters>();
        }
    }
}