/// <summary>
/// OrphanRulePreview
/// </summary>

namespace SafeExchange.Core.Permissions
{
    using System;

    public class OrphanRulePreview
    {
        public bool WouldOrphan { get; set; }

        public DateTime? CurrentExpireAt { get; set; }

        public DateTime? ProspectiveExpireAt { get; set; }
    }
}
