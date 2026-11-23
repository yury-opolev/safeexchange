/// <summary>
/// ConcurrentUserWriteInterceptor
/// </summary>

namespace SafeExchange.Tests.Utilities
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.EntityFrameworkCore.Diagnostics;
    using SafeExchange.Core;
    using SafeExchange.Core.Model;
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Simulates a concurrent writer: right before the first save of the intercepted context, saves the given
    /// change to the same user through a separate context, so the intercepted save carries a stale etag and
    /// Cosmos answers 412 (a <see cref="DbUpdateConcurrencyException"/>).
    /// </summary>
    public sealed class ConcurrentUserWriteInterceptor : SaveChangesInterceptor
    {
        private readonly DbContextOptions<SafeExchangeDbContext> writerOptions;

        private readonly string userId;

        private readonly Action<User> concurrentChange;

        public ConcurrentUserWriteInterceptor(DbContextOptions<SafeExchangeDbContext> writerOptions, string userId, Action<User> concurrentChange)
        {
            this.writerOptions = writerOptions ?? throw new ArgumentNullException(nameof(writerOptions));
            this.userId = userId ?? throw new ArgumentNullException(nameof(userId));
            this.concurrentChange = concurrentChange ?? throw new ArgumentNullException(nameof(concurrentChange));
        }

        /// <summary>How many concurrent writes were made (at most one).</summary>
        public int Writes { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (this.Writes == 0)
            {
                this.Writes++;
                await using var writer = new SafeExchangeDbContext(this.writerOptions);
                var user = await writer.Users.SingleAsync(u => u.Id == this.userId, cancellationToken);
                this.concurrentChange(user);
                await writer.SaveChangesAsync(cancellationToken);
            }

            return result;
        }
    }
}
