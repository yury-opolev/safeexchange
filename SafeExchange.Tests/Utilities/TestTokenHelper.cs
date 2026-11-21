/// <summary>
/// TestTokenHelper
/// </summary>

namespace SafeExchange.Tests
{
    using SafeExchange.Core;
    using System.Security.Claims;

    public class TestTokenHelper : ITokenHelper
    {
        public TestTokenHelper()
        {
        }

        public string GetAccountId(ClaimsPrincipal principal)
        {
            return $"{this.GetTenantId(principal)}.{this.GetObjectId(principal)}";
        }

        public string GetDisplayName(ClaimsPrincipal principal)
        {
            return principal?.FindFirst("displayname")?.Value ?? string.Empty;
        }

        public string? GetObjectId(ClaimsPrincipal? principal)
        {
            return principal?.FindFirst("oid")?.Value ?? string.Empty;
        }

        public string? GetTenantId(ClaimsPrincipal? principal)
        {
            return principal?.FindFirst("tid")?.Value ?? string.Empty;
        }

        public TokenType GetTokenType(ClaimsPrincipal principal)
        {
            return TokenType.AccessToken;
        }

        public string GetUpn(ClaimsPrincipal principal)
        {
            return principal.FindFirst("upn")?.Value ?? string.Empty;
        }

        public string GetApplicationClientId(ClaimsPrincipal principal)
        {
            return string.Empty; // TODO: find application client id
        }

        public bool IsUserToken(ClaimsPrincipal principal)
        {
            return true;
        }
    }
}
