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

        private readonly IOptionsMonitor<GloballyAllowedGroupsConfiguration> groupsConfiguration;

        private readonly ITokenHelper tokenHelper;

        private readonly SafeExchangeDbContext dbContext;

        private readonly ILogger<CallerResolver> log;

        public CallerResolver(IOptionsMonitor<GloballyAllowedGroupsConfiguration> groupsConfiguration, ITokenHelper tokenHelper, SafeExchangeDbContext dbContext, ILogger<CallerResolver> log)
        {
            this.groupsConfiguration = groupsConfiguration ?? throw new ArgumentNullException(nameof(groupsConfiguration));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public async Task<CallerResolution> ResolveAsync(ClaimsPrincipal principal, string channel, string? clientLabel, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(principal);
            ArgumentException.ThrowIfNullOrEmpty(channel);

            var policy = new GlobalAccessPolicy(this.groupsConfiguration.CurrentValue, this.tokenHelper, this.log);
            var denial = await policy.EvaluateAsync(principal, this.dbContext, cancellationToken);
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
    }
}
