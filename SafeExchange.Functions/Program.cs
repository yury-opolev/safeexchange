/// <summary>
/// Startup
/// </summary>

namespace SafeExchange.Functions
{
    using System.Threading.Tasks;
    using Microsoft.Extensions.Hosting;
    using SafeExchange.Core;

    class Program
    {
        static async Task Main(string[] args)
        {
            var host = new HostBuilder()
                .ConfigureAppConfiguration(SafeExchangeStartup.ConfigureAppConfiguration)
                .ConfigureFunctionsWorkerDefaults()
                .ConfigureServices((hostBuilderContext, serviceCollection) =>
                {
                    SafeExchangeStartup.ConfigureServices(hostBuilderContext.Configuration, serviceCollection);
                })
                .Build();

            await host.RunAsync();
        }
    }
}