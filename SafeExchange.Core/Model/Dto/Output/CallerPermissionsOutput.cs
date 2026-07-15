/// <summary>
/// CallerPermissionsOutput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using SafeExchange.Core.Permissions;

    /// <summary>
    /// The current caller's effective permissions on a secret — the union of all applicable
    /// direct and group-derived grants. Exposed so the web client can present an accurate user
    /// experience without re-deriving capabilities from the raw access-control list. The API
    /// remains the final authorization boundary; these flags are for presentation only.
    /// </summary>
    public class CallerPermissionsOutput
    {
        public bool CanRead { get; set; }

        public bool CanWrite { get; set; }

        public bool CanGrantAccess { get; set; }

        public bool CanRevokeAccess { get; set; }

        public static CallerPermissionsOutput FromPermissionType(PermissionType permission) => new()
        {
            CanRead = (permission & PermissionType.Read) == PermissionType.Read,
            CanWrite = (permission & PermissionType.Write) == PermissionType.Write,
            CanGrantAccess = (permission & PermissionType.GrantAccess) == PermissionType.GrantAccess,
            CanRevokeAccess = (permission & PermissionType.RevokeAccess) == PermissionType.RevokeAccess,
        };
    }
}
