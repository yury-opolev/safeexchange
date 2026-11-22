/// <summary>
/// McpBaseUriGuardTests
/// </summary>

namespace SafeExchange.Tests.Mcp
{
    using Microsoft.Extensions.Logging;
    using NUnit.Framework;
    using SafeExchange.Core.Mcp;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Linq;

    [TestFixture]
    public class McpBaseUriGuardTests
    {
        [TestCase("https://safeexchange.test/api/")]
        [TestCase("https://safeexchange.test:8443/api/")]
        [TestCase("http://localhost:7071/api/")]
        [TestCase("http://127.0.0.1:7071/api/")]
        [TestCase("http://[::1]:7071/api/")]
        public void HttpsAndLoopbackHttp_AreAllowed(string apiBaseUri)
        {
            var logs = new RecordingLoggerProvider();

            Assert.That(Create(logs).IsAllowed(new Uri(apiBaseUri)), Is.True);
            Assert.That(logs.Entries, Is.Empty);
        }

        [Test]
        public void PlainHttpToAnyOtherHost_IsRefused_AndLoggedOnceAtWarning()
        {
            var logs = new RecordingLoggerProvider();
            var guard = Create(logs);

            Assert.That(guard.IsAllowed(new Uri("http://safeexchange.test/api/")), Is.False);
            Assert.That(guard.IsAllowed(new Uri("http://10.0.0.4/api/")), Is.False);

            Assert.That(logs.Entries.Single(), Does.StartWith(
                "Warning SafeExchange.Core.Mcp.McpBaseUriGuard: MCP OAuth discovery and broker answer 404 for the non-https base http://safeexchange.test/api/"));
        }

        private static McpBaseUriGuard Create(RecordingLoggerProvider logs)
            => new(new LoggerFactory(new[] { logs }).CreateLogger<McpBaseUriGuard>());
    }
}
