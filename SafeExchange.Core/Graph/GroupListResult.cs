
namespace SafeExchange.Core.Graph
{
    using System.Collections.Generic;

    public class GroupListResult
    {
        public bool Success { get; set; }

        public bool ConsentRequired { get; set; }

        public IList<string> Groups { get; set; } = Array.Empty<string>();
    }
}
