/// <summary>
/// OrphanedSecretConfiguration
/// </summary>

namespace SafeExchange.Core.Configuration
{
    using System;

    public class OrphanedSecretConfiguration
    {
        public OrphanOwnershipMode Ownership { get; set; } = OrphanOwnershipMode.UserOrApp;

        public TimeSpan GracePeriod { get; set; } = TimeSpan.FromDays(7);

        public OrphanedSecretConfiguration Clone() => new()
        {
            Ownership = this.Ownership,
            GracePeriod = this.GracePeriod
        };

        public override bool Equals(object obj)
        {
            if (obj is not OrphanedSecretConfiguration other)
            {
                return false;
            }

            return this.Ownership.Equals(other.Ownership) && this.GracePeriod.Equals(other.GracePeriod);
        }

        public override int GetHashCode() => HashCode.Combine(this.Ownership, this.GracePeriod);
    }
}
