/// <summary>
/// SessionCorrelationMiddleware
/// </summary>

namespace SafeExchange.Core.Middleware
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Middleware;
    using Microsoft.Extensions.Logging;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;

    /// <summary>
    /// Reads the browser's session correlation id from the
    /// <c>x-saex-session-id</c> HTTP header and pushes it into an
    /// <see cref="ILogger"/> scope for the duration of the function
    /// invocation. Every <c>LogInformation</c> / <c>LogError</c> made
    /// by downstream code picks the value up automatically and emits
    /// it under <c>customDimensions["saex.sessionId"]</c> in
    /// Application Insights, which lets a single Kusto query stitch
    /// a browser session across the client PWA and the backend.
    ///
    /// Degrades silently:
    ///   - Header missing — skips the scope; request proceeds
    ///     normally without the dimension.
    ///   - Header present but not a 32-char hex GUID — skips the
    ///     scope and logs a debug line. We only accept the client's
    ///     own format (TelemetryService uses Guid.NewGuid().ToString("n"))
    ///     so a malformed header never lands verbatim in telemetry.
    ///   - No HTTP trigger on this invocation — skips; some
    ///     functions (queue triggers, timers) have no headers.
    ///
    /// Runs AFTER DefaultAuthenticationMiddleware so that failed
    /// auth requests get correlated too (useful for diagnosing
    /// auth-token issues per session).
    /// </summary>
    public class SessionCorrelationMiddleware : IFunctionsWorkerMiddleware
    {
        public const string HeaderName = "x-saex-session-id";

        public const string LoggerPropertyName = "saex.sessionId";

        /// <summary>
        /// Accept only the exact format the client emits —
        /// <see cref="Guid.ToString"/> with the "n" specifier
        /// (32 lowercase hex chars, no dashes). Anything else is
        /// rejected to bound the risk of an attacker sneaking
        /// arbitrary strings into our telemetry dimension.
        /// </summary>
        private static readonly Regex SessionIdPattern = new("^[0-9a-f]{32}$", RegexOptions.Compiled);

        private readonly ILogger<SessionCorrelationMiddleware> log;

        public SessionCorrelationMiddleware(ILogger<SessionCorrelationMiddleware> log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
        {
            var sessionId = await this.TryReadSessionIdAsync(context).ConfigureAwait(false);
            if (sessionId is null)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            using (this.log.BeginScope(new Dictionary<string, object>
            {
                [LoggerPropertyName] = sessionId
            }))
            {
                await next(context).ConfigureAwait(false);
            }
        }

        private async Task<string?> TryReadSessionIdAsync(FunctionContext context)
        {
            var requestData = await context.GetHttpRequestDataAsync().ConfigureAwait(false);
            if (requestData is null)
            {
                return null;
            }

            if (!requestData.Headers.TryGetValues(HeaderName, out var values))
            {
                return null;
            }

            var raw = values?.FirstOrDefault();
            if (string.IsNullOrEmpty(raw))
            {
                return null;
            }

            if (!SessionIdPattern.IsMatch(raw))
            {
                this.log.LogDebug("Received {Header} header with unexpected format; dropping.", HeaderName);
                return null;
            }

            return raw;
        }
    }
}
