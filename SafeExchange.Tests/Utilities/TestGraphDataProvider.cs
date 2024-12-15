/// <summary>
/// TestGraphDataProvider
/// </summary>

namespace SafeExchange.Tests
{
    using SafeExchange.Core;
    using SafeExchange.Core.Graph;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    public class TestGraphDataProvider : IGraphDataProvider
    {
        public readonly Dictionary<string, IList<string>> GroupMemberships = new();

        public readonly Dictionary<string, IList<GraphUserInfo>> FoundUsers = new();

        public readonly Dictionary<string, IList<GraphGroupInfo>> FoundGroups = new();

        public async Task<GroupIdListResult> TryGetMemberOfAsync(AccountIdAndToken accountIdAndToken)
        {
            if (this.GroupMemberships.TryGetValue(accountIdAndToken.AccountId, out var result))
            {
                return await Task.FromResult(new GroupIdListResult() { Success = true, GroupIds = result ?? [] });
            }

            return await Task.FromResult(new GroupIdListResult() { Success = true, GroupIds = [] });
        }

        public async Task<UsersListResult> TryFindUsersAsync(AccountIdAndToken accountIdAndToken, string searchString)
        {
            if (this.FoundUsers.TryGetValue(accountIdAndToken.AccountId, out var result))
            {
                return await Task.FromResult(new UsersListResult() { Success = true, Users = result ?? [] });
            }

            return await Task.FromResult(new UsersListResult() { Success = true, Users = [] });
        }

        public async Task<GroupsListResult> TryFindGroupsAsync(AccountIdAndToken accountIdAndToken, string searchString)
        {
            if (this.FoundGroups.TryGetValue(accountIdAndToken.AccountId, out var result))
            {
                return await Task.FromResult(new GroupsListResult() { Success = true, Groups = result ?? [] });
            }

            return await Task.FromResult(new GroupsListResult() { Success = true, Groups = [] });
        }
    }
}
