/// <summary>
/// OrphanRuleResult
/// </summary>

namespace SafeExchange.Core.Permissions
{
    using System;

    public class OrphanRuleResult
    {
        public bool WasOrphaned { get; set; }

        public DateTime? ExpireAt { get; set; }
    }
}
