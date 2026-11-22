/// <summary>
/// McpOAuthThrottle
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using System;
    using System.Net;

    /// <summary>
    /// Per-instance, per-client-address rate limits of the anonymous broker endpoints (<c>Mcp:OAuth:RateLimit</c>;
    /// token 30/min burst 30, the others 60/min burst 60). Best effort: every instance counts on its own, clients are
    /// keyed by <see cref="ClientAddress"/> (IPv6 by /64 prefix) and requests without a client address share the bucket
    /// <c>unknown</c>. Logs each refusal at Warning without the address.
    /// </summary>
    public sealed class McpOAuthThrottle
    {
        public const string Register = "register";

        public const string Authorize = "authorize";

        public const string Callback = "callback";

        public const string Token = "token";

        private const string UnknownAddress = "unknown";

        private readonly IOptionsMonitor<McpOAuthOptions> oauth;

        private readonly ClientRateLimiter limiter;

        private readonly ILogger log;

        public McpOAuthThrottle(IOptionsMonitor<McpOAuthOptions> oauth, ILogger<McpOAuthThrottle> log)
        {
            this.oauth = oauth ?? throw new ArgumentNullException(nameof(oauth));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.limiter = new ClientRateLimiter(oauth.CurrentValue.RateLimit.MaxClients);
        }

        /// <summary>
        /// Null when the request may proceed; otherwise the 429 answer (JSON <c>slow_down</c> for register and token,
        /// the HTML page for authorize and callback) with <c>Retry-After</c>.
        /// </summary>
        public McpOAuthResult? Check(string endpoint, string? clientAddress)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            var limits = this.oauth.CurrentValue.RateLimit;
            var (permitsPerMinute, burst, isBrowserEndpoint) = endpoint switch
            {
                Token => (limits.TokenPermitsPerMinute, limits.TokenBurst, false),
                Register => (limits.PermitsPerMinute, limits.Burst, false),
                Authorize or Callback => (limits.PermitsPerMinute, limits.Burst, true),
                _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Not a broker endpoint."),
            };

            var key = endpoint + "|" + (string.IsNullOrEmpty(clientAddress) ? UnknownAddress : clientAddress);
            if (this.limiter.TryAcquire(key, Math.Max(1, permitsPerMinute), Math.Max(1, burst), DateTimeProvider.UtcNow, out var retryAfter))
            {
                return null;
            }

            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            this.log.LogWarning("MCP OAuth {Endpoint} rate limit exceeded; retry after {RetryAfterSeconds} s.", endpoint, seconds);
            var result = isBrowserEndpoint
                ? McpOAuthResult.Html(HttpStatusCode.TooManyRequests, ConsentPage.Message(ConsentPage.TooManyRequestsMessage))
                : McpOAuthResult.Error(HttpStatusCode.TooManyRequests, "slow_down", "Too many requests; retry after the Retry-After delay.");
            return result with { RetryAfterSeconds = seconds };
        }
    }
}
