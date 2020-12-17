/// SafeExchange

using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace SpaceOyster.SafeExchange.Core
{
    public static class SafeExchangeStartup
    {
        public static void ConfigureFunctionServices(IFunctionsHostBuilder builder)
        {
            builder.Services.AddSingleton<IGraphClientProvider, GraphClientProvider>();
        }
    }
}