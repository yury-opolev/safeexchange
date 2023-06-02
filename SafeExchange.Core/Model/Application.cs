/// <summary>
/// Application
/// </summary>

namespace SafeExchange.Core.Model
{
    using System;
    using System.ComponentModel.DataAnnotations;
    using Microsoft.EntityFrameworkCore;

    [Index(nameof(AadTenantId), nameof(AadClientId), IsUnique = true)]
    [Index(nameof(DisplayName), IsUnique = true)]
    public class Application
	{
        public const string DefaultPartitionKey = "APPLICATION";

        public Application() { }

        public Application(string displayName, string aadClientId, string aadTenantId, string contactEmail)
        {
            this.Id = Guid.NewGuid().ToString();
            this.PartitionKey = Application.DefaultPartitionKey;
            this.Enabled = true;

            this.DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
            this.AadClientId = aadClientId ?? throw new ArgumentNullException(nameof(aadClientId));
            this.AadTenantId = aadTenantId ?? throw new ArgumentNullException(nameof(aadTenantId));

            this.ContactEmail = contactEmail ?? string.Empty;

            this.CreatedAt = DateTimeProvider.UtcNow;
            this.ModifiedAt = DateTime.MinValue;
        }

        public string Id { get; set; }

        public string PartitionKey { get; set; }

        public bool Enabled { get; set; }

        [Required]
        [StringLength(100, ErrorMessage = "Value too long (100 character limit).")]
        [RegularExpression(@"^[a-zA-Z0-9-]+( [a-zA-Z0-9-]+)*$", ErrorMessage = "Only letters, numbers, hyphens and spaces are allowed, starting with non-space symbol.")]
        public string DisplayName { get; set; }

        [Required]
        public string ContactEmail { get; set; }

        [Required]
        public string AadClientId { get; set; }

        [Required]
        public string AadTenantId { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime ModifiedAt { get; set; }
    }
}

