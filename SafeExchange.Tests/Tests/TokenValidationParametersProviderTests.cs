/// <summary>
/// TokenValidationParametersProviderTests
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using Microsoft.IdentityModel.Tokens;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using System.Collections.Generic;
    using System.Configuration;
    using System.Linq;

    /// <summary>
    /// Tests for <see cref="TokenValidationParametersProvider"/>'s startup
    /// assertions. Locks in the OWASP A02:2025 / A07:2025 fix: the constructor
    /// must throw <see cref="ConfigurationErrorsException"/> whenever audience or
    /// issuer validation is disabled or the allowlist is empty, so that a missing
    /// config key cannot silently accept every AAD-signed token.
    /// </summary>
    [TestFixture]
    public class TokenValidationParametersProviderTests
    {
        private ILogger<TokenValidationParametersProvider> logger;

        [SetUp]
        public void Setup()
        {
            this.logger = new Mock<ILogger<TokenValidationParametersProvider>>().Object;
        }

        [Test]
        public void Ctor_MissingMetadataAddress_Throws()
        {
            var configuration = BuildConfiguration(
                ("Authentication:MetadataAddress", ""),
                ("Authentication:ValidateAudience", "true"),
                ("Authentication:ValidAudiences", "api://app"),
                ("Authentication:ValidateIssuer", "true"),
                ("Authentication:ValidIssuers", "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000/v2.0"));

            var ex = Assert.Throws<ConfigurationErrorsException>(
                () => new TokenValidationParametersProvider(configuration, this.logger));
            Assert.That(ex!.Message, Does.Contain("MetadataAddress"));
        }

        [Test]
        public void Ctor_ValidateAudienceExplicitlyFalse_Throws()
        {
            var configuration = BuildConfiguration(
                ("Authentication:MetadataAddress", "https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration"),
                ("Authentication:ValidateAudience", "false"),
                ("Authentication:ValidAudiences", "api://app"),
                ("Authentication:ValidateIssuer", "true"),
                ("Authentication:ValidIssuers", "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000/v2.0"));

            var ex = Assert.Throws<ConfigurationErrorsException>(
                () => new TokenValidationParametersProvider(configuration, this.logger));
            Assert.That(ex!.Message, Does.Contain("ValidateAudience"));
        }

        [Test]
        public void Ctor_ValidateIssuerExplicitlyFalse_Throws()
        {
            var configuration = BuildConfiguration(
                ("Authentication:MetadataAddress", "https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration"),
                ("Authentication:ValidateAudience", "true"),
                ("Authentication:ValidAudiences", "api://app"),
                ("Authentication:ValidateIssuer", "false"),
                ("Authentication:ValidIssuers", "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000/v2.0"));

            var ex = Assert.Throws<ConfigurationErrorsException>(
                () => new TokenValidationParametersProvider(configuration, this.logger));
            Assert.That(ex!.Message, Does.Contain("ValidateIssuer"));
        }

        [Test]
        public void Ctor_ValidAudiencesEmpty_Throws()
        {
            var configuration = BuildConfiguration(
                ("Authentication:MetadataAddress", "https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration"),
                ("Authentication:ValidateAudience", "true"),
                ("Authentication:ValidAudiences", ""),
                ("Authentication:ValidateIssuer", "true"),
                ("Authentication:ValidIssuers", "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000/v2.0"));

            var ex = Assert.Throws<ConfigurationErrorsException>(
                () => new TokenValidationParametersProvider(configuration, this.logger));
            Assert.That(ex!.Message, Does.Contain("ValidAudiences"));
        }

        [Test]
        public void Ctor_ValidIssuersEmpty_Throws()
        {
            var configuration = BuildConfiguration(
                ("Authentication:MetadataAddress", "https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration"),
                ("Authentication:ValidateAudience", "true"),
                ("Authentication:ValidAudiences", "api://app"),
                ("Authentication:ValidateIssuer", "true"),
                ("Authentication:ValidIssuers", "   "));

            var ex = Assert.Throws<ConfigurationErrorsException>(
                () => new TokenValidationParametersProvider(configuration, this.logger));
            Assert.That(ex!.Message, Does.Contain("ValidIssuers"));
        }

        [Test]
        public void Ctor_ValidAudiencesOnlyDelimiters_Throws()
        {
            // Edge case: single-character audience list of just commas/whitespace
            // must be rejected so an operator cannot silently bypass the allowlist.
            var configuration = BuildConfiguration(
                ("Authentication:MetadataAddress", "https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration"),
                ("Authentication:ValidateAudience", "true"),
                ("Authentication:ValidAudiences", " , , "),
                ("Authentication:ValidateIssuer", "true"),
                ("Authentication:ValidIssuers", "https://login.microsoftonline.com/tid/v2.0"));

            var ex = Assert.Throws<ConfigurationErrorsException>(
                () => new TokenValidationParametersProvider(configuration, this.logger));
            Assert.That(ex!.Message, Does.Contain("ValidAudiences"));
        }

        [Test]
        public void Ctor_MissingAuthenticationSection_ThrowsBecauseAllowlistsEmpty()
        {
            // With no configuration at all, the defaults on AuthenticationConfiguration
            // leave ValidateAudience/ValidateIssuer true (secure), and the allowlists
            // empty — the constructor must still fail loudly, not start a server that
            // accepts any signed token.
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>())
                .Build();

            Assert.Throws<ConfigurationErrorsException>(
                () => new TokenValidationParametersProvider(configuration, this.logger));
        }

        [Test]
        public async System.Threading.Tasks.Task Ctor_HappyPath_PopulatesValidationParameters()
        {
            var configuration = BuildConfiguration(
                ("Authentication:MetadataAddress", "https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration"),
                ("Authentication:ValidateAudience", "true"),
                ("Authentication:ValidAudiences", "api://app-one, api://app-two"),
                ("Authentication:ValidateIssuer", "true"),
                ("Authentication:ValidIssuers", "https://login.microsoftonline.com/tenant-a/v2.0,https://login.microsoftonline.com/tenant-b/v2.0"));

            var provider = new TokenValidationParametersProvider(configuration, this.logger);

            // GetTokenValidationParametersAsync also fetches the live JWKS set which
            // needs network access — exercise only the locally-populated fields.
            // Use reflection to read the stored TokenValidationParameters since
            // it's private. This keeps the test hermetic (no real network call).
            var field = typeof(TokenValidationParametersProvider).GetField(
                "tokenValidationParameters",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, "Expected private field tokenValidationParameters.");

            var tvp = (TokenValidationParameters)field!.GetValue(provider)!;

            Assert.That(tvp.ValidateAudience, Is.True);
            Assert.That(tvp.ValidateIssuer, Is.True);
            Assert.That(tvp.ValidateIssuerSigningKey, Is.True);
            Assert.That(tvp.ValidateLifetime, Is.True);
            Assert.That(tvp.RequireSignedTokens, Is.True);
            Assert.That(tvp.RequireExpirationTime, Is.True);

            // Allowlists are trimmed + parsed.
            Assert.That(
                tvp.ValidAudiences.ToArray(),
                Is.EqualTo(new[] { "api://app-one", "api://app-two" }));
            Assert.That(
                tvp.ValidIssuers.ToArray(),
                Is.EqualTo(new[]
                {
                    "https://login.microsoftonline.com/tenant-a/v2.0",
                    "https://login.microsoftonline.com/tenant-b/v2.0",
                }));

            // Algorithm allowlist must contain at least RS256 (AAD's default family).
            Assert.That(tvp.ValidAlgorithms, Is.Not.Null);
            Assert.That(tvp.ValidAlgorithms, Does.Contain(SecurityAlgorithms.RsaSha256));

            await System.Threading.Tasks.Task.CompletedTask;
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
    }
}
