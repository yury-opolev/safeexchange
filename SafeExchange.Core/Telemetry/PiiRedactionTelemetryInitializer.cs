/// <summary>
/// PiiRedactionTelemetryInitializer
/// </summary>

namespace SafeExchange.Core.Telemetry
{
    using Microsoft.ApplicationInsights.Channel;
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.ApplicationInsights.Extensibility;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core.Configuration;
    using System;
    using System.Text.RegularExpressions;

    /// <summary>Safety net: when Features.RedactTelemetryPii is enabled, redacts
    /// email/UPN-shaped substrings from trace/exception message text. Deliberately
    /// does NOT touch GUIDs (oid/tenant/secret ids/telemetryId) or display names.
    /// Flag is read live via IOptionsMonitor so it can be toggled without redeploy.</summary>
    public class PiiRedactionTelemetryInitializer : ITelemetryInitializer
    {
        private const string Replacement = "[redacted]";

        // Linear, no nested quantifiers -> no catastrophic backtracking.
        private static readonly Regex EmailLike =
            new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);

        private readonly IOptionsMonitor<Features> features;

        public PiiRedactionTelemetryInitializer(IOptionsMonitor<Features> features)
        {
            this.features = features ?? throw new ArgumentNullException(nameof(features));
        }

        public void Initialize(ITelemetry telemetry)
        {
            if (!this.features.CurrentValue.RedactTelemetryPii)
            {
                return;
            }

            if (telemetry is TraceTelemetry trace)
            {
                trace.Message = Redact(trace.Message);
            }
            else if (telemetry is ExceptionTelemetry exception)
            {
                exception.Message = Redact(exception.Message);
            }
        }

        private static string Redact(string? message)
        {
            if (string.IsNullOrEmpty(message) || message.IndexOf('@') < 0)
            {
                return message ?? string.Empty;
            }

            return EmailLike.Replace(message, Replacement);
        }
    }
}
