/// <summary>
/// AccessListOutput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System.Collections.Generic;

    /// <summary>
    /// The response for GET access/{secretId}: the secret's full access list (each subject's actual
    /// permissions) plus the current caller's effective permissions (direct unioned with group-derived),
    /// which clients use for capability checks only.
    /// </summary>
    public class AccessListOutput
    {
        public List<SubjectPermissionsOutput> AccessList { get; set; } = new();

        public EffectivePermissionsOutput CallerEffectivePermissions { get; set; }
    }
}
