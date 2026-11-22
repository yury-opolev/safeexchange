/// <summary>
/// EntraEndpointsTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;

    [TestFixture]
    public class EntraEndpointsTests
    {
        private const string Tenant = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001";

        [TestCase(Tenant)]
        [TestCase(Tenant + "/")]
        [TestCase(Tenant + "/v2.0")]
        [TestCase(Tenant + "/V2.0/")]
        [TestCase("  " + Tenant + "/v2.0  ")]
        public void TheV2Endpoints_AreDerivedFromTheAuthority(string authority)
        {
            var endpoints = EntraEndpoints.FromAuthority(authority);

            Assert.That(endpoints, Is.EqualTo(new EntraEndpoints(Tenant + "/oauth2/v2.0/authorize", Tenant + "/oauth2/v2.0/token")));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("login.microsoftonline.com/tenant")]
        [TestCase("http://login.microsoftonline.com/tenant")]
        [TestCase("https://login.microsoftonline.com/tenant?x=1")]
        [TestCase("https://user@login.microsoftonline.com/tenant")]
        public void AnAuthorityThatIsNotAPlainHttpsUrl_GivesNoEndpoints(string? authority)
        {
            Assert.That(EntraEndpoints.FromAuthority(authority), Is.Null);
        }
    }
}
