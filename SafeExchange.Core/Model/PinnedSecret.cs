/// <summary>
/// PinnedSecret
/// </summary>

namespace SafeExchange.Core.Model
{
    using System;

    public class PinnedSecret
    {
        public const string DefaultPartitionKey = "PSEC";

        public PinnedSecret() { }

        public PinnedSecret(string userId, string secretName)
        {
            this.PartitionKey = PinnedSecret.DefaultPartitionKey;
            this.UserId = userId ?? throw new ArgumentNullException(nameof(userId));
            this.SecretName = secretName ?? throw new ArgumentNullException(nameof(secretName));
            this.CreatedAt = DateTimeProvider.UtcNow;
        }

        public string PartitionKey { get; set; }

        public string UserId { get; set; }

        public string SecretName { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
