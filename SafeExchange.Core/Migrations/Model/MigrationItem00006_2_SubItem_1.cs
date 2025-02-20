/// <summary>
/// MigrationItem00006_2
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using SafeExchange.Core.Model;
    using System;

    public class MigrationItem00006_2_SubItem_1
    {
        public MigrationItem00006_2_SubItem_1()
        { }

        public MigrationItem00006_2_SubItem_1(MigrationItem00006_2_SubItem_1 source)
        {
            this.AccessRequestId = source.AccessRequestId;
            this.SubjectType = source.SubjectType;
            this.SubjectName = source.SubjectName;
            this.SubjectId = source.SubjectId;
        }

        public string AccessRequestId { get; set; }

        public int SubjectType { get; set; }

        public string SubjectName { get; set; }

        public string SubjectId { get; set; }
    }
}
