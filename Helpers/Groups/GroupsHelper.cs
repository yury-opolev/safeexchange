/// SafeExchange

namespace SpaceOyster.SafeExchange
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using Microsoft.Graph;

    public static class GroupsHelper
    {
        public static async Task<IList<string>> TryGetCurrentUserGroupsAsync(GraphServiceClient graphClient, ILogger log)
        {
            var totalGroups = new List<string>();

            try
            {
                var groups = await graphClient.Me.MemberOf.Request().GetAsync();
                while (groups.Count > 0)
                {
                    foreach (Group g in groups)
                    {
                        totalGroups.Add(g.DisplayName);
                    }
                    if (groups.NextPageRequest != null)
                    {
                        groups = await groups.NextPageRequest.GetAsync();
                    }
                    else
                    {
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                log.LogWarning($"Cannot retrieve user groups, {exception.GetType()}: {exception.Message}");
            }

            return totalGroups;
        }
    }
}