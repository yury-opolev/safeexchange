/// <summary>
/// AccessRequest
/// </summary>

namespace SafeExchange.Core.Model
{
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using System;
    using System.ComponentModel.DataAnnotations;

    public class AccessRequest
    {
        public AccessRequest()
        { }

        public AccessRequest(string secretId, string userUpn, SubjectPermissionsInput source)
        {
            this.Id = Guid.NewGuid().ToString();
            this.PartitionKey = this.GetPartitionKey();

            this.SubjectName = userUpn;
            this.ObjectName = secretId;

            this.Permission = source.GetPermissionType();

            var utcNow = DateTimeProvider.UtcNow;
            this.RequestedAt = utcNow;
            this.Status = RequestStatus.InProgress;
            this.FinishedBy = string.Empty;
            this.FinishedAt = DateTime.MinValue;
        }

        [Key]
        public string Id { get; set; }

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
        public PermissionType Permission { get; set; }

        /// <summary>
        /// List of potential recipients
        /// </summary>
        public List<RequestRecipient> Recipients { get; set; }

        public DateTime RequestedAt { get; set; }

        public RequestStatus Status { get; set; }

        public string FinishedBy { get; set; }

        public DateTime FinishedAt { get; set; }

        public override string ToString()
        {
            return $"Id:{this.Id}, Subject:{this.SubjectName}, Object:{this.ObjectName}, Permissions:'{this.Permission}', RequestedAt:{this.RequestedAt}, Status:{this.Status}";
        }

        internal AccessRequestOutput ToDto() => new()
        {
            Id = this.Id,
            SubjectName = this.SubjectName,
            ObjectName = this.ObjectName,

            CanRead = (this.Permission & PermissionType.Read) == PermissionType.Read,
            CanWrite = (this.Permission & PermissionType.Write) == PermissionType.Write,
            CanGrantAccess = (this.Permission & PermissionType.GrantAccess) == PermissionType.GrantAccess,
            CanRevokeAccess = (this.Permission & PermissionType.RevokeAccess) == PermissionType.RevokeAccess,

            RequestedAt = this.RequestedAt
        };

        private string GetPartitionKey()
        {
            var hashString = this.Id.GetHashCode().ToString("0000");
            return hashString.Substring(hashString.Length - 4, 4);
        }
    }
}
