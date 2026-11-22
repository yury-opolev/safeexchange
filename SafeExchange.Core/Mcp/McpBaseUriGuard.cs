/// <summary>
/// McpBaseUriGuard
/// </summary>

namespace SafeExchange.Core.Mcp
{
    using Microsoft.Extensions.Logging;
    using System;
    using System.Threading;

    /// <summary>
    /// The OAuth discovery documents and the broker publish URLs derived from the request URL, so they are served only
    /// for an <c>https</c> base, or plain <c>http</c> to a loopback host (local development). Anything else is answered
    /// as "not configured" (404); the first refusal per instance logs a Warning.
    /// </summary>
    public sealed class McpBaseUriGuard
    {
        private readonly ILogger log;

        private int warned;

        public McpBaseUriGuard(ILogger<McpBaseUriGuard> log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public bool IsAllowed(Uri apiBaseUri)
        {
            ArgumentNullException.ThrowIfNull(apiBaseUri);
            if (apiBaseUri.Scheme == Uri.UriSchemeHttps || (apiBaseUri.Scheme == Uri.UriSchemeHttp && apiBaseUri.IsLoopback))
            {
                return true;
            }

            if (Interlocked.Exchange(ref this.warned, 1) == 0)
            {
                this.log.LogWarning("MCP OAuth discovery and broker answer 404 for the non-https base {ApiBaseUri}; serve the app over https.", apiBaseUri.AbsoluteUri);
            }

            return false;
        }
    }
}
