/// <summary>
/// GlobalFiltersCallerResolver
/// </summary>

namespace SafeExchange.Core.Filters
{
    using SafeExchange.Core.Callers;
    using System;
    using System.Security.Claims;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// The caller resolution behind the handlers' pre-service constructors: the shared <see cref="CallerResolver"/>
    /// logic with the global access-group gate taken from <see cref="GlobalFilters"/> (its configuration, token
    /// helper and log category) and the handler's token helper for the subject. Hosts that use DI get a plain
    /// <see cref="CallerResolver"/> instead.
    /// </summary>
    internal sealed class GlobalFiltersCallerResolver : ICallerResolver
    {
        private readonly CallerResolver inner;

        public GlobalFiltersCallerResolver(GlobalFilters globalFilters, ITokenHelper tokenHelper, SafeExchangeDbContext dbContext)
        {
            ArgumentNullException.ThrowIfNull(globalFilters);

            this.inner = new CallerResolver(globalFilters.CreateGlobalAccessPolicy, tokenHelper, dbContext);
        }

        public Task<CallerResolution> ResolveAsync(ClaimsPrincipal principal, string channel, string? clientLabel, CancellationToken cancellationToken = default)
            => this.inner.ResolveAsync(principal, channel, clientLabel, cancellationToken);
    }
}
