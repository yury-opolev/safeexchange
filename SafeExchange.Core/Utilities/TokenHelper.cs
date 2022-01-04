/// <summary>
/// TokenHelper
/// </summary>
/// 
namespace SafeExchange.Core
{
    using System.Net.Http.Headers;
    using System.Security.Claims;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;

    public class TokenHelper : ITokenHelper
    {
        private readonly ILogger<TokenHelper> log;

        public TokenHelper(ILogger<TokenHelper> log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <inheritdoc/>
        public TokenType GetTokenType(ClaimsPrincipal principal)
        {
            var userName = this.GetUpn(principal);
            if (HasClaim(principal, "appid") && HasClaim(principal, "appidacr"))
            {
                return TokenType.AccessToken;
            }

            return TokenType.IdToken;
        }

        /// <inheritdoc/>
        public bool IsUserToken(ClaimsPrincipal principal)
        {
            var tokenType = this.GetTokenType(principal);
            var userName = this.GetUpn(principal);
            var result = (tokenType == TokenType.IdToken) || 
                (HasClaim(principal, "http://schemas.microsoft.com/identity/claims/scope") || HasClaim(principal, "scope"));

            this.log.LogInformation($"Principal {userName} is authenticated as a {(result ? "user" : "app")} with {tokenType}");
            return result;
        }

        /// <inheritdoc/>
        public string GetUpn(ClaimsPrincipal principal)
        {
            var result = principal.FindFirst(ClaimTypes.Upn)?.Value;
            if (string.IsNullOrEmpty(result))
            {
                result = principal.FindFirst("upn")?.Value;
            }

            if (string.IsNullOrEmpty(result))
            {
                result = principal.FindFirst("preferred_username")?.Value;
            }

            if (string.IsNullOrEmpty(result))
            {
                result = principal.FindFirst("email")?.Value;
            }

            if (string.IsNullOrEmpty(result))
            {
                result = principal.FindFirst(ClaimTypes.Email)?.Value;
            }

            return result ?? string.Empty;
        }

        /// <inheritdoc/>
        public string GetDisplayName(ClaimsPrincipal principal)
        {
            Claim? displayName = principal?.FindFirst("name");
            if (displayName != null)
            {
                return displayName.Value;
            }

            displayName = principal?.FindFirst(ClaimTypes.Name);
            if (displayName != null)
            {
                return displayName.Value;
            }

            Claim? givenName = principal?.FindFirst(ClaimTypes.GivenName);
            Claim? surName = principal?.FindFirst(ClaimTypes.Surname);
            if (givenName != null)
            {
                return $"{givenName?.Value} {surName?.Value}";
            }

            givenName = principal?.FindFirst("givenname");
            surName = principal?.FindFirst("surname");
            if (givenName != null || surName != null)
            {
                return $"{givenName?.Value} {surName?.Value}";
            }

            return string.Empty;
        }

        /// <inheritdoc/>
        public AccountIdAndToken GetAccountIdAndToken(HttpRequest request, ClaimsPrincipal principal)
        {
            var accessToken = GetAccessToken(request);
            var accountId = GetAccountId(principal);

            this.log.LogInformation($"{nameof(GetAccountIdAndToken)}: AccountId [{accountId}], token {(string.IsNullOrEmpty(accessToken) ? "is empty" : "extracted")}");

            return new AccountIdAndToken(accountId, accessToken);
        }

        public string? GetObjectId(ClaimsPrincipal? principal)
        {
            var objectId = principal?.FindFirst("oid");
            if (objectId == null)
            {
                objectId = principal?.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier");
            }

            return objectId?.Value ?? null;
        }

        public string? GetTenantId(ClaimsPrincipal? principal)
        {
            var tenantId = principal?.FindFirst("tid");
            if (tenantId == null)
            {
                tenantId = principal?.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid");
            }

            return tenantId?.Value ?? null;
        }

        private static bool HasClaim(ClaimsPrincipal principal, string type)
        {
            return principal.HasClaim(claim => claim.Type.Equals(type));
        }

        private static string GetAccessToken(HttpRequest request)
        {
            if (!request.Headers.ContainsKey("Authorization") || 
                !AuthenticationHeaderValue.TryParse(request.Headers["Authorization"], out var authHeader))
            {
                return string.Empty;
            }

            if (authHeader != null && authHeader.Scheme.ToLower() == "bearer" && !string.IsNullOrEmpty(authHeader.Parameter))
            {
                return authHeader.Parameter;
            }

            return string.Empty;
        }

        private string GetAccountId(ClaimsPrincipal principal)
        {
            var objectId = this.GetObjectId(principal);
            var tenantId = this.GetTenantId(principal);

            if (objectId != null && tenantId != null)
            {
                return $"{objectId}.{tenantId}";
            }

            return string.Empty;
        }
    }
}