/// <summary>
/// ServiceRegistrationTests
/// </summary>

namespace SafeExchange.Tests.Services
{
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Mcp;
    using SafeExchange.Tests.Contract;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Uses the contract fixture plumbing (production serializer settings) to call handlers resolved from DI.
    /// </summary>
    [TestFixture]
    public class ServiceRegistrationTests : ContractTestBase
    {
        [Test]
        public void ConfigureServices_RegistersTheServicesAsScoped()
        {
            var services = new ServiceCollection();

            SafeExchangeStartup.ConfigureServices(StartupConfiguration(), services);

            var registrations = services
                .Where(d => d.ServiceType.Namespace == "SafeExchange.Core.Services")
                .Select(d => $"{d.Lifetime} {d.ServiceType.Name} <- {d.ImplementationType?.Name}")
                .ToList();
            Assert.That(registrations, Is.EqualTo(new[]
            {
                "Scoped SecretService <- SecretService",
                "Scoped ContentService <- ContentService",
                "Scoped ChunkService <- ChunkService",
                "Scoped AccessService <- AccessService",
                "Scoped AuditQueryService <- AuditQueryService",
            }));
        }

        [Test]
        public async Task HandlersFromDi_UseTheRegisteredCallerResolver()
        {
            var configuration = StartupConfiguration();
            var services = new ServiceCollection();
            SafeExchangeFunctionsStartup.ConfigureFunctionsServices(services);
            SafeExchangeStartup.ConfigureServices(configuration, services);
            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging();
            services.AddSingleton<IBlobHelper>(new TestBlobHelper());
            services.AddScoped<ICallerResolver, DenyingCallerResolver>();

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            using var scope = provider.CreateScope();
            var handlers = scope.ServiceProvider;
            Assert.That(handlers.GetRequiredService<SafeExchangeSecretContentMeta>(), Is.Not.Null);
            Assert.That(handlers.GetRequiredService<SafeExchangeSecretStream>(), Is.Not.Null);
            Assert.That(handlers.GetRequiredService<SafeExchangeContentCommit>(), Is.Not.Null);
            Assert.That(handlers.GetRequiredService<SafeExchangeAccess>(), Is.Not.Null);
            Assert.That(handlers.GetRequiredService<SafeExchangeSecretAudit>(), Is.Not.Null);
            var toolNames = handlers.GetServices<IMcpTool>().Select(t => t.Name).ToList();
            Assert.That(toolNames, Has.Count.EqualTo(16));
            Assert.That(toolNames, Is.Unique);

            var response = await handlers.GetRequiredService<SafeExchangeSecretMeta>().RunListV3(Request("get"), this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Unauthorized, """
                {"status":"unauthorized","subStatus":"","error":"Denied by the registered resolver."}
                """);
        }

        private static IConfiguration StartupConfiguration()
            => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "CosmosDb:CosmosDbEndpoint", "https://localhost:8081/" },
                    { "CosmosDb:DatabaseName", "startup-test" },
                })
                .Build();

        private sealed class DenyingCallerResolver : ICallerResolver
        {
            public Task<CallerResolution> ResolveAsync(ClaimsPrincipal principal, string channel, string? clientLabel, CancellationToken cancellationToken = default)
                => Task.FromResult(CallerResolution.Denied(new CallerDenial(HttpStatusCode.Unauthorized, "unauthorized", "Denied by the registered resolver.", string.Empty)));
        }
    }
}
