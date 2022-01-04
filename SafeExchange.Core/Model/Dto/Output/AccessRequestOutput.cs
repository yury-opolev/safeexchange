/// <summary>
/// AccessRequestOutput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System;

    public class AccessRequestOutput
    {
        public string Id { get; set; }

        public string SubjectName { get; set; }

        public string ObjectName { get; set; }

        public bool CanRead { get; set; }

        public bool CanWrite { get; set; }

        public bool CanGrantAccess { get; set; }

        public bool CanRevokeAccess { get; set; }

        public DateTime RequestedAt { get; set; }
    }
}
