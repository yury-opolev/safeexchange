/// <summary>
/// AuditPayloads — shared canonical payload-shape helpers used by handlers that
/// emit audit events. Centralises the wire shape so PermissionGranted /
/// PermissionRevoked / AccessRequested / etc. cannot drift from each other.
/// </summary>

namespace SafeExchange.Core.Audit
{
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Permissions;

    public static class AuditPayloads
    {
        public static object PermissionFlags(PermissionType p) => new
        {
            canRead = (p & PermissionType.Read) != 0,
            canWrite = (p & PermissionType.Write) != 0,
            canGrantAccess = (p & PermissionType.GrantAccess) != 0,
            canRevokeAccess = (p & PermissionType.RevokeAccess) != 0,
        };

        public static PermissionType ToPermissionType(SubjectPermissions? existing)
        {
            if (existing is null)
            {
                return PermissionType.None;
            }
            var p = PermissionType.None;
            if (existing.CanRead)
            {
                p |= PermissionType.Read;
            }
            if (existing.CanWrite)
            {
                p |= PermissionType.Write;
            }
            if (existing.CanGrantAccess)
            {
                p |= PermissionType.GrantAccess;
            }
            if (existing.CanRevokeAccess)
            {
                p |= PermissionType.RevokeAccess;
            }
            return p;
        }
    }
}
