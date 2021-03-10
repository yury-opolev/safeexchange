/// <summary>
/// SafeExchange 
/// </summary>

namespace SpaceOyster.SafeExchange.Core
{
    using System;

    public class AccessRequest
    {
        public string id { get; set; }

        public string PartitionKey { get; set; }

        /// <summary>
        /// User's name/id, who requests access
        /// </summary>
        public string SubjectName { get; set; }

        /// <summary>
        /// Secret name/id, which is the object to access
        /// </summary>
        public string ObjectName { get; set; }

        /// <summary>
        /// List of permissions, a comma-separated list
        /// </summary>
        public string Permissions { get; set; }

        public DateTime RequestedAt { get; set; }

        public RequestStatus Status { get; set; }

        public override string ToString()
        {
            return $"Id:{this.id}, Subject:{this.SubjectName}, Object:{this.ObjectName}, Permissions:'{this.Permissions}', RequestedAt:{this.RequestedAt}, Status:{this.Status}";
        }
    }
}
