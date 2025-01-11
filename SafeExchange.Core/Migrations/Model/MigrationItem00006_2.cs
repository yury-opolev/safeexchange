/// <summary>
/// MigrationItem00006_2
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using System;

    public class MigrationItem00006_2
    {
        public MigrationItem00006_2()
        { }

        public MigrationItem00006_2(MigrationItem00006_2 source)
        {
            this.Id = source.Id;
            this.SubjectType = source.SubjectType;
            this.SubjectName = source.SubjectName;
            this.SubjectId = source.SubjectId;
            this.ObjectName = source.ObjectName;
            this.Permission = source.Permission;
            this.RequestedAt = source.RequestedAt;
            this.Status = source.Status;
            this.FinishedBy = source.FinishedBy;
            this.FinishedAt = source.FinishedAt;

            this.Recipients = new List<MigrationItem00006_2_SubItem_1>(source.Recipients?.Count ?? 0);
            foreach (var sourceSubItem in source.Recipients ?? Array.Empty<MigrationItem00006_2_SubItem_1>().ToList())
            {
                this.Recipients.Add(new MigrationItem00006_2_SubItem_1(sourceSubItem));
            }

            this.PartitionKey = source.PartitionKey;
            this.id = source.id;
        }

        public string PartitionKey { get; set; }

        public string id { get; set; }

        public string Id { get; set; }

        public int SubjectType { get; set; }

        public string SubjectName { get; set; }

        public string SubjectId { get; set; }

        public string ObjectName { get; set; }

        public int Permission { get; set; }

        public List<MigrationItem00006_2_SubItem_1> Recipients { get; set; }

        public DateTime RequestedAt { get; set; }

        public int Status { get; set; }

        public string FinishedBy { get; set; }

        public DateTime FinishedAt { get; set; }
    }
}
