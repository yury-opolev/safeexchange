/// <summary>
/// PermissionType
/// </summary>

namespace SafeExchange.Core.Permissions
{
    using System;

    [Flags]
    public enum PermissionType
    {
        None = 0,
        Read = 1,
        Write = 2,
        GrantAccess = 4,
        RevokeAccess = 8,
        Full = 15
    }
}
