/// <summary>
/// RequestRecipient
/// </summary>

namespace SafeExchange.Core.Model
{
    using System;

    public class RequestRecipient
    {
        public string AccessRequestId { get; set; }

        public SubjectType SubjectType { get; set; }

        public string SubjectId { get; set; }
    }
}
