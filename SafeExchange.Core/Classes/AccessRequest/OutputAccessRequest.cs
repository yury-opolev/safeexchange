/// <summary>
/// SafeExchange 
/// </summary>

namespace SpaceOyster.SafeExchange.Core
{
    using System;

    public class OutputAccessRequest
    {
        public OutputAccessRequest(AccessRequest source)
        {
            this.CopyFrom(source);
        }

        public string RequestId { get; set; }

        public string UserName { get; set; }

        public string SecretName { get; set; }

        public string Permissions { get; set; }

        public AccessRequestType RequestType { get; set; }

        public DateTime RequestedAt { get; set; }

        private void CopyFrom(AccessRequest source)
        {
            this.RequestId = source.id;
            this.UserName = source.SubjectName;
            this.SecretName = source.ObjectName;
            this.Permissions = source.Permissions;
            this.RequestedAt = source.RequestedAt;
        }
    }
}
