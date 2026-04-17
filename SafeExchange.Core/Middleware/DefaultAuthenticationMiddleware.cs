/// <summary>
/// DefaultAuthenticationMiddleware
/// </summary>

namespace SafeExchange.Core.Middleware
{
    using Microsoft.Azure.Functions.Worker.Middleware;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Extensions.Logging;
    using Microsoft.Azure.Functions.Worker.Http;
    using System.Net;
    using System.IdentityModel.Tokens.Jwt;
    using System.Net.Http.Headers;
    using Microsoft.IdentityModel.Tokens;

    public class DefaultAuthenticationMiddleware : IFunctionsWorkerMiddleware
    {
        public static readonly string ClaimsPrincipalKey = "ClaimsPrincipal";
        public static readonly string InvocationContextUserIdKey = "CurrentUserId";

        /// <summary>
        /// Fixed body text surfaced to the caller when JWT validation fails. Never
        /// contains the underlying <c>IDX…</c> reason — see the catch block in
        /// <see cref="Invoke"/> for the security rationale (OWASP A02:2025 / CWE-209).
        /// </summary>
        public const string InvalidTokenErrorMessage = "Invalid or expired token.";

        private const string AuthorizationHeaderName = "Authorization";

        private const string DefaultAuthorizationHeaderScheme = "Bearer";

        private readonly JwtSecurityTokenHandler tokenHandler;

        private readonly ITokenValidationParametersProvider validationParametersProvider;

        private readonly ILogger log;

        public DefaultAuthenticationMiddleware(ITokenValidationParametersProvider validationParametersProvider, ILogger<DefaultAuthenticationMiddleware> log)
        {
            this.validationParametersProvider = validationParametersProvider ?? throw new ArgumentNullException(nameof(validationParametersProvider));
            this.tokenHandler = new JwtSecurityTokenHandler();
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
        {
            var httpRequestData = await context.GetHttpRequestDataAsync();
            IEnumerable<string>? headers = default;
            if (!httpRequestData?.Headers?.TryGetValues(AuthorizationHeaderName, out headers) == true)
            {
                this.log.LogWarning("No authorization header present.");
                await UnauthorizedAsync(context, httpRequestData, "No authorization header present.");
                return;
            }

            if ((headers?.Count() ?? 0) != 1)
            {
                this.log.LogWarning("No authorization header present.");
                await UnauthorizedAsync(context, httpRequestData, "Authorization headers count is invalid.");
                return;
            }

            if (!AuthenticationHeaderValue.TryParse(headers!.First(), out var authenticationHeader))
            {
                this.log.LogWarning("Cannot parse authorization header.");
                await UnauthorizedAsync(context, httpRequestData, "Cannot parse authorization header.");
                return;
            }

            if (!string.Equals(authenticationHeader.Scheme, DefaultAuthorizationHeaderScheme, StringComparison.InvariantCultureIgnoreCase))
            {
                this.log.LogWarning($"Wrong authorization header scheme ({authenticationHeader.Scheme}).");
                await UnauthorizedAsync(context, httpRequestData, "Wrong authorization header scheme.");
                return;
            }

            try
            {
                var tokenValidationParameters = await this.validationParametersProvider.GetTokenValidationParametersAsync();
                var principal = this.tokenHandler.ValidateToken(authenticationHeader.Parameter, tokenValidationParameters, out _);
                context.Items[ClaimsPrincipalKey] = principal;
            }
            catch (Exception exception) when (exception is ArgumentException or SecurityTokenException)
            {
                // Log the real validation failure (issuer mismatch, expired token, invalid signature,
                // clock skew, IDX10205 / IDX10214 / IDX10223 etc.) server-side, but never surface it
                // to the caller — those messages let an unauthenticated attacker enumerate configured
                // audiences, issuers, tenant ids and signing-key rotation state by probing tokens in
                // a loop. OWASP A02:2025 / CWE-209.
                this.log.LogWarning(exception, "Token validation failed.");
                await UnauthorizedAsync(context, httpRequestData, InvalidTokenErrorMessage);
                return;
            }

            await next(context);
        }

        private static async Task UnauthorizedAsync(FunctionContext context, HttpRequestData? httpRequestData, string errorMessage)
        {
            var response = httpRequestData!.CreateResponse();
            response.StatusCode = HttpStatusCode.Unauthorized;
            await response.WriteAsJsonAsync(new BaseResponseObject<object> { Status = "unauthorized", Error = errorMessage });
            context.GetInvocationResult().Value = response;
        }
    }
}
