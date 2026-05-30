/// <summary>
/// SecretAccessItemOutput — one subject-permissions row returned by
/// GET v2/admin/secret/{secretName}/access.
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    public class SecretAccessItemOutput
    {
        /// <summary>Display name or UPN of the subject (user / group / application).</summary>
        public string SubjectName { get; set; } = string.Empty;

        /// <summary>"User", "Group", or "Application".</summary>
        public string SubjectType { get; set; } = string.Empty;

        /// <summary>Subject can read the secret content.</summary>
        public bool CanRead { get; set; }

        /// <summary>Subject can write / update the secret content.</summary>
        public bool CanWrite { get; set; }

        /// <summary>Subject can grant access to others.</summary>
        public bool CanGrantAccess { get; set; }

        /// <summary>Subject can revoke access from others.</summary>
        public bool CanRevokeAccess { get; set; }
    }
}
