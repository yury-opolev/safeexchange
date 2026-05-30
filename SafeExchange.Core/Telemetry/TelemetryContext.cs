/// <summary>
/// TelemetryContext
/// </summary>

namespace SafeExchange.Core.Telemetry
{
    using System.Threading;

    /// <summary>Holds the current request's telemetry id for the duration of the
    /// async call chain (mirrors SessionCorrelationMiddleware.Current).</summary>
    public static class TelemetryContext
    {
        private static readonly AsyncLocal<string?> current = new();

        public static string? Current
        {
            get => current.Value;
            set => current.Value = value;
        }
    }
}
