/// <summary>
/// ITokenHelper
/// </summary>

namespace SafeExchange.Core
{
    using System.Security.Claims;

    public interface ITokenHelper
    {
        /// <summary>
        /// Get type of JWT token from ClaimsPrincipal
        /// </summary>
        /// <param name="principal"><see cref="ClaimsPrincipal">ClaimsPrincipal</see> for authenticated user.</param>
        /// <returns><see cref="TokenType">TokenType</see> enum value.</returns>
        public TokenType GetTokenType(ClaimsPrincipal principal);

        /// <summary>
        /// Return true if request bearer token is a user token, otherwise it is application token and return false.
        /// </summary>
        /// <param name="principal"><see cref="ClaimsPrincipal">ClaimsPrincipal</see> for authenticated user.</param>
        /// <returns>True if request bearer token is a user token, otherwise false.</returns>
        public bool IsUserToken(ClaimsPrincipal principal);

        /// <summary>
        /// Get user UPN from ClaimsPrincipal.
        /// </summary>
        /// <param name="principal"><see cref="ClaimsPrincipal">ClaimsPrincipal</see> for authenticated user.</param>
        /// <returns>A string, representing user UPN, extracted from given ClaimsPrincipal.</returns>
        public string GetUpn(ClaimsPrincipal principal);

        /// <summary>
        /// Get AAD application client id from ClaimsPrincipal.
        /// </summary>
        /// <param name="principal"><see cref="ClaimsPrincipal">ClaimsPrincipal</see> for authenticated user.</param>
        /// <returns>A string, representing AAD application client id, extracted from given ClaimsPrincipal.</returns>
        public string GetApplicationClientId(ClaimsPrincipal principal);

        /// <summary>
        /// Get user display name from ClaimsPrincipal.
        /// </summary>
        /// <param name="principal"><see cref="ClaimsPrincipal">ClaimsPrincipal</see> for authenticated user.</param>
        /// <returns>A string, representing user display name, extracted from given ClaimsPrincipal.</returns>
        public string GetDisplayName(ClaimsPrincipal principal);

        /// <summary>
        /// Get user 'ObjectId' from ClaimsPrincipal.
        /// </summary>
        /// <param name="principal"><see cref="ClaimsPrincipal">ClaimsPrincipal</see> for authenticated user.</param>
        /// <returns>A string, representing user 'ObjectId', extracted from given ClaimsPrincipal.</returns>
        public string? GetObjectId(ClaimsPrincipal? principal);

        /// <summary>
        /// Get 'TenantId' from ClaimsPrincipal.
        /// </summary>
        /// <param name="principal"><see cref="ClaimsPrincipal">ClaimsPrincipal</see> for authenticated user.</param>
        /// <returns>A string, representing 'TenantId', extracted from given ClaimsPrincipal.</returns>
        public string? GetTenantId(ClaimsPrincipal? principal);

        /// <summary>
        /// Get the account id ('{ObjectId}.{TenantId}') used for later on-behalf token acquisition.
        /// </summary>
        /// <param name="principal"><see cref="ClaimsPrincipal">ClaimsPrincipal</see> for authenticated user.</param>
        /// <returns>A string, representing the account id, or an empty string when the principal has no object id.</returns>
        public string GetAccountId(ClaimsPrincipal principal);
    }
}
