/// <summary>
/// PinnedGroupInput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Input
{
    using System;

    public class PinnedGroupInput
    {
        public required string GroupId { get; set; }

        public required string GroupDisplayName { get; set; }

        public string? GroupMail { get; set; }
    }
}
