/// <summary>
/// TokenClassificationTests — locks in the shared rule that distinguishes an
/// app-only (client-credentials) token from a delegated user token. The S2S
/// issuer allowlist relaxes issuer validation ONLY for app-only tokens, so this
/// classification is security-load-bearing (CWE-287). It must stay identical to
/// the logic TokenHelper uses on the live auth path.
/// </summary>

namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Utilities;
    using System;
    using System.Linq;

    [TestFixture]
    public class TokenClassificationTests
    {
        private static Func<string, bool> Claims(params string[] types)
            => t => types.Contains(t, StringComparer.Ordinal);

        [Test]
        public void AppOnly_v1_appid_appidacr_without_scope_is_true()
            => Assert.That(TokenClassification.IsAppOnlyToken(Claims("appid", "appidacr")), Is.True);

        [Test]
        public void AppOnly_v2_azp_azpacr_without_scope_is_true()
            => Assert.That(TokenClassification.IsAppOnlyToken(Claims("azp", "azpacr")), Is.True);

        [Test]
        public void AppCredential_claims_but_with_delegated_scope_is_not_app_only()
        {
            Assert.That(TokenClassification.IsAppOnlyToken(Claims("appid", "appidacr", "scp")), Is.False);
            Assert.That(TokenClassification.IsAppOnlyToken(Claims("azp", "azpacr", "scope")), Is.False);
            Assert.That(TokenClassification.IsAppOnlyToken(
                Claims("azp", "azpacr", "http://schemas.microsoft.com/identity/claims/scope")), Is.False);
        }

        [Test]
        public void Partial_app_credential_claims_are_not_app_only()
        {
            // Needs BOTH of a pair — a lone appid (v1 delegated tokens carry appid too) must not qualify.
            Assert.That(TokenClassification.IsAppOnlyToken(Claims("appid")), Is.False);
            Assert.That(TokenClassification.IsAppOnlyToken(Claims("azp")), Is.False);
        }

        [Test]
        public void No_claims_is_not_app_only()
            => Assert.That(TokenClassification.IsAppOnlyToken(Claims()), Is.False);

        [Test]
        public void HasDelegatedScopeClaim_recognizes_all_three_forms()
        {
            Assert.That(TokenClassification.HasDelegatedScopeClaim(Claims("scp")), Is.True);
            Assert.That(TokenClassification.HasDelegatedScopeClaim(Claims("scope")), Is.True);
            Assert.That(TokenClassification.HasDelegatedScopeClaim(
                Claims("http://schemas.microsoft.com/identity/claims/scope")), Is.True);
            Assert.That(TokenClassification.HasDelegatedScopeClaim(Claims("appid")), Is.False);
        }
    }
}
