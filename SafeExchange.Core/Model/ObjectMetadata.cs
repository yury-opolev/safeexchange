/// <summary>
/// ObjectMetadata
/// </summary>

namespace SafeExchange.Core.Model
{
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using System;
    using System.Collections.Generic;
    using System.ComponentModel.DataAnnotations;

    public class ObjectMetadata
    {
        public ObjectMetadata() { }

        public ObjectMetadata(string inputName, MetadataCreationInput input, string createdBy)
        {
            this.ObjectName = inputName ?? throw new ArgumentNullException(nameof(inputName));
            this.PartitionKey = this.GetPartitionKey();

            this.ExpirationMetadata = new ExpirationMetadata(input.ExpirationSettings);
            this.Content = CreateContent(true);

            this.KeepInStorage = true;
            this.CreatedBy = createdBy;
            this.CreatedAt = DateTimeProvider.UtcNow;
            this.ModifiedBy = string.Empty;
            this.ModifiedAt = DateTime.MinValue;

            this.LastAccessedAt = DateTimeProvider.UtcNow;
        }

        private static List<ContentMetadata> CreateContent(bool isStarter)
        {
            var utcNow = DateTimeProvider.UtcNow;
            var mainContent = new ContentMetadata()
            {
                ContentName = ContentMetadata.NewName(),

                IsMain = isStarter,
                ContentType = string.Empty,
                FileName = string.Empty,
                Status = ContentStatus.Blank,
                AccessTicket = string.Empty,
                AccessTicketSetAt = DateTime.MinValue,

                Chunks = new List<ChunkMetadata>()
            };

            return new List<ContentMetadata> { mainContent };
        }

        public string PartitionKey { get; set; }

        [Key]
        [Required]
        [StringLength(100, ErrorMessage = "Value too long (100 character limit).")]
        [RegularExpression(@"^[0-9a-zA-Z-]+$", ErrorMessage = "Only letters, numbers and hyphens are allowed.")]
        public string ObjectName { get; set; }

        public bool KeepInStorage { get; set; }

        public List<ContentMetadata> Content { get; set; }

        public ExpirationMetadata ExpirationMetadata { get; set; }

        public DateTime CreatedAt { get; set; }

        public string CreatedBy { get; set; }

        public DateTime ModifiedAt { get; set; }

        public string ModifiedBy { get; set; }

        public DateTime LastAccessedAt { get; set; }

        internal ObjectMetadataOutput ToDto() => new ()
        {
            ObjectName = this.ObjectName,
            Content = this.Content?.Select(x => x.ToDto()).ToList() ?? Array.Empty<ContentMetadataOutput>().ToList(),
            ExpirationSettings = this.ExpirationMetadata.ToDto()
        };

        private string GetPartitionKey()
        {
            var hashString = this.ObjectName.GetHashCode().ToString("0000");
            return hashString.Substring(hashString.Length - 4, 4);
        }
    }
}
