/// <summary>
/// MigrationItem00004_2
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using System;

    public class MigrationItem00004_2
    {
        public MigrationItem00004_2()
        { }

        public MigrationItem00004_2(MigrationItem00004_2 source)
        {
            this.PartitionKey = source.PartitionKey;
            this.id = source.id;

            this.SecretName = source.SecretName;
            this.SubjectType = source.SubjectType;
            this.SubjectName = source.SubjectName;

            this.CanGrantAccess = source.CanGrantAccess;
            this.CanRead = source.CanRead;
            this.CanRevokeAccess = source.CanRevokeAccess;
            this.CanWrite = source.CanWrite;
        }

        public string PartitionKey { get; set; }

        public string id { get; set; }

        public string SecretName { get; set; }

        public int SubjectType { get; set; }

        public string SubjectName { get; set; }

        public bool CanGrantAccess { get; set; }

        public bool CanRead { get; set; }

        public bool CanRevokeAccess { get; set; }

        public bool CanWrite { get; set; }
    }
}
