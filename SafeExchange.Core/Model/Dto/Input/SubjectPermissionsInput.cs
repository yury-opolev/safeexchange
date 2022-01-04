/// <summary>
/// SubjectPermissionsInput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Input
{
    using SafeExchange.Core.Permissions;
    using System;

    public class SubjectPermissionsInput
    {
        public string SubjectName { get; set; }

        public bool CanRead { get; set; }

        public bool CanWrite { get; set; }

        public bool CanGrantAccess { get; set; }

        public bool CanRevokeAccess { get; set; }

        public PermissionType GetPermissionType()
        {
            var result = PermissionType.None;

            result |= this.CanRead ? PermissionType.Read : PermissionType.None;
            result |= this.CanWrite ? PermissionType.Write : PermissionType.None;
            result |= this.CanGrantAccess ? PermissionType.GrantAccess : PermissionType.None;
            result |= this.CanRevokeAccess ? PermissionType.RevokeAccess : PermissionType.None;

            return result;
        }
    }
}
