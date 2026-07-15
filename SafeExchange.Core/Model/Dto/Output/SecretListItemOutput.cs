/// <summary>
/// SecretListItemOutput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System.Collections.Generic;

    /// <summary>
    /// One entry in the caller's secret list. The Can* flags are the caller's actual direct grant;
    /// EffectivePermissions is the caller's effective grant (direct unioned with group-derived) and
    /// is used by clients for capability checks only.
    /// </summary>
    public class SecretListItemOutput
    {
        public string ObjectName { get; set; }

        public SubjectTypeOutput SubjectType { get; set; }

        public string SubjectName { get; set; }

        public string SubjectId { get; set; }

        public bool CanRead { get; set; }

        public bool CanWrite { get; set; }

        public bool CanGrantAccess { get; set; }

        public bool CanRevokeAccess { get; set; }

        public List<string> Tags { get; set; } = new();

        public EffectivePermissionsOutput CallerEffectivePermissions { get; set; }
    }
}
