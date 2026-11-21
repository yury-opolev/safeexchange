/// <summary>
/// TokenHelperHttpExtensions
/// </summary>

namespace SafeExchange.Core
{
    using System.Net.Http.Headers;
    using System.Security.Claims;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;

    public static class TokenHelperHttpExtensions
    {
        /// <summary>
        /// Extract token and AccountId for later on-behalf token acquisition.
        /// </summary>
        /// <param name="tokenHelper">Token helper that resolves the account id.</param>
        /// <param name="request">Incoming http request.</param>
        /// <param name="principal"><see cref="ClaimsPrincipal">ClaimsPrincipal</see> for authenticated user.</param>
        /// <returns><see cref="AccountIdAndToken">AccountIdAndToken</see>.</returns>
        public static AccountIdAndToken GetAccountIdAndToken(this ITokenHelper tokenHelper, HttpRequestData request, ClaimsPrincipal principal)
        {
            var accessToken = GetAccessToken(request);
            var accountId = tokenHelper.GetAccountId(principal);

            // Logged under the TokenHelper category, where this trace was written before it moved here.
            var log = request.FunctionContext.InstanceServices?.GetService<ILoggerFactory>()?.CreateLogger<TokenHelper>()
                ?? (ILogger)NullLogger.Instance;
            log.LogInformation($"{nameof(GetAccountIdAndToken)}: AccountId [{accountId}], token {(string.IsNullOrEmpty(accessToken) ? "is empty" : "extracted")}");

            return new AccountIdAndToken(accountId, accessToken);
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
    }
}
