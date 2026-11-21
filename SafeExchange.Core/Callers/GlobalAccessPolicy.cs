/// <summary>
/// GlobalAccessPolicy
/// </summary>

namespace SafeExchange.Core.Callers
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Configuration;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Claims;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Host-agnostic global access-group gate (<c>GlobalAllowLists:AllowedGroups</c>). When groups are
    /// configured, only a user that exists in Users and is a member of one of them may call the API.
    /// </summary>
    public sealed class GlobalAccessPolicy
    {
        public const string NotAMemberError = "Not a member of a global group.";

        private readonly IReadOnlyList<string> accessGroupIds;

        private readonly ITokenHelper tokenHelper;

        private readonly ILogger log;

        public GlobalAccessPolicy(GloballyAllowedGroupsConfiguration groupsConfiguration, ITokenHelper tokenHelper, ILogger log)
        {
            if (groupsConfiguration is null)
            {
                throw new ArgumentNullException(nameof(groupsConfiguration));
            }

            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.log = log ?? throw new ArgumentNullException(nameof(log));

            var allowedGroupsString = groupsConfiguration.AllowedGroups;
            this.accessGroupIds = string.IsNullOrEmpty(allowedGroupsString)
                ? Array.Empty<string>()
                : allowedGroupsString.Split(",", StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// Returns <c>null</c> when the caller passes the gate, otherwise the denial.
        /// </summary>
        public async Task<CallerDenial?> EvaluateAsync(ClaimsPrincipal principal, SafeExchangeDbContext dbContext, CancellationToken cancellationToken = default)
        {
            if (this.accessGroupIds.Count == 0)
            {
                return null;
            }

            var userUpn = this.tokenHelper.GetUpn(principal);
            var existingUser = await dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals(userUpn), cancellationToken);
            if (existingUser is null)
            {
                return CallerDenial.Forbidden(NotAMemberError);
            }

            foreach (var groupId in this.accessGroupIds)
            {
                if (existingUser.Groups.Any(g => g.AadGroupId.Equals(groupId)))
                {
                    this.log.LogInformation("User (tid {TelemetryId}) is a member of global access group '{GroupId}', authorized.", existingUser.TelemetryId, groupId);
                    return null;
                }
            }

            this.log.LogInformation("User (tid {TelemetryId}) is not a member of any global access group, unauthorized.", existingUser.TelemetryId);
            return CallerDenial.Forbidden(NotAMemberError);
        }
    }
}
