/// <summary>
/// IGraphDataProvider
/// </summary>

namespace SafeExchange.Core.Graph
{
    using System;

    public interface IGraphDataProvider
    {
        /// <summary>
        /// Try to get 'memberOf' list of groups for a given user from Azure Graph (on behalf of the user).
        /// </summary>
        /// <param name="accountIdAndToken">Account Id and token for a user, to query Azure Graph 'on behalf of'.</param>
        /// <returns>A list of groups which user is a member of.</returns>
        public Task<GroupListResult> TryGetMemberOfAsync(AccountIdAndToken accountIdAndToken);

        /// <summary>
        /// Try to find list of users for a given user display name, email or upn.
        /// </summary>
        /// <param name="accountIdAndToken">Account Id and token for a user, to query Azure Graph 'on behalf of'.</param>
        /// <param name="searchString">Part of display name, email or upn.</param>
        /// <returns>A list of users that contain given search string in its display name, email or upn.</returns>
        public Task<UsersListResult> TryFindUsersAsync(AccountIdAndToken accountIdAndToken, string searchString);
    }
}
