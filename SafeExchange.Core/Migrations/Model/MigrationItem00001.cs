/// <summary>
/// MigrationItem00001
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using System;

    public class MigrationItem00001
    {
        public string SecretName { get; set; }

        public int SubjectType { get; set; }

        public string SubjectName { get; set; }

        public bool CanGrantAccess { get; set; }

        public bool CanRead { get; set; }

        public bool CanRevokeAccess { get; set; }

        public bool CanWrite { get; set; }

        public string PartitionKey { get; set; }

        public string id { get; set; }
    }
}
