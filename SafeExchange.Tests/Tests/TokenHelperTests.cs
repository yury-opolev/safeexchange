/// <summary>
/// TokenHelperTests
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using System.Collections.Generic;
    using System.Configuration;
    using System.Security.Claims;

    /// <summary>
    /// Tests for <see cref="TokenHelper.GetUpn"/>. Locks in the tighter default
    /// UPN claim chain and the <c>Authentication:UpnClaims</c> configuration
    /// escape hatch introduced as OWASP A01:2025 / A06:2025 hardening.
    ///
    /// Before this change, <see cref="TokenHelper.GetUpn"/> always walked a
    /// hard-coded fallback chain: <c>ClaimTypes.Upn → upn → preferred_username →
    /// email → ClaimTypes.Email</c>. The last three are not guaranteed stable
    /// or unique across a tenant, so a token containing only an <c>email</c>
    /// claim could alias onto another user's permissions. This test fixture
    /// verifies:
    ///
    /// - the default chain is just the <c>upn</c> claim (in its two standard
    ///   forms) and nothing more;
    /// - custom comma-separated chains are parsed, trimmed and honored in
    ///   order;
    /// - an empty chain is rejected loudly at construction so an accidental
    ///   typo cannot silently disable UPN resolution;
    /// - a principal that only carries <c>email</c> / <c>preferred_username</c>
    ///   resolves to empty UPN under the default chain (regression guard for
    ///   the hardening).
    /// </summary>
    [TestFixture]
    public class TokenHelperTests
    {
        private const string ClaimTypesUpn = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn";

        private ILogger<TokenHelper> logger;

        [SetUp]
        public void Setup()
        {
            this.logger = new Mock<ILogger<TokenHelper>>().Object;
        }

        [Test]
        public void Ctor_MissingUpnClaimsConfig_UsesSafeDefaultChain()
        {
            // Configuration has no Authentication:UpnClaims entry at all → binder
            // falls back to the property default in AuthenticationConfiguration.
            var configuration = BuildConfiguration();
            var helper = new TokenHelper(configuration, this.logger);

            var principalWithUpnClaim = BuildPrincipal(("upn", "alice@test.test"));
            Assert.That(helper.GetUpn(principalWithUpnClaim), Is.EqualTo("alice@test.test"));
        }

        [Test]
        public void Ctor_EmptyUpnClaimsConfig_ThrowsConfigurationErrorsException()
        {
            // Setting it to an explicit empty string must NOT silently accept it —
            // otherwise a typo in Key Vault could disable UPN resolution.
            var configuration = BuildConfiguration(("Authentication:UpnClaims", ""));
            var ex = Assert.Throws<ConfigurationErrorsException>(
                () => new TokenHelper(configuration, this.logger));
            Assert.That(ex!.Message, Does.Contain("UpnClaims"));
        }

        [Test]
        public void Ctor_WhitespaceOnlyUpnClaimsConfig_ThrowsConfigurationErrorsException()
        {
            var configuration = BuildConfiguration(("Authentication:UpnClaims", " , , "));
            Assert.Throws<ConfigurationErrorsException>(
                () => new TokenHelper(configuration, this.logger));
        }

        [Test]
        public void GetUpn_NullPrincipal_ReturnsEmptyString()
        {
            var helper = new TokenHelper(BuildConfiguration(), this.logger);
            Assert.That(helper.GetUpn(null!), Is.EqualTo(string.Empty));
        }

        [Test]
        public void GetUpn_DefaultChain_FindsRawUpnClaim()
        {
            var helper = new TokenHelper(BuildConfiguration(), this.logger);
            var principal = BuildPrincipal(("upn", "alice@test.test"));

            Assert.That(helper.GetUpn(principal), Is.EqualTo("alice@test.test"));
        }

        [Test]
        public void GetUpn_DefaultChain_FindsClaimTypesUpn()
        {
            var helper = new TokenHelper(BuildConfiguration(), this.logger);
            var principal = BuildPrincipal((ClaimTypesUpn, "alice@test.test"));

            Assert.That(helper.GetUpn(principal), Is.EqualTo("alice@test.test"));
        }

        [Test]
        public void GetUpn_DefaultChain_IgnoresPreferredUsername()
        {
            // Regression guard: on the default chain, a token that carries ONLY
            // preferred_username (no upn claim) must NOT resolve — otherwise we
            // re-introduce the email-aliasing attack the tightening was meant
            // to close.
            var helper = new TokenHelper(BuildConfiguration(), this.logger);
            var principal = BuildPrincipal(("preferred_username", "alice@test.test"));

            Assert.That(helper.GetUpn(principal), Is.EqualTo(string.Empty));
        }

        [Test]
        public void GetUpn_DefaultChain_IgnoresEmailClaim()
        {
            var helper = new TokenHelper(BuildConfiguration(), this.logger);
            var principal = BuildPrincipal(("email", "alice@test.test"));

            Assert.That(helper.GetUpn(principal), Is.EqualTo(string.Empty));
        }

        [Test]
        public void GetUpn_CustomChainIncludesPreferredUsername_ReturnsPreferredUsername()
        {
            // Operator deliberately relaxed the chain via config — escape hatch.
            var configuration = BuildConfiguration(
                ("Authentication:UpnClaims", "upn,preferred_username,email"));
            var helper = new TokenHelper(configuration, this.logger);

            var principal = BuildPrincipal(("preferred_username", "alice@test.test"));
            Assert.That(helper.GetUpn(principal), Is.EqualTo("alice@test.test"));
        }

        [Test]
        public void GetUpn_CustomChain_FirstMatchWins()
        {
            // Chain is upn, preferred_username, email — principal has all three,
            // but the caller's intent is "prefer upn." Even though the token
            // contains both upn and email, the chain order must be honored.
            var configuration = BuildConfiguration(
                ("Authentication:UpnClaims", "upn,preferred_username,email"));
            var helper = new TokenHelper(configuration, this.logger);

            var principal = BuildPrincipal(
                ("upn", "alice-upn@test.test"),
                ("preferred_username", "alice-preferred@test.test"),
                ("email", "alice-email@test.test"));

            Assert.That(helper.GetUpn(principal), Is.EqualTo("alice-upn@test.test"));
        }

        [Test]
        public void GetUpn_CustomChain_FallsThroughToLaterEntries()
        {
            var configuration = BuildConfiguration(
                ("Authentication:UpnClaims", "upn,preferred_username,email"));
            var helper = new TokenHelper(configuration, this.logger);

            // upn claim absent → fall through to preferred_username.
            var principal = BuildPrincipal(
                ("preferred_username", "alice-preferred@test.test"),
                ("email", "alice-email@test.test"));

            Assert.That(helper.GetUpn(principal), Is.EqualTo("alice-preferred@test.test"));
        }

        [Test]
        public void GetUpn_CustomChain_WhitespaceAndEmptyEntriesAreSkipped()
        {
            // Operator typo with stray whitespace and empty entries must still
            // produce a valid chain.
            var configuration = BuildConfiguration(
                ("Authentication:UpnClaims", " upn , , preferred_username , "));
            var helper = new TokenHelper(configuration, this.logger);

            var principal = BuildPrincipal(("preferred_username", "alice@test.test"));
            Assert.That(helper.GetUpn(principal), Is.EqualTo("alice@test.test"));
        }

        [Test]
        public void GetUpn_NoMatchingClaim_ReturnsEmpty()
        {
            var helper = new TokenHelper(BuildConfiguration(), this.logger);
            var principal = BuildPrincipal(
                ("displayname", "Alice Example"),
                ("oid", "00000000-0000-0000-0000-000000000001"),
                ("tid", "00000000-0000-0000-0000-000000000002"));

            Assert.That(helper.GetUpn(principal), Is.EqualTo(string.Empty));
        }

        [Test]
        public void GetUpn_PrincipalWithEmptyUpnClaimValue_FallsThroughToNextInChain()
        {
            var configuration = BuildConfiguration(
                ("Authentication:UpnClaims", "upn,preferred_username"));
            var helper = new TokenHelper(configuration, this.logger);

            // Token explicitly sets upn to an empty string — the foreach should
            // skip it and match the next configured claim instead.
            var principal = BuildPrincipal(
                ("upn", ""),
                ("preferred_username", "alice@test.test"));

            Assert.That(helper.GetUpn(principal), Is.EqualTo("alice@test.test"));
        }

        private static IConfiguration BuildConfiguration(params (string key, string value)[] entries)
        {
            var dict = new Dictionary<string, string?>();
            foreach (var (key, value) in entries)
            {
                dict[key] = value;
            }

            return new ConfigurationBuilder()
                .AddInMemoryCollection(dict)
                .Build();
        }

        private static ClaimsPrincipal BuildPrincipal(params (string type, string value)[] claims)
        {
            var claimsList = new List<Claim>();
            foreach (var (type, value) in claims)
            {
                claimsList.Add(new Claim(type, value));
            }

            var identity = new ClaimsIdentity(claimsList);
            return new ClaimsPrincipal(identity);
        }
    }
}
