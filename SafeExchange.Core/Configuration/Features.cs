/// <summary>
/// Features
/// </summary>

namespace SafeExchange.Core.Configuration
{
    using System;

    public class Features
    {
        public bool UseNotifications { get; set; }

        public bool UseExternalWebHookNotifications { get; set; }

        public bool UseGroupsAuthorization { get; set; }

        public Features Clone()
        {
            return new Features()
            {
                UseNotifications = this.UseNotifications,
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
                this.UseNotifications.Equals(other.UseNotifications) &&
                this.UseExternalWebHookNotifications.Equals(other.UseExternalWebHookNotifications) &&
                this.UseGroupsAuthorization.Equals(other.UseGroupsAuthorization);
        }

        public override int GetHashCode() => HashCode.Combine(
            this.UseNotifications,
            this.UseExternalWebHookNotifications,
            this.UseGroupsAuthorization);
    }
}
