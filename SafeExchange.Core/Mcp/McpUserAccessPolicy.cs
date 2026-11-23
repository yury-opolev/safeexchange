/// <summary>
/// McpUserAccessPolicy
/// </summary>

namespace SafeExchange.Core.Mcp
{
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Telemetry;
    using SafeExchange.Core.Utilities;
    using System;
    using System.Security.Claims;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Per-user MCP opt-in. A user caller may use MCP only when they allowed it (<see cref="User.McpAllowed"/>)
    /// and no administrator blocked it (<see cref="User.McpBlockedByAdmin"/>, which wins); a user without a
    /// Users row has not allowed it. Applications are not subject to it. Evaluated on every MCP message.
    /// </summary>
    public sealed class McpUserAccessPolicy
    {
        public const string NotAllowedSubStatus = "mcp_not_allowed";

        public const string BlockedByAdminSubStatus = "mcp_blocked_by_admin";

        public const string NotAllowedError = "MCP access is not enabled for this account. Turn on 'Allow MCP' in SafeExchange settings.";

        public const string BlockedByAdminError = "MCP access is blocked for this account by an administrator.";

        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly ILogger log;

        public McpUserAccessPolicy(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, ILogger<McpUserAccessPolicy> log)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// Returns <c>null</c> when the caller may use MCP, otherwise the denial.
        /// </summary>
        public async Task<CallerDenial?> EvaluateAsync(ClaimsPrincipal principal, Caller caller, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(principal);
            ArgumentNullException.ThrowIfNull(caller);

            if (!SubjectType.User.Equals(caller.SubjectType))
            {
                return null;
            }

            var user = await UserLookup.FindByTokenAsync(this.dbContext, this.tokenHelper, principal, cancellationToken);
            if (user is { McpBlockedByAdmin: true })
            {
                return this.Deny(user, BlockedByAdminError, BlockedByAdminSubStatus);
            }

            if (user is not { McpAllowed: true })
            {
                return this.Deny(user, NotAllowedError, NotAllowedSubStatus);
            }

            return null;
        }

        private CallerDenial Deny(User? user, string error, string subStatus)
        {
            this.log.LogInformation("MCP refused for user (tid {TelemetryId}): {SubStatus}.", user?.TelemetryId ?? TelemetryContext.Current, subStatus);
            return CallerDenial.Forbidden(error, subStatus);
        }
    }
}
