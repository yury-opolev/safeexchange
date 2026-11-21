/// <summary>
/// CallerResolver
/// </summary>

namespace SafeExchange.Core.Callers
{
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Model;
    using System;
    using System.Security.Claims;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Default <see cref="ICallerResolver"/>: the REST handlers' preamble, in the same order. First the global
    /// access-group gate, then <see cref="SubjectHelper.GetSubjectInfoAsync"/>, then the registered-application
    /// gate (only enabled registrations resolve, which makes disabling an application its kill switch).
    /// </summary>
    public sealed class CallerResolver : ICallerResolver
    {
        public const string ApplicationNotRegisteredError = "Application is not registered or disabled.";

        private readonly Func<GlobalAccessPolicy> globalAccessPolicy;

        private readonly ITokenHelper tokenHelper;

        private readonly SafeExchangeDbContext dbContext;

        public CallerResolver(IOptionsMonitor<GloballyAllowedGroupsConfiguration> groupsConfiguration, ITokenHelper tokenHelper, SafeExchangeDbContext dbContext, ILogger<CallerResolver> log)
            : this(PolicyFactory(groupsConfiguration, tokenHelper, log), tokenHelper, dbContext)
        {
        }

        /// <summary>
        /// For hosts that already own the global access policy (its configuration and log), such as the handlers'
        /// pre-service constructors.
        /// </summary>
        internal CallerResolver(Func<GlobalAccessPolicy> globalAccessPolicy, ITokenHelper tokenHelper, SafeExchangeDbContext dbContext)
        {
            this.globalAccessPolicy = globalAccessPolicy ?? throw new ArgumentNullException(nameof(globalAccessPolicy));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public async Task<CallerResolution> ResolveAsync(ClaimsPrincipal principal, string channel, string? clientLabel, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(principal);
            ArgumentException.ThrowIfNullOrEmpty(channel);

            var denial = await this.globalAccessPolicy().EvaluateAsync(principal, this.dbContext, cancellationToken);
            if (denial is not null)
            {
                return CallerResolution.Denied(denial);
            }

            (SubjectType subjectType, string subjectId) = await SubjectHelper.GetSubjectInfoAsync(this.tokenHelper, principal, this.dbContext);
            if (SubjectType.Application.Equals(subjectType) && string.IsNullOrEmpty(subjectId))
            {
                return CallerResolution.Denied(CallerDenial.Forbidden(ApplicationNotRegisteredError));
            }

            var displayName = SubjectType.User.Equals(subjectType) ? this.tokenHelper.GetDisplayName(principal) : subjectId;
            if (string.IsNullOrEmpty(displayName))
            {
                displayName = subjectId;
            }

            return CallerResolution.Ok(new Caller(subjectType, subjectId, displayName, Array.Empty<string>(), channel, clientLabel));
        }

        private static Func<GlobalAccessPolicy> PolicyFactory(IOptionsMonitor<GloballyAllowedGroupsConfiguration> groupsConfiguration, ITokenHelper tokenHelper, ILogger<CallerResolver> log)
        {
            ArgumentNullException.ThrowIfNull(groupsConfiguration);
            ArgumentNullException.ThrowIfNull(log);

            return () => new GlobalAccessPolicy(groupsConfiguration.CurrentValue, tokenHelper, log);
        }
    }
}
