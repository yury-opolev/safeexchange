/// <summary>
/// SubjectPermissions
/// </summary>

namespace SafeExchange.Core.Model
{
    using Microsoft.EntityFrameworkCore;
    using SafeExchange.Core.Model.Dto.Output;
    using System;

    [Index(nameof(SecretName), nameof(SubjectType), nameof(SubjectId))]
    public class SubjectPermissions
    {
        public SubjectPermissions()
        { }

        public SubjectPermissions(string secretName, SubjectType subjectType, string subjectName)
            : this(secretName, subjectType, subjectName, subjectName)
        {
        }

        public SubjectPermissions(string secretName, SubjectType subjectType, string subjectName, string subjectId)
        {
            this.SecretName = secretName ?? throw new ArgumentNullException(nameof(secretName));
            this.SubjectType = subjectType;
            this.SubjectName = subjectName ?? throw new ArgumentNullException(nameof(subjectName));
            this.SubjectId = subjectId ?? throw new ArgumentNullException(nameof(subjectId));
            this.PartitionKey = this.GetPartitionKey();
        }

        public string PartitionKey { get; set; }

        public string SecretName { get; set; }

        public SubjectType SubjectType { get; set; } = SubjectType.User;

        public string SubjectName { get; set; }

        public string SubjectId { get; set; }

        public bool CanRead { get; set; }

        public bool CanWrite { get; set; }

        public bool CanGrantAccess { get; set; }

        public bool CanRevokeAccess { get; set; }

        internal SubjectPermissionsOutput ToDto() => new()
        {
            ObjectName = this.SecretName,
            SubjectType = this.SubjectType.ToDto(),
            SubjectName = this.SubjectName,
            SubjectId = this.SubjectId,

            CanRead = this.CanRead,
            CanWrite = this.CanWrite,
            CanGrantAccess = this.CanGrantAccess,
            CanRevokeAccess = this.CanRevokeAccess,
        };

        private string GetPartitionKey()
        {
            var hashString = this.SecretName.GetHashCode().ToString("0000");
            return hashString.Substring(hashString.Length - 4, 4);
        }
    }
}
