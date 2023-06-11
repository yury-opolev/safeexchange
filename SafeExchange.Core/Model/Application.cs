/// <summary>
/// Application
/// </summary>

namespace SafeExchange.Core.Model
{
    using System;
    using System.ComponentModel.DataAnnotations;
    using Microsoft.EntityFrameworkCore;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;

    [Index(nameof(AadTenantId), nameof(AadClientId), IsUnique = true)]
    [Index(nameof(DisplayName), IsUnique = true)]
    public class Application
	{
        public const string DefaultPartitionKey = "APPLICATION";

        public Application() { }

        public Application(string displayName, ApplicationRegistrationInput input, string createdBy)
        {
            this.Id = Guid.NewGuid().ToString();
            this.PartitionKey = Application.DefaultPartitionKey;
            this.Enabled = true;

            this.DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
            this.AadClientId = input.AadClientId ?? throw new ArgumentNullException(nameof(input.AadClientId));
            this.AadTenantId = input.AadTenantId ?? throw new ArgumentNullException(nameof(input.AadTenantId));

            this.ContactEmail = input.ContactEmail ?? throw new ArgumentNullException(nameof(input.ContactEmail));

            this.CreatedAt = DateTimeProvider.UtcNow;
            this.CreatedBy = createdBy;
            this.ModifiedAt = DateTime.MinValue;
            this.ModifiedBy = string.Empty;
        }

        public string Id { get; set; }

        public string PartitionKey { get; set; }

        public bool Enabled { get; set; }

        [Required]
        [StringLength(150, ErrorMessage = "Value too long (150 character limit).")]
        [RegularExpression(@"^[a-zA-Z0-9-]+( [a-zA-Z0-9-]+)*$", ErrorMessage = "Only letters, numbers, hyphens and spaces are allowed, starting with non-space symbol.")]
        public string DisplayName { get; set; }

        [Required]
        public string ContactEmail { get; set; }

        [Required]
        public string AadClientId { get; set; }

        [Required]
        public string AadTenantId { get; set; }

        public DateTime CreatedAt { get; set; }

        public string CreatedBy { get; set; }

        public DateTime ModifiedAt { get; set; }

        public string ModifiedBy { get; set; }

        internal ApplicationRegistrationOutput ToDto() => new()
        {
            DisplayName = this.DisplayName,
            ContactEmail = this.ContactEmail,

            AadTenantId = this.AadTenantId,
            AadClientId = this.AadClientId,

            Enabled = this.Enabled
        };

        internal ApplicationRegistrationOverviewOutput ToOverviewDto() => new()
        {
            DisplayName = this.DisplayName,
            Enabled = this.Enabled
        };
    }
}

