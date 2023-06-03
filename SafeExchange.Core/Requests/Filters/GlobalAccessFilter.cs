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

    public class GlobalAccessFilter : IRequestFilter
    {
        private IList<string> accessGroupIds;

        private readonly ITokenHelper tokenHelper;

        private readonly ILogger log;

        public GlobalAccessFilter(GloballyAllowedGroupsConfiguration groupsConfiguration, ITokenHelper tokenHelper, ILogger log)
        {
            if (groupsConfiguration is null)
            {
                throw new ArgumentNullException(nameof(groupsConfiguration));
            }

            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.log = log ?? throw new ArgumentNullException(nameof(log));

            var allowedGroupsString = groupsConfiguration.AllowedGroups;
            this.accessGroupIds = new List<string>();
            if (!string.IsNullOrEmpty(allowedGroupsString))
            {
                var groupArray = allowedGroupsString.Split(",", StringSplitOptions.RemoveEmptyEntries);
                foreach (var group in groupArray)
                {
                    this.accessGroupIds.Add(group);
                }
            }
        }

        public async ValueTask<(bool shouldReturn, HttpResponseData? response)> GetFilterResultAsync(HttpRequestData req, ClaimsPrincipal principal, SafeExchangeDbContext dbContext)
        {
            (bool shouldReturn, HttpResponseData? response) result = (shouldReturn: false, response: null);
            if (!accessGroupIds.Any())
            {
                return result;
            }

            var userUpn = this.tokenHelper.GetUpn(principal);
            var existingUser = await dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals(userUpn));
            if (existingUser is null)
            {
                result.shouldReturn = true;
                result.response = req.CreateResponse(HttpStatusCode.Unauthorized);
                await result.response.WriteAsJsonAsync(new BaseResponseObject<object> { Status = "unauthorized", Error = $"Not a member of a global group." });
                return result;
            }

            foreach (var groupId in accessGroupIds)
            {
                if (existingUser.Groups.Any(g => g.AadGroupId.Equals(groupId)))
                {
                    this.log.LogInformation($"{existingUser.AadUpn} is a member of global access group '{groupId}', authorized.");
                    return result;
                }
            }

            this.log.LogInformation($"{existingUser.AadUpn} is not a member of any global access group, unauthorized.");
            result.shouldReturn = true;
            result.response = req.CreateResponse(HttpStatusCode.Unauthorized);
            await result.response.WriteAsJsonAsync(new BaseResponseObject<object> { Status = "unauthorized", Error = $"Not a member of a global group." });
            return result;
        }
    }
}
