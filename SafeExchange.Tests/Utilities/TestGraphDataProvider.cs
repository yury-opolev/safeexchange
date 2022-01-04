/// <summary>
/// TestGraphDataProvider
/// </summary>

namespace SafeExchange.Tests
{
    using SafeExchange.Core;
    using SafeExchange.Core.Graph;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;

    public class TestGraphDataProvider : IGraphDataProvider
    {
        public readonly Dictionary<string, IList<string>> GroupMemberships = new();

        public async Task<IList<string>> TryGetMemberOfAsync(AccountIdAndToken accountIdAndToken)
        {
            if (this.GroupMemberships.TryGetValue(accountIdAndToken.AccountId, out var result))
            {
                return result ?? Array.Empty<string>().ToList();
            }

            return await Task.FromResult(Array.Empty<string>().ToList());
        }
    }
}
