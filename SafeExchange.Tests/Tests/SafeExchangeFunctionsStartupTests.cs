/// <summary>
/// SafeExchangeFunctionsStartupTests
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.ApplicationInsights.Extensibility;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Middleware;
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
                "Scoped McpOAuthHandler <- McpOAuthHandler",
                "Singleton ITelemetryInitializer <- SessionCorrelationTelemetryInitializer",
                "Singleton ITelemetryInitializer <- ApiVersionTelemetryInitializer",
            }));
        }

        [Test]
        public void ConfigureWorkerDefaults_RegistersTheMiddlewareInOrder()
        {
            var builder = new RecordingWorkerBuilder();

            SafeExchangeFunctionsStartup.ConfigureWorkerDefaults(null!, builder);

            Assert.That(builder.Middleware, Is.EqualTo(new[]
            {
                typeof(DefaultAuthenticationMiddleware),
                typeof(TokenFilterMiddleware),
                typeof(SessionCorrelationMiddleware),
                typeof(ApiVersionTelemetryMiddleware),
            }));
            Assert.That(builder.UseCalls, Is.EqualTo(4));
        }

        [Test]
        public void UseSafeExchangeAuthentication_RegistersOnlyTheAuthenticationMiddleware()
        {
            var builder = new RecordingWorkerBuilder();

            SafeExchangeFunctionsStartup.UseSafeExchangeAuthentication(builder);

            Assert.That(builder.Middleware, Is.EqualTo(new[] { typeof(DefaultAuthenticationMiddleware) }));
            Assert.That(builder.UseCalls, Is.EqualTo(1));
        }

        [Test]
        public void UseSafeExchangeMiddleware_RegistersEverythingExceptAuthentication()
        {
            var builder = new RecordingWorkerBuilder();

            SafeExchangeFunctionsStartup.UseSafeExchangeMiddleware(builder);

            Assert.That(builder.Middleware, Is.EqualTo(new[]
            {
                typeof(TokenFilterMiddleware),
                typeof(SessionCorrelationMiddleware),
                typeof(ApiVersionTelemetryMiddleware),
            }));
            Assert.That(builder.UseCalls, Is.EqualTo(3));
        }

        private sealed class RecordingWorkerBuilder : IFunctionsWorkerApplicationBuilder
        {
            public IServiceCollection Services { get; } = new ServiceCollection();

            public int UseCalls { get; private set; }

            public IEnumerable<System.Type> Middleware => this.Services.Select(d => d.ServiceType);

            public IFunctionsWorkerApplicationBuilder Use(System.Func<FunctionExecutionDelegate, FunctionExecutionDelegate> middleware)
            {
                this.UseCalls++;
                return this;
            }
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
