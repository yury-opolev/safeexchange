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

        public bool UseGraphUserSearch { get; set; }

        public bool UseGraphGroupSearch { get; set; }

        public Features Clone() => new Features()
            {
                UseExternalWebHookNotifications = this.UseExternalWebHookNotifications,
                UseGroupsAuthorization = this.UseGroupsAuthorization,
                UseGraphUserSearch = this.UseGraphUserSearch,
                UseGraphGroupSearch = this.UseGraphGroupSearch
        };

        public override bool Equals(object obj)
        {
            if (obj is not Features other)
            {
                return false;
            }

            return
                this.UseExternalWebHookNotifications.Equals(other.UseExternalWebHookNotifications) &&
                this.UseGroupsAuthorization.Equals(other.UseGroupsAuthorization) &&
                this.UseGraphUserSearch.Equals(other.UseGraphUserSearch) &&
                this.UseGraphGroupSearch.Equals(other.UseGraphGroupSearch);
        }

        public override int GetHashCode() => HashCode.Combine(
            this.UseExternalWebHookNotifications, this.UseGroupsAuthorization,
            this.UseGraphUserSearch, this.UseGraphGroupSearch);
    }
}
