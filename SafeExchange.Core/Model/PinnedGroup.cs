/// <summary>
/// PinnedGroup
/// </summary>

namespace SafeExchange.Core.Model
{
    using Microsoft.EntityFrameworkCore;
    using System;

    [Index(nameof(UserId), nameof(EntraTenantId), nameof(EntraObjectId), IsUnique = true)]
    public class PinnedGroup
    {
        public const string DefaultPartitionKey = "PGRP";

        public PinnedGroup() { }

        public PinnedGroup(string userId, string entraObjectId, string entraTenantId, string displayName, string mail)
        {
            this.Id = Guid.NewGuid().ToString();
            this.PartitionKey = PinnedGroup.DefaultPartitionKey;

            this.DisplayName = userId ?? throw new ArgumentNullException(nameof(userId));
            this.EntraObjectId = entraObjectId ?? throw new ArgumentNullException(nameof(entraObjectId));
            this.EntraTenantId = entraTenantId ?? throw new ArgumentNullException(nameof(entraTenantId));

            this.DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
            this.Mail = mail ?? string.Empty;

            this.CreatedAt = DateTimeProvider.UtcNow;
        }

        public string Id { get; set; }

        public string PartitionKey { get; set; }

        public string UserId { get; set; }

        public string EntraTenantId { get; set; }

        public string EntraObjectId { get; set; }

        public string DisplayName { get; set; }

        public string Mail { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
