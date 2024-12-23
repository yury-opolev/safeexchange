/// <summary>
/// PinnedGroupOutput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Input
{
    using System;

    public class PinnedGroupOutput
    {
        public string GroupId { get; set; }

        public string GroupDisplayName { get; set; }

        public string? GroupMail { get; set; }
    }
}
