/// <summary>
/// WebClientTelemetryConfiguration
/// </summary>

namespace SafeExchange.Core.Configuration
{
    /// <summary>
    /// Application Insights configuration the backend hands to authenticated
    /// browser clients via GET /v2/telemetry/config. Populated from the
    /// WebClientTelemetry--* secrets in Key Vault.
    ///
    /// The connection string sits behind an authenticated endpoint so it is
    /// not reachable from the public wwwroot/appsettings.json bundle — an
    /// attacker has to hold a valid tenant JWT before they can extract the
    /// ingestion credential.
    /// </summary>
    public class WebClientTelemetryConfiguration
    {
        public string ConnectionString { get; set; } = string.Empty;
    }
}
