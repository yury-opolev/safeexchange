/// <summary>
/// SafeExchange
/// </summary>

namespace SafeExchange.Core.Filters
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Graph;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class GlobalAccessFilter : IRequestFilter
    {
        private IList<string> accessGroupIds;

        private readonly IConfiguration configuration;

        private readonly ITokenHelper tokenHelper;

        private readonly IGraphDataProvider graphDataProvider;

        private readonly ILogger log;

        public GlobalAccessFilter(IConfiguration configuration, ITokenHelper tokenHelper, IGraphDataProvider graphDataProvider, ILogger log)
        {
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.graphDataProvider = graphDataProvider ?? throw new ArgumentNullException(nameof(graphDataProvider));
            this.log = log ?? throw new ArgumentNullException(nameof(log));

            var globallyAllowed = new GloballyAllowedGroupsConfiguration();
            this.configuration.GetSection("GlobalAllowLists").Bind(globallyAllowed);

            var allowedGroupsString = globallyAllowed.AllowedGroups;
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

        public async ValueTask<(bool shouldReturn, IActionResult? actionResult)> GetFilterResultAsync(HttpRequest req, ClaimsPrincipal principal, SafeExchangeDbContext dbContext)
        {
            (bool shouldReturn, IActionResult? actionResult) result = (shouldReturn: false, actionResult: null);

            if (!accessGroupIds.Any())
            {
                return result;
            }

            var userName = this.tokenHelper.GetUpn(principal);
            var accountIdAndToken = this.tokenHelper.GetAccountIdAndToken(req, principal);
            var userGroups = await this.graphDataProvider.TryGetMemberOfAsync(accountIdAndToken);
            foreach (var groupId in accessGroupIds)
            {
                if (userGroups.Contains(groupId))
                {
                    
                    this.log.LogInformation($"{userName} is a member of global access group '{groupId}', authorized.");
                    return result;
                }
            }

            this.log.LogInformation($"{userName} is not a member of any global access group, unauthorized.");
            result.shouldReturn = true;
            result.actionResult = new ObjectResult(new { status = "unauthorized", error = $"Not a member of a global group." }) { StatusCode = StatusCodes.Status401Unauthorized };
            return result;
        }
    }
}
