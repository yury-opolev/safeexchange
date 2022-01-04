/// <summary>
/// Startup
/// </summary>

using Microsoft.Azure.Functions.Extensions.DependencyInjection;

[assembly: FunctionsStartup(typeof(SafeExchange.Functions.Startup))]
namespace SafeExchange.Functions
{
    using SafeExchange.Core;

    public class Startup : FunctionsStartup
    {
        private readonly SafeExchangeStartup startup = new SafeExchangeStartup();

        public override void ConfigureAppConfiguration(IFunctionsConfigurationBuilder builder)
            => this.startup.ConfigureAppConfiguration(builder);

        public override void Configure(IFunctionsHostBuilder builder)
            => this.startup.Configure(builder);
    }
}
