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
        public static async Task<IList<string>> TryGetMemberOfAsync(GraphServiceClient graphClient, ILogger log)
        {
            var totalGroups = new List<string>();

            try
            {
                var memberOf = await graphClient.Me.MemberOf.Request().Select("id").GetAsync();
                while (memberOf.Count > 0)
                {
                    foreach (Group group in memberOf)
                    {
                        totalGroups.Add(group.Id);
                    }
                    if (memberOf.NextPageRequest != null)
                    {
                        memberOf = await memberOf.NextPageRequest.GetAsync();
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