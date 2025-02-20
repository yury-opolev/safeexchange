/// <summary>
/// MigrationItem00006
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using SafeExchange.Core.Model;
    using System;

    public class MigrationItem00006_1
    {
        public MigrationItem00006_1()
        { }

        public MigrationItem00006_1(MigrationItem00006_1 source)
        {
            this.SecretName = source.SecretName;
            this.SubjectType = source.SubjectType;
            this.SubjectName = source.SubjectName;
            this.SubjectId = source.SubjectId;
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

        public string SubjectId { get; set; }

        public bool CanRead { get; set; }

        public bool CanWrite { get; set; }

        public bool CanGrantAccess { get; set; }

        public bool CanRevokeAccess { get; set; }

        public string PartitionKey { get; set; }

        public string id { get; set; }
    }
}
