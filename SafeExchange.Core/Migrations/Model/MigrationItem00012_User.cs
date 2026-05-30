/// <summary>
/// MigrationItem00012_User
/// </summary>

namespace SafeExchange.Core.Migrations
{
    public class MigrationItem00012_User
    {
        public MigrationItem00012_User() { }

        public string PartitionKey { get; set; }

        public string id { get; set; }

        public string TelemetryId { get; set; }

        public string TelemetryIdIssuedAt { get; set; }
    }
}
