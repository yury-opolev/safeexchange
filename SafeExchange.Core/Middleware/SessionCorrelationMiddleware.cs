/// <summary>
/// SessionCorrelationMiddleware
/// </summary>

namespace SafeExchange.Core.Middleware
{
    using Microsoft.ApplicationInsights.Channel;
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.ApplicationInsights.Extensibility;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Middleware;
    using Microsoft.Extensions.Logging;
    using System;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Reads the browser's session correlation id from the
    /// <c>x-saex-session-id</c> HTTP header and attaches it as the
    /// <c>saex.sessionId</c> custom dimension on every telemetry item
    /// (trace, exception, dependency, request) emitted during the
    /// function invocation.
    ///
    /// Uses an <see cref="AsyncLocal{T}"/> plus a registered
    /// <see cref="ITelemetryInitializer"/> rather than
    /// <see cref="ILogger.BeginScope(object)"/> because the Azure
    /// Functions isolated-worker host's Application Insights logger
    /// integration does not reliably copy external ILogger scopes
    /// into telemetry customDimensions. The AsyncLocal is populated
    /// for the duration of <c>next(context)</c>, so every downstream
    /// log in the same async call chain picks up the value.
    ///
    /// Degrades silently:
    ///   - Header missing — skips; request proceeds without the dim.
    ///   - Header present but not a 32-char hex GUID — skips + debug
    ///     log. Only the client's own Guid.ToString("n") shape is
    ///     accepted, so a malformed header never lands in telemetry.
    ///   - No HTTP trigger on this invocation — skips.
    ///
    /// Runs AFTER DefaultAuthenticationMiddleware so failed-auth
    /// requests still get the dimension (useful when diagnosing
    /// token issues per session).
    /// </summary>
    public class SessionCorrelationMiddleware : IFunctionsWorkerMiddleware
    {
        public const string HeaderName = "x-saex-session-id";

        public const string PropertyName = "saex.sessionId";

        private static readonly AsyncLocal<string?> currentSessionId = new();

        private static readonly Regex SessionIdPattern = new("^[0-9a-f]{32}$", RegexOptions.Compiled);

        private readonly ILogger<SessionCorrelationMiddleware> log;

        public SessionCorrelationMiddleware(ILogger<SessionCorrelationMiddleware> log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        internal static string? Current => currentSessionId.Value;

        public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
        {
            var sessionId = await this.TryReadSessionIdAsync(context).ConfigureAwait(false);
            if (sessionId is null)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var previous = currentSessionId.Value;
            currentSessionId.Value = sessionId;
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                currentSessionId.Value = previous;
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

    /// <summary>
    /// Stamps the current request's saex.sessionId onto every
    /// emitted telemetry item. Reads the AsyncLocal maintained by
    /// <see cref="SessionCorrelationMiddleware"/>; no-ops when the
    /// AsyncLocal is empty (non-HTTP invocations, missing header,
    /// etc.).
    /// </summary>
    public class SessionCorrelationTelemetryInitializer : ITelemetryInitializer
    {
        public void Initialize(ITelemetry telemetry)
        {
            var sessionId = SessionCorrelationMiddleware.Current;
            if (string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            if (telemetry is ISupportProperties supportsProperties
                && !supportsProperties.Properties.ContainsKey(SessionCorrelationMiddleware.PropertyName))
            {
                supportsProperties.Properties[SessionCorrelationMiddleware.PropertyName] = sessionId;
            }
        }
    }
}
