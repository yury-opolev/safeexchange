/// <summary>
/// WebClientTelemetryConfigOutput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    /// <summary>
    /// Shape returned by GET /v2/telemetry/config. The browser uses this to
    /// decide whether to initialise the Application Insights JS SDK and which
    /// resource to point it at.
    ///
    /// Enabled is a derived flag — true if and only if ConnectionString is
    /// non-empty server-side. Clients should treat Enabled=false as "do not
    /// initialise anything"; ConnectionString is empty in that case.
    /// </summary>
    public class WebClientTelemetryConfigOutput
    {
        public bool Enabled { get; set; }

        public string ConnectionString { get; set; } = string.Empty;
    }
}
