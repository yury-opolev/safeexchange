/// <summary>
/// PinnedSecretListItemOutput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System.Collections.Generic;

    /// <summary>
    /// One entry in the caller's pinned-secrets list (v3). The Can* flags are the caller's actual
    /// direct grant; CallerEffectivePermissions is the effective grant (direct unioned with
    /// group-derived). The client presents effective permissions so a secret reachable only through
    /// a group still shows as accessible rather than "no access".
    /// </summary>
    public class PinnedSecretListItemOutput
    {
        public string SecretName { get; set; }

        public bool Exists { get; set; }

        public bool CanRead { get; set; }

        public bool CanWrite { get; set; }

        public bool CanGrantAccess { get; set; }

        public bool CanRevokeAccess { get; set; }

        public List<string> Tags { get; set; } = new List<string>();

        public EffectivePermissionsOutput CallerEffectivePermissions { get; set; }
    }
}
