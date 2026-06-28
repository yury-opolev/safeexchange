/// <summary>
/// S2SAllowedTenantTests — unit tests for the configuration parser that turns the
/// Authentication:S2SAllowedTenants JSON setting into a typed, validated list.
/// Parsing must FAIL CLOSED: malformed / missing config yields an empty list
/// (no S2S tenants trusted), never an exception that takes the app down.
/// </summary>

namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Configuration;
    using System.Linq;

    [TestFixture]
    public class S2SAllowedTenantTests
    {
        private const string TenantA = "11111111-1111-1111-1111-111111111111";
        private const string TenantB = "22222222-2222-2222-2222-222222222222";

        [Test]
        public void ParseList_null_or_empty_returns_empty()
        {
            Assert.That(S2SAllowedTenant.ParseList(null), Is.Empty);
            Assert.That(S2SAllowedTenant.ParseList(string.Empty), Is.Empty);
            Assert.That(S2SAllowedTenant.ParseList("   "), Is.Empty);
        }

        [Test]
        public void ParseList_valid_json_returns_entries()
        {
            var json = $"[{{\"tenantId\":\"{TenantA}\",\"displayName\":\"Contoso\"}},{{\"tenantId\":\"{TenantB}\",\"displayName\":\"Fabrikam\"}}]";

            var result = S2SAllowedTenant.ParseList(json);

            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(result[0].TenantId, Is.EqualTo(TenantA));
            Assert.That(result[0].DisplayName, Is.EqualTo("Contoso"));
            Assert.That(result[1].TenantId, Is.EqualTo(TenantB));
            Assert.That(result[1].DisplayName, Is.EqualTo("Fabrikam"));
        }

        [Test]
        public void ParseList_missing_displayName_defaults_to_tenantId()
        {
            var json = $"[{{\"tenantId\":\"{TenantA}\"}}]";

            var result = S2SAllowedTenant.ParseList(json);

            Assert.That(result.Count, Is.EqualTo(1));
            Assert.That(result[0].DisplayName, Is.EqualTo(TenantA));
        }

        [Test]
        public void ParseList_skips_non_guid_tenant_ids()
        {
            var json = $"[{{\"tenantId\":\"not-a-guid\"}},{{\"tenantId\":\"{TenantA}\"}}]";

            var result = S2SAllowedTenant.ParseList(json);

            Assert.That(result.Select(t => t.TenantId), Is.EqualTo(new[] { TenantA }));
        }

        [Test]
        public void ParseList_malformed_json_fails_closed_to_empty()
        {
            Assert.That(S2SAllowedTenant.ParseList("not json at all"), Is.Empty);
            Assert.That(S2SAllowedTenant.ParseList("{\"tenantId\":\"x\"}"), Is.Empty); // object, not array
        }

        [Test]
        public void ParseList_dedupes_by_tenant_id_case_insensitive_keeping_first()
        {
            var json = $"[{{\"tenantId\":\"{TenantA}\",\"displayName\":\"First\"}},{{\"tenantId\":\"{TenantA.ToUpperInvariant()}\",\"displayName\":\"Second\"}}]";

            var result = S2SAllowedTenant.ParseList(json);

            Assert.That(result.Count, Is.EqualTo(1));
            Assert.That(result[0].DisplayName, Is.EqualTo("First"));
        }

        [Test]
        public void Contains_matches_tenant_id_case_insensitively()
        {
            var list = S2SAllowedTenant.ParseList($"[{{\"tenantId\":\"{TenantA}\"}}]");

            Assert.That(S2SAllowedTenant.Contains(list, TenantA.ToUpperInvariant()), Is.True);
            Assert.That(S2SAllowedTenant.Contains(list, TenantB), Is.False);
            Assert.That(S2SAllowedTenant.Contains(list, null), Is.False);
        }
    }
}
