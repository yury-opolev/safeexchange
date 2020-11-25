/// SafeExchange

namespace SpaceOyster.SafeExchange
{
    using System.Security.Claims;
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

        private static bool HasClaim(ClaimsPrincipal principal, string type, ILogger log)
        {
            return principal.HasClaim(claim => claim.Type.Equals(type));
        }
    }
}