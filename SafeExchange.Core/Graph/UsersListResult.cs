
namespace SafeExchange.Core.Graph
{
    using System.Collections.Generic;

    public class UsersListResult
    {
        public bool Success { get; set; }

        public bool ConsentRequired { get; set; }

        public string ScopesToConsent { get; set; }

        public IList<GraphUserInfo> Users { get; set; } = Array.Empty<GraphUserInfo>();
    }
}
