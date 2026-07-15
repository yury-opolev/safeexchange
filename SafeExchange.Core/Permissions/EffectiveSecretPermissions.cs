/// <summary>
/// EffectiveSecretPermissions
/// </summary>

namespace SafeExchange.Core.Permissions
{
    public sealed record EffectiveSecretPermissions(string SecretName, PermissionType Direct, PermissionType Effective);
}
