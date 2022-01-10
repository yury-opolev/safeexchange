/// <summary>
/// User
/// </summary>

namespace SafeExchange.Core.Model
{
    using Microsoft.EntityFrameworkCore;
    using System;

    [Index(nameof(AadTenantId), nameof(AadObjectId), IsUnique = true)]
    [Index(nameof(AadUpn), IsUnique = true)]
    public class User
    {
        public const string DefaultPartitionKey = "USER";

        public User() { }

        public User(string displayName, string aadObjectId, string aadTenantId, string aadUpn, string contactEmail)
        {
            this.Id = Guid.NewGuid().ToString();
            this.PartitionKey = User.DefaultPartitionKey;
            this.Enabled = true;

            this.DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
            this.AadObjectId = aadObjectId ?? throw new ArgumentNullException(nameof(aadObjectId));
            this.AadTenantId = aadTenantId ?? throw new ArgumentNullException(nameof(aadTenantId));
            this.AadUpn = aadUpn ?? throw new ArgumentNullException(nameof(aadUpn));

            this.ContactEmail = contactEmail ?? string.Empty;

            this.Groups = new List<UserGroup>();
            this.LastGroupSync = DateTime.MinValue;

            this.CreatedAt = DateTimeProvider.UtcNow;
            this.ModifiedAt = DateTime.MinValue;
        }

        public string Id { get; set; }

        public string PartitionKey { get; set; }

        public bool Enabled { get; set; }

        public string DisplayName { get; set; }

        public string ContactEmail { get; set; }

        public string AadUpn { get; set; }

        public string AadObjectId { get; set; }

        public string AadTenantId { get; set; }

        public List<UserGroup> Groups { get; set; }

        public DateTime LastGroupSync { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime ModifiedAt { get; set; }
    }
}
