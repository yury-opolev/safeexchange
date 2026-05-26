/// <summary>
/// Limits — configurable upper bounds and defaults for the admin surface.
/// All values flow from Key Vault via the `Limits--*` secret naming convention
/// (mapped to `Limits:*` by AddAzureKeyVault). Defaults are safe; prod parameter
/// files override.
/// </summary>

namespace SafeExchange.Core.Configuration
{
    using System;

    public class Limits
    {
        /// <summary>
        /// Default `pageSize` used by admin paginated lists when the caller
        /// doesn't pass one. Kept small enough to render comfortably on a
        /// phone-sized admin panel.
        /// </summary>
        public int AdminListDefaultPageSize { get; set; } = 25;

        /// <summary>
        /// Hard cap on `pageSize` for admin lists. Requests above this are
        /// clamped down rather than rejected — pagination is for UX, not for
        /// rate-limiting.
        /// </summary>
        public int AdminListMaxPageSize { get; set; } = 100;

        /// <summary>
        /// Advisory grace period for migrated apps that don't yet meet the
        /// ≥2-owners-with-a-user invariant. Surfaced in the admin panel and the
        /// owner's self-service page; no auto-disable behaviour in the spike.
        /// </summary>
        public int OwnerlessGracePeriodDays { get; set; } = 30;

        public Limits Clone() => new Limits()
        {
            AdminListDefaultPageSize = this.AdminListDefaultPageSize,
            AdminListMaxPageSize = this.AdminListMaxPageSize,
            OwnerlessGracePeriodDays = this.OwnerlessGracePeriodDays,
        };

        public override bool Equals(object obj)
        {
            if (obj is not Limits other)
            {
                return false;
            }

            return
                this.AdminListDefaultPageSize.Equals(other.AdminListDefaultPageSize) &&
                this.AdminListMaxPageSize.Equals(other.AdminListMaxPageSize) &&
                this.OwnerlessGracePeriodDays.Equals(other.OwnerlessGracePeriodDays);
        }

        public override int GetHashCode() => HashCode.Combine(
            this.AdminListDefaultPageSize,
            this.AdminListMaxPageSize,
            this.OwnerlessGracePeriodDays);
    }
}
