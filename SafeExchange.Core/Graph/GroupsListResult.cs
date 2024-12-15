
namespace SafeExchange.Core.Graph
{
    using System.Collections.Generic;

    public class GroupsListResult
    {
        public bool Success { get; set; }

        public bool ConsentRequired { get; set; }

        public string ScopesToConsent { get; set; }

        public IList<GraphGroupInfo> Groups { get; set; } = Array.Empty<GraphGroupInfo>();
    }
}
