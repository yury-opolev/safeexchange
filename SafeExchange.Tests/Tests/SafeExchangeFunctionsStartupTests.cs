/// <summary>
/// SafeExchangeFunctionsStartupTests
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.ApplicationInsights.Extensibility;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Middleware;
    using SafeExchange.Core.Telemetry;
    using System.Collections.Generic;
    using System.Linq;

    [TestFixture]
    public class SafeExchangeFunctionsStartupTests
    {
        [Test]
        public void ConfigureFunctionsServices_RegistersTheFunctionsTypedServices()
        {
            var services = new ServiceCollection();

            SafeExchangeFunctionsStartup.ConfigureFunctionsServices(services);

            var registrations = services
                .Select(d => $"{d.Lifetime} {d.ServiceType.Name} <- {d.ImplementationType?.Name ?? "factory"}")
                .ToList();
            Assert.That(registrations, Is.EqualTo(new[]
            {
                "Scoped ITokenMiddlewareCore <- TokenMiddlewareCore",
                "Singleton GlobalFilters <- GlobalFilters",
                "Scoped SafeExchangeSecretMeta <- factory",
                "Scoped SafeExchangeSecretContentMeta <- factory",
                "Scoped SafeExchangeSecretStream <- factory",
                "Scoped SafeExchangeContentCommit <- factory",
                "Scoped SafeExchangeAccess <- factory",
                "Scoped SafeExchangeSecretAudit <- factory",
                "Scoped McpHandler <- McpHandler",
                "Scoped OAuthProtectedResourceHandler <- OAuthProtectedResourceHandler",
                "Singleton ITelemetryInitializer <- SessionCorrelationTelemetryInitializer",
                "Singleton ITelemetryInitializer <- ApiVersionTelemetryInitializer",
            }));
        }

        [Test]
        public void FunctionsThenCoreServices_KeepTheTelemetryInitializerOrder()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "CosmosDb:CosmosDbEndpoint", "https://localhost:8081/" },
                    { "CosmosDb:DatabaseName", "startup-test" },
                })
                .Build();
            var services = new ServiceCollection();

            SafeExchangeFunctionsStartup.ConfigureFunctionsServices(services);
            SafeExchangeStartup.ConfigureServices(configuration, services);

            var initializers = services
                .Where(d => d.ServiceType == typeof(ITelemetryInitializer))
                .Select(d => d.ImplementationType)
                .ToList();
            Assert.That(initializers, Is.EqualTo(new[]
            {
                typeof(SessionCorrelationTelemetryInitializer),
                typeof(ApiVersionTelemetryInitializer),
                typeof(TelemetryIdTelemetryInitializer),
                typeof(PiiRedactionTelemetryInitializer),
            }));
        }
    }
}
