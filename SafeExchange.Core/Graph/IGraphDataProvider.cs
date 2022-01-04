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
        public Task<IList<string>> TryGetMemberOfAsync(AccountIdAndToken accountIdAndToken);
    }
}
