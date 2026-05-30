/// <summary>
/// TelemetryIdWindowOutput — a retired telemetry id and the window it was active.
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System;

    public class TelemetryIdWindowOutput
    {
        public string Id { get; set; } = string.Empty;

        public DateTime ValidFromUtc { get; set; }

        public DateTime ValidToUtc { get; set; }
    }
}
