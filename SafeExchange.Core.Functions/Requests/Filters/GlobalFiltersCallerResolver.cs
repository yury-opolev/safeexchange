/// <summary>
/// GlobalFiltersCallerResolver
/// </summary>

namespace SafeExchange.Core.Filters
{
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Model;
    using System;
    using System.Security.Claims;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// The caller resolution behind the handlers' pre-service constructors: exactly the old preamble, i.e.
    /// the <see cref="GlobalFilters"/> global access-group gate (its configuration, token helper and log),
    /// then <see cref="SubjectHelper.GetSubjectInfoAsync"/> with the handler's token helper, then the
    /// registered-application gate. Hosts that use DI get <see cref="CallerResolver"/> instead.
    /// </summary>
    internal sealed class GlobalFiltersCallerResolver : ICallerResolver
    {
        private readonly GlobalFilters globalFilters;

        private readonly ITokenHelper tokenHelper;

        private readonly SafeExchangeDbContext dbContext;

        public GlobalFiltersCallerResolver(GlobalFilters globalFilters, ITokenHelper tokenHelper, SafeExchangeDbContext dbContext)
        {
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public async Task<CallerResolution> ResolveAsync(ClaimsPrincipal principal, string channel, string? clientLabel, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(principal);
            ArgumentException.ThrowIfNullOrEmpty(channel);

            var denial = await this.globalFilters.GetGlobalAccessDenialAsync(principal, this.dbContext);
            if (denial is not null)
            {
                return CallerResolution.Denied(denial);
            }

            (SubjectType subjectType, string subjectId) = await SubjectHelper.GetSubjectInfoAsync(this.tokenHelper, principal, this.dbContext);
            if (SubjectType.Application.Equals(subjectType) && string.IsNullOrEmpty(subjectId))
            {
                return CallerResolution.Denied(CallerDenial.Forbidden(CallerResolver.ApplicationNotRegisteredError));
            }

            // The old preamble never read the display name and REST does not use it, so no token lookup is added.
            return CallerResolution.Ok(new Caller(subjectType, subjectId, subjectId, Array.Empty<string>(), channel, clientLabel));
        }
    }
}
