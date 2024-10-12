﻿/// <summary>
/// GroupDictionaryItem
/// </summary>

namespace SafeExchange.Core.Model
{
    using SafeExchange.Core.Model.Dto.Output;
    using System;
    using System.ComponentModel.DataAnnotations;

    public class GroupDictionaryItem
    {
        public GroupDictionaryItem()
        { }

        public GroupDictionaryItem(string groupId, string displayName, string groupMail, string createdBy)
        {
            this.GroupId = groupId ?? throw new ArgumentNullException(nameof(groupId));
            this.PartitionKey = this.GetPartitionKey();

            this.DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
            this.GroupMail = groupMail ?? throw new ArgumentNullException(nameof(groupMail));

            this.CreatedAt = DateTimeProvider.UtcNow;
            this.CreatedBy = createdBy;
        }

        public string PartitionKey { get; set; }

        [Key]
        public string GroupId { get; set; }

        public string DisplayName { get; set; }

        public string GroupMail { get; set; }

        public DateTime CreatedAt { get; set; }

        public string CreatedBy { get; set; }

        private string GetPartitionKey()
        {
            var hashString = this.GroupId.GetHashCode().ToString("0000");
            return hashString.Substring(hashString.Length - 4, 4);
        }

        internal GroupOverviewOutput ToOverviewDto() => new()
        {
            DisplayName = this.DisplayName,
            GroupMail = this.GroupMail
        };
    }
}
