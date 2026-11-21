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

    [TestFixture]
    public class McpProtectedResourceTests
    {
        private const string Authority = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001";

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
        public void ToMetadata_IsTheRfc9728Document()
        {
            var metadata = Create(new AuthenticationConfiguration { Authority = Authority, ValidAudiences = "api://safeexchange.test" }).ToMetadata();

            Assert.That(metadata.Resource, Is.EqualTo("api://safeexchange.test"));
            Assert.That(metadata.AuthorizationServers, Is.EqualTo(new[] { Authority + "/v2.0" }));
            Assert.That(metadata.ScopesSupported, Is.EqualTo(new[] { "api://safeexchange.test/user_impersonation" }));
            Assert.That(metadata.BearerMethodsSupported, Is.EqualTo(new[] { "header" }));
        }

        private static McpProtectedResource Create(AuthenticationConfiguration configuration)
            => new(Mock.Of<IOptionsMonitor<AuthenticationConfiguration>>(x => x.CurrentValue == configuration));
    }
}
