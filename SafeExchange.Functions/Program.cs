/// <summary>
/// Startup
/// </summary>

namespace SafeExchange.Functions
{
    using System.Threading.Tasks;
    using Microsoft.Extensions.Hosting;
    using SafeExchange.Core;
    using SafeExchange.Core.Middleware;

    class Program
    {
        static async Task Main(string[] args)
        {
            var host = new HostBuilder()
                .ConfigureFunctionsWorkerDefaults((context, builder) =>
                {
                    builder.UseMiddleware<DefaultAuthenticationMiddleware>();
                })
                .ConfigureAppConfiguration(SafeExchangeStartup.ConfigureAppConfiguration)
                .ConfigureServices((hostBuilderContext, serviceCollection) =>
                {
                    SafeExchangeStartup.ConfigureServices(hostBuilderContext.Configuration, serviceCollection);
                })
                .Build();

            await host.RunAsync();
        }
    }
}