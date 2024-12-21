/// <summary>
/// PinnedGroup
/// </summary>

namespace SafeExchange.Core.Model
{
    using Microsoft.EntityFrameworkCore;
    using System;

    [Index(nameof(UserId), nameof(GroupItemId))]
    public class PinnedGroup
    {
        public const string DefaultPartitionKey = "PGRP";

        public PinnedGroup() { }

        public PinnedGroup(string userId, string groupItemId)
        {
            this.PartitionKey = PinnedGroup.DefaultPartitionKey;

            this.UserId = userId ?? throw new ArgumentNullException(nameof(userId));
            this.GroupItemId = groupItemId ?? throw new ArgumentNullException(nameof(groupItemId));

            this.CreatedAt = DateTimeProvider.UtcNow;
        }

        public string PartitionKey { get; set; }

        public string UserId { get; set; }

        public string GroupItemId { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
