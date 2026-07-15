/// <summary>
/// EffectivePermissionsOutput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using SafeExchange.Core.Permissions;

    /// <summary>
    /// The caller's effective permissions on a secret: the union of direct and group-derived
    /// grants. Presentation-only — the API remains the authorization boundary.
    /// </summary>
    public class EffectivePermissionsOutput
    {
        public bool CanRead { get; set; }

        public bool CanWrite { get; set; }

        public bool CanGrantAccess { get; set; }

        public bool CanRevokeAccess { get; set; }

        public static EffectivePermissionsOutput FromPermissionType(PermissionType permission) => new()
        {
            CanRead = (permission & PermissionType.Read) == PermissionType.Read,
            CanWrite = (permission & PermissionType.Write) == PermissionType.Write,
            CanGrantAccess = (permission & PermissionType.GrantAccess) == PermissionType.GrantAccess,
            CanRevokeAccess = (permission & PermissionType.RevokeAccess) == PermissionType.RevokeAccess,
        };
    }
}
