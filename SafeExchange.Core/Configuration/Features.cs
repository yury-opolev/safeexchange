/// <summary>
/// Features
/// </summary>

namespace SafeExchange.Core.Configuration
{
    using System;

    public class Features
    {
        public bool UseExternalWebHookNotifications { get; set; }

        public bool UseGroupsAuthorization { get; set; }

        public Features Clone()
        {
            return new Features()
            {
                UseExternalWebHookNotifications = this.UseExternalWebHookNotifications,
                UseGroupsAuthorization = this.UseGroupsAuthorization
            };
        }

        public override bool Equals(object obj)
        {
            if (obj is not Features other)
            {
                return false;
            }

            return
                this.UseExternalWebHookNotifications.Equals(other.UseExternalWebHookNotifications) &&
                this.UseGroupsAuthorization.Equals(other.UseGroupsAuthorization);
        }

        public override int GetHashCode() => HashCode.Combine(
            this.UseExternalWebHookNotifications,
            this.UseGroupsAuthorization);
    }
}
