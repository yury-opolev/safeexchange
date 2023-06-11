/// <summary>
/// SafeExchange
/// </summary>

namespace SafeExchange.Core.Filters
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Configuration;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class AdminGroupFilter : IRequestFilter
    {
        private IList<string> adminGroupIds;

        private IList<string> adminUserIds;

        private readonly ITokenHelper tokenHelper;

        private readonly ILogger log;

        public AdminGroupFilter(AdminConfiguration adminConfiguration, ITokenHelper tokenHelper, ILogger log)
        {
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.log = log ?? throw new ArgumentNullException(nameof(log));

            this.adminGroupIds = new List<string>();
            if (!string.IsNullOrEmpty(adminConfiguration?.AdminGroups))
            {
                var groupArray = adminConfiguration.AdminGroups.Split(",", StringSplitOptions.RemoveEmptyEntries);
                foreach (var group in groupArray)
                {
                    this.adminGroupIds.Add(group);
                }
            }

            this.adminUserIds = new List<string>();
            if (!string.IsNullOrEmpty(adminConfiguration?.AdminUsers))
            {
                var groupArray = adminConfiguration.AdminUsers.Split(",", StringSplitOptions.RemoveEmptyEntries);
                foreach (var group in groupArray)
                {
                    this.adminUserIds.Add(group);
                }
            }
        }

        public async ValueTask<(bool shouldReturn, HttpResponseData? response)> GetFilterResultAsync(HttpRequestData req, ClaimsPrincipal principal, SafeExchangeDbContext dbContext)
        {
            (bool shouldReturn, HttpResponseData? response) result = (shouldReturn: false, response: null);

            if (!this.adminGroupIds.Any() && !this.adminUserIds.Any())
            {
                this.log.LogInformation($"No admin groups or users configured, unauthorized.");
                result.shouldReturn = true;
                result.response = await ActionResults.CreateResponseAsync(
                    req, HttpStatusCode.Forbidden,
                    new BaseResponseObject<object> { Status = "forbidden", Error = $"Not an admin or a member of an admin group." });
                return result;
            }

            var userUpn = this.tokenHelper.GetUpn(principal);
            var userId = this.tokenHelper.GetObjectId(principal);
            if (adminUserIds.Contains(userId, StringComparer.OrdinalIgnoreCase))
            {
                this.log.LogInformation($"{userUpn} is configured as an admin '{userId}', authorized.");
                return result;
            }

            var existingUser = await dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals(userUpn));
            if (existingUser is null)
            {
                result.shouldReturn = true;
                result.response = await ActionResults.CreateResponseAsync(
                    req, HttpStatusCode.Forbidden,
                    new BaseResponseObject<object> { Status = "forbidden", Error = $"Not an admin or a member of an admin group." });
                return result;
            }

            foreach (var groupId in adminGroupIds)
            {
                var foundGroup = existingUser.Groups.FirstOrDefault(g => g.AadGroupId.Equals(groupId));
                if (foundGroup != default)
                {
                    this.log.LogInformation($"{userUpn} is a member of an admin group '{groupId}', authorized.");
                    return result;
                }
            }

            this.log.LogInformation($"{userUpn} is not an admin or a member of any admin group, unauthorized.");
            result.shouldReturn = true;
            result.response = await ActionResults.CreateResponseAsync(
                req, HttpStatusCode.Forbidden,
                new BaseResponseObject<object> { Status = "forbidden", Error = $"Not an admin or a member of an admin group." });
            return result;
        }
    }
}
