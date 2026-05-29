/// <summary>
/// Startup
/// </summary>

namespace SafeExchange.Functions
{
    using System.Threading.Tasks;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core;

    class Program
    {
        static async Task Main(string[] args)
        {
            var host = new HostBuilder()
                .ConfigureFunctionsWorkerDefaults(SafeExchangeStartup.ConfigureWorkerDefaults)
                .ConfigureLogging(SafeExchangeStartup.ConfigureWorkerLogging)
                .ConfigureAppConfiguration(SafeExchangeStartup.ConfigureAppConfiguration)
                .ConfigureAppConfiguration(cb =>
                {
                    if (SafeExchangeStartup.IsDevMode())
                    {
                        cb.AddUserSecrets<Program>(optional: true);
                    }
                })
                .ConfigureServices((hostBuilderContext, serviceCollection) =>
                {
                    SafeExchangeStartup.ConfigureServices(hostBuilderContext.Configuration, serviceCollection);
                })
                .Build();

            await host.RunAsync();
        }
    }
}