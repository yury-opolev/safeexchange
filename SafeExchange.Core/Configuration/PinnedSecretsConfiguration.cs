/// <summary>
/// PinnedSecretsConfiguration
/// </summary>

namespace SafeExchange.Core.Configuration
{
    public class PinnedSecretsConfiguration
    {
        public int MaxPinnedSecretsPerUser { get; set; } = 5;

        public PinnedSecretsConfiguration Clone() => new()
        {
            MaxPinnedSecretsPerUser = this.MaxPinnedSecretsPerUser
        };

        public override bool Equals(object obj)
        {
            if (obj is not PinnedSecretsConfiguration other)
            {
                return false;
            }

            return this.MaxPinnedSecretsPerUser.Equals(other.MaxPinnedSecretsPerUser);
        }

        public override int GetHashCode() => this.MaxPinnedSecretsPerUser.GetHashCode();
    }
}
