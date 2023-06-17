/// <summary>
/// MigrationItem00001
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using System;

    public class MigrationItem00001
    {
        public MigrationItem00001()
        { }

        public MigrationItem00001(MigrationItem00001 source)
        {
            this.SecretName = source.SecretName;
            this.SubjectType = source.SubjectType;
            this.SubjectName = source.SubjectName;
            this.CanGrantAccess = source.CanGrantAccess;
            this.CanRead = source.CanRead;
            this.CanRevokeAccess = source.CanRevokeAccess;
            this.CanWrite = source.CanWrite;
            this.PartitionKey = source.PartitionKey;
            this.id = source.id;
        }

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
