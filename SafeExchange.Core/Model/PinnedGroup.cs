/// <summary>
/// PinnedGroup
/// </summary>

namespace SafeExchange.Core.Model
{
    using Microsoft.EntityFrameworkCore;
    using SafeExchange.Core.Model.Dto.Input;
    using System;

    [Index(nameof(UserId), nameof(GroupItemId))]
    public class PinnedGroup
    {
        public const string DefaultPartitionKey = "PGRP";

        public PinnedGroup() { }

        public PinnedGroup(string userId, PinnedGroupInput input)
        {
            this.PartitionKey = PinnedGroup.DefaultPartitionKey;

            this.UserId = userId ?? throw new ArgumentNullException(nameof(userId));
            this.GroupItemId = input.GroupId ?? throw new ArgumentNullException(nameof(input.GroupId));

            this.CreatedAt = DateTimeProvider.UtcNow;
        }

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
