/// <summary>
/// UserUpdate
/// </summary>

namespace SafeExchange.Core.Utilities
{
    using Microsoft.EntityFrameworkCore;
    using SafeExchange.Core.Model;
    using System;
    using System.Threading.Tasks;

    public static class UserUpdate
    {
        public const int MaxAttempts = 4;

        /// <summary>
        /// Applies <paramref name="apply"/> to the tracked <paramref name="user"/> and saves it. The user document is
        /// updated concurrently (e.g. the user's own request rotating its telemetry id under the etag concurrency
        /// token), so on a conflict the latest document is reloaded and <paramref name="apply"/> runs again, for up to
        /// <see cref="MaxAttempts"/> saves; the last conflict propagates. <paramref name="apply"/> returns <c>false</c>
        /// to give up without saving (it must leave the user unchanged then), which this method returns.
        /// </summary>
        public static async Task<bool> SaveWithRetryAsync(SafeExchangeDbContext dbContext, User user, Func<User, bool> apply)
        {
            ArgumentNullException.ThrowIfNull(dbContext);
            ArgumentNullException.ThrowIfNull(user);
            ArgumentNullException.ThrowIfNull(apply);

            for (var attempt = 1; ; attempt++)
            {
                if (!apply(user))
                {
                    return false;
                }

                try
                {
                    await dbContext.SaveChangesAsync();
                    return true;
                }
                catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
                {
                    await dbContext.Entry(user).ReloadAsync();
                }
            }
        }
    }
}
