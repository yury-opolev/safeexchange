/// <summary>
/// McpOAuthRateLimitOptions
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    /// <summary>
    /// <c>Mcp:OAuth:RateLimit</c>: per-instance, per-client-address token buckets for the anonymous broker endpoints
    /// and the cap on concurrent outbound Entra token calls. Values below 1 count as 1.
    /// </summary>
    public sealed class McpOAuthRateLimitOptions
    {
        public int TokenPermitsPerMinute { get; set; } = 30;

        public int TokenBurst { get; set; } = 30;

        /// <summary>
        /// Refill rate of the register, authorize and callback buckets.
        /// </summary>
        public int PermitsPerMinute { get; set; } = 60;

        public int Burst { get; set; } = 60;

        /// <summary>
        /// Most buckets kept per instance; the least recently used one is dropped beyond that.
        /// </summary>
        public int MaxClients { get; set; } = 10000;

        public int MaxConcurrentEntraCalls { get; set; } = 16;
    }
}
