/// <summary>
/// TokenHelper
/// </summary>

namespace SafeExchange.Core
{
    using System;
    using System.Configuration;
    using System.Net.Http.Headers;
    using System.Security.Claims;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Configuration;

    public class TokenHelper : ITokenHelper
    {
        /// <summary>
        /// Claim types, in priority order, that <see cref="GetUpn"/> will consult
        /// when resolving the caller's UPN. Parsed once at construction from
        /// <see cref="AuthenticationConfiguration.UpnClaims"/>.
        /// </summary>
        private readonly string[] upnClaims;

        private readonly ILogger<TokenHelper> log;

        public TokenHelper(IConfiguration configuration, ILogger<TokenHelper> log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));

            var authConfig = new AuthenticationConfiguration();
            configuration.GetSection("Authentication").Bind(authConfig);

            this.upnClaims = (authConfig.UpnClaims ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (this.upnClaims.Length == 0)
            {
                throw new ConfigurationErrorsException(
                    "Authentication:UpnClaims must contain at least one claim name. " +
                    "Default is 'upn,http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn'.");
            }

            this.log.LogInformation(
                "TokenHelper UPN claim chain: [{UpnClaims}]",
                string.Join(", ", this.upnClaims));
        }

        /// <inheritdoc/>
        public TokenType GetTokenType(ClaimsPrincipal principal)
        {
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
                (HasClaim(principal, "http://schemas.microsoft.com/identity/claims/scope") ||
                HasClaim(principal, "scope") ||
                HasClaim(principal, "scp"));

            this.log.LogInformation($"Principal {userName} is authenticated as {(result ? "a user" : "an app")} with {tokenType}");
            return result;
        }

        /// <inheritdoc/>
        public string GetUpn(ClaimsPrincipal principal)
        {
            if (principal is null)
            {
                return string.Empty;
            }

            foreach (var claimType in this.upnClaims)
            {
                var value = principal.FindFirst(claimType)?.Value;
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        /// <inheritdoc/>
        public string GetApplicationClientId(ClaimsPrincipal principal)
        {
            var clientId = principal?.FindFirst("azp")?.Value;
            if (string.IsNullOrEmpty(clientId))
            {
                clientId = principal?.FindFirst("appid")?.Value;
            }

            return clientId ?? string.Empty;
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
        public AccountIdAndToken GetAccountIdAndToken(HttpRequestData request, ClaimsPrincipal principal)
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

        public string GetTenantId(ClaimsPrincipal? principal)
        {
            var tenantId = principal?.FindFirst("tid");
            if (tenantId == null)
            {
                tenantId = principal?.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid");
            }

            return tenantId?.Value ?? string.Empty;
        }

        private static bool HasClaim(ClaimsPrincipal principal, string type)
        {
            return principal.HasClaim(claim => claim.Type.Equals(type));
        }

        private static string GetAccessToken(HttpRequestData request)
        {
            if (!request.Headers.TryGetValues("Authorization", out var headerValues) || 
                !AuthenticationHeaderValue.TryParse(headerValues.FirstOrDefault(), out var authHeader))
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