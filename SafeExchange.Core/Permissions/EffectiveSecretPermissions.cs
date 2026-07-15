/// <summary>
/// EffectiveSecretPermissions
/// </summary>

namespace SafeExchange.Core.Permissions
{
    /// <summary>
    /// The permissions a caller effectively has on a single secret, aggregated as the union of
    /// all applicable direct and group-derived grants.
    /// </summary>
    /// <param name="SecretName">The secret the permissions apply to.</param>
    /// <param name="Permissions">The union of direct and group-derived permission flags.</param>
    public sealed record EffectiveSecretPermissions(string SecretName, PermissionType Permissions);
}
