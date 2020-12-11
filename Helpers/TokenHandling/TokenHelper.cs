/// SafeExchange

namespace SpaceOyster.SafeExchange
{
    using System.Net.Http.Headers;
    using System.Security.Claims;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;

    public static class TokenHelper
    {
        public static TokenType GetTokenType(ClaimsPrincipal principal, ILogger log)
        {
            var userName = GetName(principal);
            if (HasClaim(principal, "appid", log) && HasClaim(principal, "appidacr", log))
            {
                log.LogInformation($"Principal {userName} is authenticated with access token");
                return TokenType.AccessToken;
            }

            log.LogInformation($"Principal {userName} is authenticated with id token");
            return TokenType.IdToken;
        }

        public static bool IsUserAccessToken(ClaimsPrincipal principal, ILogger log)
        {
            var tokenType = GetTokenType(principal, log);
            if (tokenType != TokenType.AccessToken)
            {
                return false;
            }

            var userName = GetName(principal);
            var result = HasClaim(principal, "http://schemas.microsoft.com/identity/claims/scope", log) || HasClaim(principal, "scope", log);
            log.LogInformation($"Principal {userName} is authenticated as user");
            return result;
        }

        public static string GetName(ClaimsPrincipal principal)
        {
            var result = principal.Identity.Name;

            if (string.IsNullOrEmpty(result))
            {
                result = principal.FindFirst("preferred_username")?.Value;
            }
            if (string.IsNullOrEmpty(result))
            {
                result = principal.FindFirst(ClaimTypes.Upn)?.Value;
            }
            if (string.IsNullOrEmpty(result))
            {
                result = principal.FindFirst("upn")?.Value;
            }
            return result;
        }

        public static string GetId(ClaimsPrincipal principal)
        {
            return principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value ?? principal.FindFirst("oid")?.Value;
        }

        public static TokenResult GetTokenResult(HttpRequest request, ClaimsPrincipal principal, ILogger log)
        {
            var accessToken = GetAccessToken(request);
            var accountId = GetAccountId(principal);

            log.LogInformation($"GetTokenResult: AccountId [{accountId}], token {(string.IsNullOrEmpty(accessToken) ? "is empty" : "extracted")}");

            return new TokenResult(accountId, accessToken);
        }

        private static bool HasClaim(ClaimsPrincipal principal, string type, ILogger log)
        {
            return principal.HasClaim(claim => claim.Type.Equals(type));
        }

        private static string GetAccessToken(HttpRequest request)
        {
            if (!request.Headers.ContainsKey("Authorization") || 
                !AuthenticationHeaderValue.TryParse(request.Headers["Authorization"], out var authHeader))
            {
                return null;
            }

            if (authHeader != null && authHeader.Scheme.ToLower() == "bearer" && !string.IsNullOrEmpty(authHeader.Parameter))
            {
                return authHeader.Parameter;
            }

            return null;
        }

        private static string GetAccountId(ClaimsPrincipal principal)
        {
            var objectId = principal?.FindFirst("oid");
            if (objectId == null)
            {
                objectId = principal?.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier");
            }

            var tenantId = principal?.FindFirst("tid");
            if (tenantId == null)
            {
                tenantId = principal?.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid");
            }

            if (objectId != null && tenantId != null)
            {
                return $"{objectId.Value}.{tenantId.Value}";
            }

            return null;
        }
    }
}