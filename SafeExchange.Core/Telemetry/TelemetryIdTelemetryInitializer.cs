/// <summary>
/// TelemetryIdTelemetryInitializer
/// </summary>

namespace SafeExchange.Core.Telemetry
{
    using Microsoft.ApplicationInsights.Channel;
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.ApplicationInsights.Extensibility;

    /// <summary>Stamps saex.telemetryId on every telemetry item from TelemetryContext.</summary>
    public class TelemetryIdTelemetryInitializer : ITelemetryInitializer
    {
        public const string PropertyName = "saex.telemetryId";

        public void Initialize(ITelemetry telemetry)
        {
            var telemetryId = TelemetryContext.Current;
            if (string.IsNullOrEmpty(telemetryId))
            {
                return;
            }

            if (telemetry is ISupportProperties supportsProperties
                && !supportsProperties.Properties.ContainsKey(PropertyName))
            {
                supportsProperties.Properties[PropertyName] = telemetryId;
            }
        }
    }
}
