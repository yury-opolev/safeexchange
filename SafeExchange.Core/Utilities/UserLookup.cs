/// <summary>
/// UserLookup
/// </summary>

namespace SafeExchange.Core.Utilities
{
    using Microsoft.EntityFrameworkCore;
    using SafeExchange.Core.Model;
    using System.Security.Claims;
    using System.Threading;
    using System.Threading.Tasks;

    public static class UserLookup
    {
        /// <summary>
        /// The Users row of a user token's principal, matched on tenant id and object id the way the
        /// token middleware finds or creates it; <c>null</c> when the token lacks them or no row exists.
        /// The entity is tracked, so callers can change and save it.
        /// </summary>
        public static async Task<User?> FindByTokenAsync(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, ClaimsPrincipal principal, CancellationToken cancellationToken = default)
        {
            var tenantId = tokenHelper.GetTenantId(principal);
            var objectId = tokenHelper.GetObjectId(principal);
            if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(objectId))
            {
                return null;
            }

            return await dbContext.Users.FirstOrDefaultAsync(u => u.AadTenantId == tenantId && u.AadObjectId == objectId, cancellationToken);
        }
    }
}
