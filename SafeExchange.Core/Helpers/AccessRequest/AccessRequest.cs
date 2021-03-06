﻿/// <summary>
/// SafeExchange 
/// </summary>

namespace SpaceOyster.SafeExchange.Core
{
    using System;

    public class AccessRequest
    {
        public AccessRequest()
        {
        }

        public AccessRequest(AccessRequest resource)
        {
            this.id = resource.id;
            this.PartitionKey = resource.PartitionKey;

            this.SubjectName = resource.SubjectName;
            this.ObjectName = resource.ObjectName;
            this.Permissions = resource.Permissions;

            this.Recipients = new RequestRecipient[resource.Recipients.Length];
            for (int i = 0; i < resource.Recipients.Length; i++)
            {
                this.Recipients.SetValue(resource.Recipients[i], i);
            }

            this.RequestedAt = resource.RequestedAt;
            this.Status = resource.Status;
            this.FinishedBy = resource.FinishedBy;
            this.FinishedAt = resource.FinishedAt;
        }

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

        /// <summary>
        /// List of potential recipients
        /// </summary>
        public RequestRecipient[] Recipients { get; set; }

        public DateTime RequestedAt { get; set; }

        public RequestStatus Status { get; set; }

        public string FinishedBy { get; set; }

        public DateTime FinishedAt { get; set; }

        public override string ToString()
        {
            return $"Id:{this.id}, Subject:{this.SubjectName}, Object:{this.ObjectName}, Permissions:'{this.Permissions}', RequestedAt:{this.RequestedAt}, Status:{this.Status}";
        }
    }
}
