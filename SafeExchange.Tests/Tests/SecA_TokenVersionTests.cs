/// <summary>
/// SecA_TokenVersionTests
/// Security regression tests for Cluster A (CWE-287): AAD v2 app-only
/// (client-credentials) tokens must be classified as app tokens, not user tokens.
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging.Abstractions;
    using NUnit.Framework;
    using SafeExchange.Core;
    using System.Collections.Generic;
    using System.Security.Claims;

    [TestFixture]
    public class SecA_TokenVersionTests
    {
        private static TokenHelper CreateTokenHelper()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:UpnClaims"] = "upn",
                })
                .Build();

            return new TokenHelper(configuration, NullLogger<TokenHelper>.Instance);
        }

        private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims)
        {
            var identity = new ClaimsIdentity();
            foreach (var (type, value) in claims)
            {
                identity.AddClaim(new Claim(type, value));
            }

            return new ClaimsPrincipal(identity);
        }

        [Test]
        public void V2_app_only_token_is_classified_as_app_not_user()
        {
            var tokenHelper = CreateTokenHelper();

            // AAD v2 app-only (client-credentials) token: carries azp + azpacr,
            // NO appid/appidacr, NO delegated user claims (scp/scope/upn).
            var principal = Principal(
                ("azp", "11111111-1111-1111-1111-111111111111"),
                ("azpacr", "2"),
                ("oid", "22222222-2222-2222-2222-222222222222"),
                ("tid", "33333333-3333-3333-3333-333333333333"));

            Assert.That(tokenHelper.GetTokenType(principal), Is.EqualTo(TokenType.AccessToken));
            Assert.That(tokenHelper.IsUserToken(principal), Is.False);
        }

        [Test]
        public void V1_app_only_token_is_classified_as_app()
        {
            var tokenHelper = CreateTokenHelper();

            // AAD v1 app-only token: carries appid + appidacr.
            var principal = Principal(
                ("appid", "44444444-4444-4444-4444-444444444444"),
                ("appidacr", "2"),
                ("oid", "55555555-5555-5555-5555-555555555555"),
                ("tid", "66666666-6666-6666-6666-666666666666"));

            Assert.That(tokenHelper.GetTokenType(principal), Is.EqualTo(TokenType.AccessToken));
            Assert.That(tokenHelper.IsUserToken(principal), Is.False);
        }

        [Test]
        public void V1_user_token_is_classified_as_user()
        {
            var tokenHelper = CreateTokenHelper();

            // Genuine user token: has upn, no app-only claims.
            var principal = Principal(
                ("upn", "user@contoso.com"),
                ("oid", "77777777-7777-7777-7777-777777777777"),
                ("tid", "88888888-8888-8888-8888-888888888888"));

            Assert.That(tokenHelper.IsUserToken(principal), Is.True);
        }

        [Test]
        public void V2_delegated_user_token_is_classified_as_user()
        {
            var tokenHelper = CreateTokenHelper();

            // Delegated (on-behalf-of a user) v2 token: carries azp/azpacr AND
            // a delegated scope claim (scp), plus user upn. Must remain a user token.
            var principal = Principal(
                ("azp", "99999999-9999-9999-9999-999999999999"),
                ("azpacr", "1"),
                ("scp", "user_impersonation"),
                ("upn", "user@contoso.com"));

            Assert.That(tokenHelper.IsUserToken(principal), Is.True);
        }
    }
}
