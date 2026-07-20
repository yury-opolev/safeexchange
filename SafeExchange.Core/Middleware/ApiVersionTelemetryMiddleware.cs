/// <summary>
/// ApiVersionTelemetryMiddleware
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
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Records which API version each request targeted, so the operator can see how much
    /// traffic still hits v2 versus v3 and decide when v2 can be retired. The version comes from
    /// the <c>{apiVersion}</c> route segment. Stamps a <c>saex.apiVersion</c> custom dimension on
    /// every telemetry item of the invocation (via <see cref="ApiVersionTelemetryInitializer"/>);
    /// the per-request trace is Debug-only, since request telemetry already carries the dimension.
    /// No-ops for invocations without an <c>apiVersion</c> route value.
    /// </summary>
    public class ApiVersionTelemetryMiddleware : IFunctionsWorkerMiddleware
    {
        public const string RouteValueName = "apiVersion";

        public const string PropertyName = "saex.apiVersion";

        private static readonly AsyncLocal<string?> currentApiVersion = new();

        private readonly ILogger<ApiVersionTelemetryMiddleware> log;

        public ApiVersionTelemetryMiddleware(ILogger<ApiVersionTelemetryMiddleware> log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        internal static string? Current => currentApiVersion.Value;

        public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
        {
            var apiVersion = TryReadApiVersion(context);
            if (apiVersion is null)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var previous = currentApiVersion.Value;
            currentApiVersion.Value = apiVersion;
            try
            {
                this.log.LogDebug("saex api version {ApiVersion} {FunctionName}", apiVersion, context.FunctionDefinition.Name);
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                currentApiVersion.Value = previous;
            }
        }

        private static string? TryReadApiVersion(FunctionContext context)
        {
            if (!context.BindingContext.BindingData.TryGetValue(RouteValueName, out var raw))
            {
                return null;
            }

            var value = raw?.ToString()?.Trim('"');
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }

    /// <summary>
    /// Stamps the current request's API version onto every emitted telemetry item, so requests,
    /// traces and dependencies can be aggregated by <c>saex.apiVersion</c>. No-ops when the
    /// AsyncLocal is empty (non-HTTP invocations or routes without an apiVersion segment).
    /// </summary>
    public class ApiVersionTelemetryInitializer : ITelemetryInitializer
    {
        public void Initialize(ITelemetry telemetry)
        {
            var apiVersion = ApiVersionTelemetryMiddleware.Current;
            if (string.IsNullOrEmpty(apiVersion))
            {
                return;
            }

            if (telemetry is ISupportProperties supportsProperties
                && !supportsProperties.Properties.ContainsKey(ApiVersionTelemetryMiddleware.PropertyName))
            {
                supportsProperties.Properties[ApiVersionTelemetryMiddleware.PropertyName] = apiVersion;
            }
        }
    }
}
