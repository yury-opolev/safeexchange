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
                var memberOf = await graphClient.Me.MemberOf
                    .Request()
                    .Select("id,mail,mailNickname")
                    .GetAsync();
                while (memberOf.Count > 0)
                {
                    foreach (Group group in memberOf)
                    {
                        if (!string.IsNullOrEmpty(group.Mail))
                        {
                            totalGroups.Add(group.Mail);
                        }
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

        public static async Task<IList<string>> TryGetMemberObjectIdsAsync(GraphServiceClient graphClient, ILogger log)
        {
            var totalGroups = new List<string>();

            try
            {
                var securityEnabledOnly = false;
                var groupIds = await graphClient.Me
                    .GetMemberObjects(securityEnabledOnly)
                    .Request()
                    .PostAsync();
                while (groupIds.Count > 0)
                {
                    foreach (string groupId in groupIds)
                    {
                        totalGroups.Add(groupId);
                    }
                    if (groupIds.NextPageRequest != null)
                    {
                        groupIds = await groupIds.NextPageRequest.PostAsync();
                    }
                    else
                    {
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                log.LogWarning($"Cannot retrieve user group Ids, {exception.GetType()}: {exception.Message}");
            }

            return totalGroups;
        }
    }
}