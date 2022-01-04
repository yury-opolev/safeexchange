/// <summary>
/// AccessRequestUpdateInput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Input
{
    using System;

    public class AccessRequestUpdateInput
    {
        public string RequestId { get; set; }

        public bool Approve { get; set; }
    }
}
