﻿/// <summary>
/// SafeExchange
/// </summary>

namespace SpaceOyster.SafeExchange.Core
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class GlobalAccessFilter : IRequestFilter
    {
        private bool isInitialized;

        private IList<string> accessGroupIds;

        private readonly IGraphClientProvider graphClientProvider;

        private readonly ConfigurationSettings configuration;

        private readonly string[] graphScopes = new string[] { "User.Read" };

        public GlobalAccessFilter(ConfigurationSettings configuration, IGraphClientProvider graphClientProvider)
        {
            this.graphClientProvider = graphClientProvider ?? throw new ArgumentNullException(nameof(graphClientProvider));
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }
         
        public async ValueTask<(bool shouldReturn, IActionResult actionResult)> GetFilterResultAsync(HttpRequest req, ClaimsPrincipal principal, ILogger log)
        {
            await this.InitializeAsync();

            (bool shouldReturn, IActionResult actionResult) result = (shouldReturn: false, actionResult: null);

            if (!accessGroupIds.Any())
            {
                return result;
            }

            var userName = TokenHelper.GetName(principal);
            var tokenResult = TokenHelper.GetTokenResult(req, principal, log);
            var graphClient = await this.graphClientProvider.GetGraphClientAsync(tokenResult, this.graphScopes, log);
            var userGroups = await GroupsHelper.TryGetMemberOfAsync(graphClient, log);
            foreach (var groupId in accessGroupIds)
            {
                if (userGroups.Contains(groupId))
                {
                    log.LogInformation($"{userName} is a member of global access group '{groupId}', authorized.");
                    return result;
                }
            }

            log.LogInformation($"{userName} is not a member of any global access group, unauthorized.");
            result.shouldReturn = true;
            result.actionResult = new ObjectResult(new { status = "unauthorized", error = $"Not a member of a global group." }) { StatusCode = StatusCodes.Status401Unauthorized };
            return result;
        }

        private async ValueTask InitializeAsync()
        {
            if (this.isInitialized)
            {
                return;
            }

            var configurationData = await this.configuration.GetDataAsync();
            this.accessGroupIds = new List<string>();
            if (!string.IsNullOrEmpty(configurationData.WhitelistedGroups))
            {
                var groupArray = configurationData.WhitelistedGroups.Split(",", StringSplitOptions.RemoveEmptyEntries);
                foreach (var group in groupArray)
                {
                    this.accessGroupIds.Add(group);
                }
            }

            this.isInitialized = true;
        }
    }
}
