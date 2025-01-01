
namespace SafeExchange.Core.Graph
{
    using System.Collections.Generic;

    public class GroupIdListResult
    {
        public bool Success { get; set; }

        public bool ConsentRequired { get; set; }

        public string ScopesToConsent { get; set; }

        public IList<string> GroupIds { get; set; } = Array.Empty<string>();
    }
}
