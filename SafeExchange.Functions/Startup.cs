/// <summary>
/// ...
/// </summary>

using Microsoft.Azure.Functions.Extensions.DependencyInjection;

[assembly: FunctionsStartup(typeof(SpaceOyster.SafeExchange.Functions.Startup))]
namespace SpaceOyster.SafeExchange.Functions
{
    using SpaceOyster.SafeExchange.Core;

    public class Startup : FunctionsStartup
    {
        public override void Configure(IFunctionsHostBuilder builder)
        {
            SafeExchangeStartup.ConfigureFunctionServices(builder);
        }
    }
}
