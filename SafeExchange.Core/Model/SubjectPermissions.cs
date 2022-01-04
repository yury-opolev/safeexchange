/// <summary>
/// SubjectPermissions
/// </summary>

namespace SafeExchange.Core.Model
{
    using Microsoft.EntityFrameworkCore;
    using SafeExchange.Core.Model.Dto.Output;
    using System;

    [Index(nameof(SecretName), nameof(SubjectName))]
    public class SubjectPermissions
    {
        public SubjectPermissions()
        { }

        public SubjectPermissions(string secretName, string subjectName)
        {
            this.SecretName = secretName ?? throw new ArgumentNullException(nameof(secretName));
            this.SubjectName = subjectName ?? throw new ArgumentNullException(nameof(subjectName));
            this.PartitionKey = this.GetPartitionKey();
        }

        public string PartitionKey { get; set; }

        public string SecretName { get; set; }

        public string SubjectName { get; set; }

        public bool CanRead { get; set; }

        public bool CanWrite { get; set; }

        public bool CanGrantAccess { get; set; }

        public bool CanRevokeAccess { get; set; }

        internal SubjectPermissionsOutput ToDto() => new()
        {
            ObjectName = this.SecretName,
            SubjectName = this.SubjectName,

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
