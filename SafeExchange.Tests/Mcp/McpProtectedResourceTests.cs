/// <summary>
/// McpProtectedResourceTests
/// </summary>

namespace SafeExchange.Tests.Mcp
{
    using Microsoft.Extensions.Options;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Mcp.OAuth;
    using SafeExchange.Tests.Mcp.OAuth;
    using System;

    [TestFixture]
    public class McpProtectedResourceTests
    {
        private const string Authority = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001";

        private const string BrokerClientId = "00000000-0000-0000-0000-0000000000c9";

        private static readonly Uri ApiBase = new("https://safeexchange.test/api/");

        [Test]
        public void Defaults_AreDerivedFromTheAuthenticationSection()
        {
            var resource = Create(new AuthenticationConfiguration
            {
                Authority = Authority,
                ValidAudiences = "00000000-0000-0000-0000-0000000000e1, api://safeexchange.test",
            });

            Assert.That(resource.Resource, Is.EqualTo("api://safeexchange.test"));
            Assert.That(resource.AuthorizationServers, Is.EqualTo(new[] { Authority + "/v2.0" }));
            Assert.That(resource.Scopes, Is.EqualTo(new[] { "api://safeexchange.test/user_impersonation" }));
            Assert.That(resource.ApplicationScope, Is.EqualTo("api://safeexchange.test/.default"));
        }

        [Test]
        public void McpScopes_AndAV2Authority_AreTakenAsConfigured()
        {
            var resource = Create(new AuthenticationConfiguration
            {
                Authority = Authority + "/v2.0/",
                ValidAudiences = "api://safeexchange.test/",
                McpScopes = "api://custom/a, ,api://custom/b",
            });

            Assert.That(resource.AuthorizationServers, Is.EqualTo(new[] { Authority + "/v2.0" }));
            Assert.That(resource.Scopes, Is.EqualTo(new[] { "api://custom/a", "api://custom/b" }));
            Assert.That(resource.ApplicationScope, Is.EqualTo("api://safeexchange.test/.default"));
        }

        [Test]
        public void WithoutAnAppIdUri_TheFirstAudienceIsTheResource_AndEmptyConfigurationGivesEmptyLists()
        {
            var guidOnly = Create(new AuthenticationConfiguration { ValidAudiences = "00000000-0000-0000-0000-0000000000e1" });
            var empty = Create(new AuthenticationConfiguration());

            Assert.That(guidOnly.Resource, Is.EqualTo("00000000-0000-0000-0000-0000000000e1"));
            Assert.That(empty.Resource, Is.Empty);
            Assert.That(empty.Scopes, Is.Empty);
            Assert.That(empty.AuthorizationServers, Is.Empty);
            Assert.That(empty.ApplicationScope, Is.Empty);
        }

        [Test]
        public void ToMetadata_IsTheRfc9728Document_WithTheMcpUrlAsResource()
        {
            var resource = Create(new AuthenticationConfiguration { Authority = Authority, ValidAudiences = "api://safeexchange.test" });

            var metadata = resource.ToMetadata(ApiBase);

            Assert.That(resource.IsBrokerConfigured, Is.False);
            Assert.That(metadata.Resource, Is.EqualTo("https://safeexchange.test/api/mcp"));
            Assert.That(metadata.AuthorizationServers, Is.EqualTo(new[] { Authority + "/v2.0" }));
            Assert.That(metadata.ScopesSupported, Is.EqualTo(new[] { "api://safeexchange.test/user_impersonation" }));
            Assert.That(metadata.BearerMethodsSupported, Is.EqualTo(new[] { "header" }));
        }

        [Test]
        public void ToMetadata_WithTheBrokerConfigured_NamesThisHostAsTheAuthorizationServer()
        {
            var resource = Create(new AuthenticationConfiguration { Authority = Authority, ValidAudiences = "api://safeexchange.test" }, BrokerClientId);

            var metadata = resource.ToMetadata(ApiBase);

            Assert.That(resource.IsBrokerConfigured, Is.True);
            Assert.That(metadata.Resource, Is.EqualTo("https://safeexchange.test/api/mcp"));
            Assert.That(metadata.AuthorizationServers, Is.EqualTo(new[] { "https://safeexchange.test/api" }));
            Assert.That(metadata.ScopesSupported, Is.EqualTo(new[] { "api://safeexchange.test/user_impersonation" }));
        }

        [TestCase("", Authority, McpOAuthTestKey.Value)]
        [TestCase("   ", Authority, McpOAuthTestKey.Value)]
        [TestCase(BrokerClientId, "", McpOAuthTestKey.Value)]
        [TestCase(BrokerClientId, "http://login.microsoftonline.com/00000000-0000-0000-0000-000000000001", McpOAuthTestKey.Value)]
        [TestCase(BrokerClientId, Authority, "")]
        [TestCase(BrokerClientId, Authority, "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHw==")]
        public void TheBroker_NeedsAClientIdAnHttpsAuthorityAndAStateKey(string clientId, string authority, string stateKey)
        {
            var resource = Create(new AuthenticationConfiguration { Authority = authority, ValidAudiences = "api://safeexchange.test" }, clientId, stateKey);

            Assert.That(resource.IsBrokerConfigured, Is.False);
            Assert.That(resource.ToMetadata(ApiBase).AuthorizationServers, Does.Not.Contain("https://safeexchange.test/api"));
        }

        private static McpProtectedResource Create(AuthenticationConfiguration configuration, string clientId = "", string stateKey = McpOAuthTestKey.Value)
            => new(
                Mock.Of<IOptionsMonitor<AuthenticationConfiguration>>(x => x.CurrentValue == configuration),
                Mock.Of<IOptionsMonitor<McpOAuthOptions>>(x => x.CurrentValue == new McpOAuthOptions { ClientId = clientId, StateKey = stateKey }));
    }
}
