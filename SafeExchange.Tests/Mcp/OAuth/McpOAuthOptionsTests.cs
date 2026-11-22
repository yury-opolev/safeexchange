/// <summary>
/// McpOAuthOptionsTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Options;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Mcp.OAuth;
    using System.Collections.Generic;

    [TestFixture]
    public class McpOAuthOptionsTests
    {
        [Test]
        public void ConfigureServices_BindsTheMcpOAuthSection()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "CosmosDb:CosmosDbEndpoint", "https://localhost:8081/" },
                    { "CosmosDb:DatabaseName", "startup-test" },
                    { "Mcp:OAuth:ClientId", "00000000-0000-0000-0000-0000000000c9" },
                })
                .Build();
            var services = new ServiceCollection();

            SafeExchangeStartup.ConfigureServices(configuration, services);

            using var provider = services.BuildServiceProvider();
            Assert.That(provider.GetRequiredService<IOptionsMonitor<McpOAuthOptions>>().CurrentValue.ClientId, Is.EqualTo("00000000-0000-0000-0000-0000000000c9"));
        }

        [Test]
        public void ConfigureServices_BindsTheStateKeyAndTheRateLimits()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "CosmosDb:CosmosDbEndpoint", "https://localhost:8081/" },
                    { "CosmosDb:DatabaseName", "startup-test" },
                    { "Mcp:OAuth:StateKey", McpOAuthTestKey.Value },
                    { "Mcp:OAuth:RateLimit:TokenPermitsPerMinute", "5" },
                    { "Mcp:OAuth:RateLimit:MaxConcurrentEntraCalls", "4" },
                })
                .Build();
            var services = new ServiceCollection();

            SafeExchangeStartup.ConfigureServices(configuration, services);

            using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<IOptionsMonitor<McpOAuthOptions>>().CurrentValue;
            Assert.That(options.StateKey, Is.EqualTo(McpOAuthTestKey.Value));
            Assert.That(options.RateLimit.TokenPermitsPerMinute, Is.EqualTo(5));
            Assert.That(options.RateLimit.MaxConcurrentEntraCalls, Is.EqualTo(4));
            Assert.That(options.RateLimit.TokenBurst, Is.EqualTo(30));
        }
    }
}
