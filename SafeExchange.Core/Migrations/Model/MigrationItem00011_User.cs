/// <summary>
/// MigrationItem00011_User
/// </summary>

namespace SafeExchange.Core.Migrations
{
    /// <summary>
    /// Marker DTO used by the telemetry-id backfill migration.
    /// Only the fields needed to identify the document and check/set
    /// the telemetry id are projected from the Users container.
    /// </summary>
    public class MigrationItem00011_User
    {
        public MigrationItem00011_User()
        { }

        public string PartitionKey { get; set; }

        public string id { get; set; }

        public string TelemetryId { get; set; }
    }
}
