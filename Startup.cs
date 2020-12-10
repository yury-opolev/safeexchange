/// SafeExchange

using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

[assembly: FunctionsStartup(typeof(SpaceOyster.SafeExchange.Startup))]
namespace SpaceOyster.SafeExchange
{
    public class Startup : FunctionsStartup
    {
        public override void Configure(IFunctionsHostBuilder builder)
        {
            builder.Services.AddSingleton<IGraphClientProvider, GraphClientProvider>();
        }
    }
}