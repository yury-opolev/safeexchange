/// <summary>
/// TelemetryIdMapEntry
/// </summary>

namespace SafeExchange.Core.Model
{
    using System;

    /// <summary>One retired telemetry id and the window it was active. The document id
    /// is the telemetry id itself; the partition key is the owning user's id so a
    /// future per-user erasure is a single-partition delete. Auto-purged by the
    /// TelemetryIdMap container's 28-day TTL.</summary>
    public class TelemetryIdMapEntry
    {
        public TelemetryIdMapEntry() { }

        /// <summary>The retired telemetry id (GUID "n") — also the Cosmos document id.</summary>
        public string id { get; set; } = string.Empty;

        /// <summary>Owning user's <see cref="User.Id"/>. Partition key.</summary>
        public string UserId { get; set; } = string.Empty;

        /// <summary>UTC instant the id became the user's current id.</summary>
        public DateTime ValidFromUtc { get; set; }

        /// <summary>UTC instant the id was retired (rotated out).</summary>
        public DateTime ValidToUtc { get; set; }
    }
}
