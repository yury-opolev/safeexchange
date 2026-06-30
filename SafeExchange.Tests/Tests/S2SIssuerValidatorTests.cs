/// <summary>
/// S2SIssuerValidatorTests — the heart of the cross-tenant S2S feature. The
/// validator must accept app-only tokens from the configured home tenant OR any
/// allowlisted tenant, accept user tokens ONLY from the home tenant, and reject
/// everything else with a SecurityTokenInvalidIssuerException.
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.IdentityModel.Tokens;
    using NUnit.Framework;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Utilities;
    using System.Collections.Generic;

    [TestFixture]
    public class S2SIssuerValidatorTests
    {
        private const string Home = "00000000-0000-0000-0000-0000000000aa";
        private const string Allowed = "11111111-1111-1111-1111-111111111111";
        private const string Other = "99999999-9999-9999-9999-999999999999";

        private static string V2(string tid) => $"https://login.microsoftonline.com/{tid}/v2.0";
        private static string V1(string tid) => $"https://sts.windows.net/{tid}/";

        private static S2SIssuerValidator Validator()
        {
            var configured = new[] { V2(Home), V1(Home) };
            var allowed = S2SAllowedTenant.ParseList($"[{{\"tenantId\":\"{Allowed}\",\"displayName\":\"Dev\"}}]");
            return new S2SIssuerValidator(configured, allowed);
        }

        [Test]
        public void Home_issuer_accepted_for_user_token()
            => Assert.That(Validator().Validate(V2(Home), isAppOnlyToken: false), Is.EqualTo(V2(Home)));

        [Test]
        public void Home_issuer_accepted_for_app_token()
            => Assert.That(Validator().Validate(V1(Home), isAppOnlyToken: true), Is.EqualTo(V1(Home)));

        [Test]
        public void Allowlisted_issuer_accepted_for_app_token_v2_and_v1()
        {
            Assert.That(Validator().Validate(V2(Allowed), isAppOnlyToken: true), Is.EqualTo(V2(Allowed)));
            Assert.That(Validator().Validate(V1(Allowed), isAppOnlyToken: true), Is.EqualTo(V1(Allowed)));
        }

        [Test]
        public void Allowlisted_issuer_rejected_for_user_token()
            => Assert.Throws<SecurityTokenInvalidIssuerException>(
                () => Validator().Validate(V2(Allowed), isAppOnlyToken: false));

        [Test]
        public void Unknown_issuer_rejected_for_app_token()
            => Assert.Throws<SecurityTokenInvalidIssuerException>(
                () => Validator().Validate(V2(Other), isAppOnlyToken: true));

        [Test]
        public void Unknown_issuer_rejected_for_user_token()
            => Assert.Throws<SecurityTokenInvalidIssuerException>(
                () => Validator().Validate(V2(Other), isAppOnlyToken: false));

        [Test]
        public void Issuer_match_is_case_insensitive_on_tenant_guid()
            => Assert.That(Validator().Validate(V2(Allowed.ToUpperInvariant()), isAppOnlyToken: true),
                Is.EqualTo(V2(Allowed.ToUpperInvariant())));
    }
}
