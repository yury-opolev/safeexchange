/// <summary>
/// Authorizer
/// </summary>

namespace SafeExchange.Core.Permissions
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Telemetry;
    using System;
    using System.Threading.Tasks;

    public class PermissionsManager : IPermissionsManager
    {
        internal const int GroupIdQueryBatchSize = 40;

        internal const int MaxEffectivePermissionsRequestSize = 10;

        private static readonly List<PermissionType> SinglePermissions =
            new()
            {
                PermissionType.Read,
                PermissionType.Write,
                PermissionType.GrantAccess,
                PermissionType.RevokeAccess
            };

        private readonly Features features;

        private readonly SafeExchangeDbContext dbContext;

        private readonly ILogger<PermissionsManager> logger;

        public PermissionsManager(IConfiguration configuration, SafeExchangeDbContext dbContext, ILogger<PermissionsManager> logger)
        {
            this.features = new Features();
            configuration.GetSection("Features").Bind(this.features);

            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<bool> IsConsentRequiredAsync(string userId)
        {
            var existingUser = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals(userId));
            return existingUser?.ConsentRequired ?? false;
        }

        public async Task<bool> IsAuthorizedAsync(SubjectType subjectType, string subjectId, string secretId, PermissionType permission)
        {
            if (subjectType == SubjectType.User)
            {
                subjectId = Normalize(subjectId);
            }

            var isAuthorized = await this.HasPermissionAsync(subjectType, subjectId, secretId, permission);
            this.logger.LogInformation($"Subject '{subjectType} (tid {TelemetryContext.Current})' {(isAuthorized ? "has" : "does not have")} direct {permission} permissions for '{secretId}'.");

            if (!isAuthorized && subjectType == SubjectType.User)
            {
                isAuthorized = await this.IsGroupAuthorizedAsync(subjectId, secretId, permission);
            }

            return isAuthorized;
        }

        public async Task SetPermissionAsync(SubjectType subjectType, string subjectId, string secretId, PermissionType permission)
            => await this.SetPermissionAsync(subjectType, subjectId, subjectId, secretId, permission);

        public async Task SetPermissionAsync(SubjectType subjectType, string subjectId, string subjectName, string secretId, PermissionType permission)
        {
            if (subjectType == SubjectType.User)
            {
                subjectId = Normalize(subjectId);
            }

            var canRead = (permission & PermissionType.Read) == PermissionType.Read;
            var canWrite = (permission & PermissionType.Write) == PermissionType.Write;
            var canGrantAccess = (permission & PermissionType.GrantAccess) == PermissionType.GrantAccess;
            var canRevokeAccess = (permission & PermissionType.RevokeAccess) == PermissionType.RevokeAccess;

            var subjectPermissions = await this.GetSubjectPermissionsAsync(secretId, subjectType, subjectId);
            if (subjectPermissions != null)
            {
                subjectPermissions.CanRead = canRead || subjectPermissions.CanRead;
                subjectPermissions.CanWrite = canWrite || subjectPermissions.CanWrite;
                subjectPermissions.CanGrantAccess = canGrantAccess || subjectPermissions.CanGrantAccess;
                subjectPermissions.CanRevokeAccess = canRevokeAccess || subjectPermissions.CanRevokeAccess;
            }
            else
            {
                var newPermissions = new SubjectPermissions(secretId, subjectType, subjectName, subjectId)
                {
                    CanRead = canRead,
                    CanWrite = canWrite,
                    CanGrantAccess = canGrantAccess,
                    CanRevokeAccess = canRevokeAccess
                };

                await this.dbContext.Permissions.AddAsync(newPermissions);
            }
        }

        public async Task UnsetPermissionAsync(SubjectType subjectType, string subjectId, string secretId, PermissionType permission)
        {
            if (subjectType == SubjectType.User)
            {
                subjectId = Normalize(subjectId);
            }

            var unsetRead = (permission & PermissionType.Read) == PermissionType.Read;
            var unsetWrite = (permission & PermissionType.Write) == PermissionType.Write;
            var unsetGrantAccess = (permission & PermissionType.GrantAccess) == PermissionType.GrantAccess;
            var unsetRevokeAccess = (permission & PermissionType.RevokeAccess) == PermissionType.RevokeAccess;

            var subjectPermissions = await this.GetSubjectPermissionsAsync(secretId, subjectType, subjectId);
            if (subjectPermissions == null)
            {
                return;
            }

            subjectPermissions.CanRead = unsetRead ? false : subjectPermissions.CanRead;
            subjectPermissions.CanWrite = unsetWrite ? false : subjectPermissions.CanWrite;
            subjectPermissions.CanGrantAccess = unsetGrantAccess ? false : subjectPermissions.CanGrantAccess;
            subjectPermissions.CanRevokeAccess = unsetRevokeAccess ? false : subjectPermissions.CanRevokeAccess;

            if (!subjectPermissions.CanRead && !subjectPermissions.CanWrite && !subjectPermissions.CanGrantAccess && !subjectPermissions.CanRevokeAccess)
            {
                this.dbContext.Permissions.Remove(subjectPermissions);
            }
        }

        public static PermissionType ComputeNetPermission(PermissionType existing, PermissionType remove, PermissionType add)
        {
            return (existing & ~remove) | add;
        }

        internal static string NormalizeSubjectId(SubjectType subjectType, string subjectId)
        {
            return subjectType == SubjectType.User ? Normalize(subjectId) : subjectId;
        }

        public async Task<(PermissionType before, PermissionType after)> ApplyNetPermissionAsync(
            SubjectType subjectType, string subjectId, string subjectName, string secretId,
            PermissionType removeFlags, PermissionType addFlags)
        {
            if (subjectType == SubjectType.User)
            {
                subjectId = Normalize(subjectId);
            }

            var subjectPermissions = await this.GetSubjectPermissionsAsync(secretId, subjectType, subjectId);
            var before = ToPermissionType(subjectPermissions);
            var after = ComputeNetPermission(before, removeFlags, addFlags);

            if (after == PermissionType.None)
            {
                if (subjectPermissions != null)
                {
                    this.dbContext.Permissions.Remove(subjectPermissions);
                }

                return (before, after);
            }

            var canRead = (after & PermissionType.Read) == PermissionType.Read;
            var canWrite = (after & PermissionType.Write) == PermissionType.Write;
            var canGrantAccess = (after & PermissionType.GrantAccess) == PermissionType.GrantAccess;
            var canRevokeAccess = (after & PermissionType.RevokeAccess) == PermissionType.RevokeAccess;

            if (subjectPermissions == null)
            {
                var newPermissions = new SubjectPermissions(secretId, subjectType, subjectName, subjectId)
                {
                    CanRead = canRead,
                    CanWrite = canWrite,
                    CanGrantAccess = canGrantAccess,
                    CanRevokeAccess = canRevokeAccess
                };

                await this.dbContext.Permissions.AddAsync(newPermissions);
            }
            else
            {
                // Set the resulting flags exactly (not OR-merge): the net already folded in any removals.
                subjectPermissions.CanRead = canRead;
                subjectPermissions.CanWrite = canWrite;
                subjectPermissions.CanGrantAccess = canGrantAccess;
                subjectPermissions.CanRevokeAccess = canRevokeAccess;
            }

            return (before, after);
        }

        private static PermissionType ToPermissionType(SubjectPermissions? subjectPermissions)
        {
            if (subjectPermissions is null)
            {
                return PermissionType.None;
            }

            return ToPermissionType(
                subjectPermissions.CanRead,
                subjectPermissions.CanWrite,
                subjectPermissions.CanGrantAccess,
                subjectPermissions.CanRevokeAccess);
        }

        private static PermissionType ToPermissionType(bool canRead, bool canWrite, bool canGrantAccess, bool canRevokeAccess)
        {
            var permission = PermissionType.None;
            if (canRead)
            {
                permission |= PermissionType.Read;
            }
            if (canWrite)
            {
                permission |= PermissionType.Write;
            }
            if (canGrantAccess)
            {
                permission |= PermissionType.GrantAccess;
            }
            if (canRevokeAccess)
            {
                permission |= PermissionType.RevokeAccess;
            }

            return permission;
        }

        internal static IEnumerable<List<string>> BatchBy(IReadOnlyList<string> values, int batchSize)
        {
            for (var offset = 0; offset < values.Count; offset += batchSize)
            {
                yield return values.Skip(offset).Take(batchSize).ToList();
            }
        }

        public static bool IsPresentPermission(SubjectPermissions permissionSet, PermissionType permission)
        {
            var result = true;

            foreach (PermissionType value in SinglePermissions)
            {
                if ((permission & value) == value)
                {
                    switch (value)
                    {
                        case PermissionType.Read:
                            result = result && permissionSet.CanRead;
                            break;

                        case PermissionType.Write:
                            result = result && permissionSet.CanWrite;
                            break;

                        case PermissionType.GrantAccess:
                            result = result && permissionSet.CanGrantAccess;
                            break;

                        case PermissionType.RevokeAccess:
                            result = result && permissionSet.CanRevokeAccess;
                            break;
                    }
                }
                
                if (result == false)
                {
                    break;
                }
            }

            return result;
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value.Trim(' ').ToLowerInvariant();
        }

        private async Task<bool> HasPermissionAsync(SubjectType subjectType, string subjectId, string secretName, PermissionType permission)
        {
            var permissions = await this.dbContext.Permissions.FirstOrDefaultAsync(p => p.SecretName.Equals(secretName) && p.SubjectType.Equals(subjectType) && p.SubjectId.Equals(subjectId));
            if (permissions == default)
            {
                return false;
            }

            return IsPresentPermission(permissions, permission);
        }

        public async ValueTask<bool> IsGroupAuthorizedAsync(string userName, string secretName, PermissionType permission)
        {
            if (!this.features.UseGroupsAuthorization)
            {
                return false;
            }

            var existingUser = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals(userName));
            if (existingUser == default)
            {
                return false;
            }

            if (existingUser.ConsentRequired)
            {
                this.logger.LogInformation($"Subject 'User (tid {TelemetryContext.Current})' has not consented to the AAD application to get group memberships, cannot authorize via groups.");
                return false;
            }

            var userGroups = existingUser.Groups;
            if (userGroups == default || userGroups.Count == 0)
            {
                this.logger.LogInformation($"Subject 'User (tid {TelemetryContext.Current})' does not have any group memberships, cannot authorize via groups.");
                return false;
            }

            var groupPermissions = await this.GetGroupPermissionsAsync(secretName);
            foreach (var groupPermission in groupPermissions)
            {
                var foundGroup = userGroups.FirstOrDefault(g => g.AadGroupId.Equals(groupPermission.SubjectId));
                if (foundGroup != default && IsPresentPermission(groupPermission, permission))
                {
                    this.logger.LogInformation($"Subject 'User (tid {TelemetryContext.Current})' has {permission} permissions for '{secretName}' via group {groupPermission.SubjectName} ({foundGroup.AadGroupId}).");
                    return true;
                }
            }

            this.logger.LogInformation($"Subject 'User (tid {TelemetryContext.Current})' does not have {permission} permissions for '{secretName}' via groups ({userGroups.Count} groups total).");
            return false;
        }

        private async Task<List<SubjectPermissions>> GetGroupPermissionsAsync(string secretName)
        {
            return await this.dbContext.Permissions.Where(p => p.SubjectType.Equals(SubjectType.Group) && p.SecretName.Equals(secretName)).ToListAsync();
        }

        public async Task<SubjectPermissions?> GetSubjectPermissionsAsync(string secretName, SubjectType subjectType, string subjectId)
        {
            return await this.dbContext.Permissions.FindAsync(secretName, subjectType, subjectId);
        }

        public async Task<bool> HasAnyAccessAsync(SubjectType subjectType, string subjectId, string secretId)
        {
            if (subjectType == SubjectType.User)
            {
                subjectId = Normalize(subjectId);
            }

            var directRow = await this.dbContext.Permissions
                .FirstOrDefaultAsync(p => p.SecretName.Equals(secretId)
                    && p.SubjectType.Equals(subjectType)
                    && p.SubjectId.Equals(subjectId));

            if (directRow != null && (directRow.CanRead || directRow.CanWrite || directRow.CanGrantAccess || directRow.CanRevokeAccess))
            {
                return true;
            }

            if (subjectType != SubjectType.User || !this.features.UseGroupsAuthorization)
            {
                return false;
            }

            var existingUser = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals(subjectId));
            if (existingUser == default || existingUser.ConsentRequired)
            {
                return false;
            }

            var userGroups = existingUser.Groups;
            if (userGroups == default || userGroups.Count == 0)
            {
                return false;
            }

            var groupIds = userGroups.Select(g => g.AadGroupId).ToList();
            var groupRows = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals(secretId) && p.SubjectType.Equals(SubjectType.Group) && groupIds.Contains(p.SubjectId))
                .ToListAsync();

            return groupRows.Any(r => r.CanRead || r.CanWrite || r.CanGrantAccess || r.CanRevokeAccess);
        }

        public async Task<PermissionType> GetEffectivePermissionsAsync(SubjectType subjectType, string subjectId, string secretId)
        {
            if (subjectType == SubjectType.User)
            {
                subjectId = Normalize(subjectId);
            }

            var effective = ToPermissionType(await this.GetSubjectPermissionsAsync(secretId, subjectType, subjectId));

            if (subjectType == SubjectType.User && this.features.UseGroupsAuthorization)
            {
                effective |= await this.GetGroupDerivedPermissionsAsync(subjectId, secretId);
            }

            return effective;
        }

        public async Task<IReadOnlyDictionary<string, PermissionType>> GetEffectivePermissionsAsync(
            SubjectType subjectType, string subjectId, IReadOnlyCollection<string> secretNames)
        {
            if (subjectType == SubjectType.User)
            {
                subjectId = Normalize(subjectId);
            }

            var effectiveBySecret = new Dictionary<string, PermissionType>(StringComparer.Ordinal);
            foreach (var secretName in secretNames)
            {
                effectiveBySecret[secretName] = PermissionType.None;
            }

            if (effectiveBySecret.Count == 0)
            {
                return effectiveBySecret;
            }

            if (effectiveBySecret.Count > MaxEffectivePermissionsRequestSize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(secretNames),
                    effectiveBySecret.Count,
                    $"At most {MaxEffectivePermissionsRequestSize} distinct secret names per call.");
            }

            var requestedNames = effectiveBySecret.Keys.ToList();

            var directRows = await this.dbContext.Permissions
                .Where(p => requestedNames.Contains(p.SecretName)
                    && p.SubjectType.Equals(subjectType)
                    && p.SubjectId.Equals(subjectId))
                .Select(p => new { p.SecretName, p.CanRead, p.CanWrite, p.CanGrantAccess, p.CanRevokeAccess })
                .ToListAsync();
            foreach (var row in directRows)
            {
                AccumulatePermission(
                    effectiveBySecret,
                    row.SecretName,
                    ToPermissionType(row.CanRead, row.CanWrite, row.CanGrantAccess, row.CanRevokeAccess));
            }

            if (subjectType == SubjectType.User && this.features.UseGroupsAuthorization)
            {
                var existingUser = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals(subjectId));
                if (existingUser is not null && !existingUser.ConsentRequired && existingUser.Groups is { Count: > 0 })
                {
                    // Keyed by SecretName (the partition key); membership filtered in memory.
                    var groupIds = existingUser.Groups.Select(g => g.AadGroupId).ToHashSet();
                    var groupRows = await this.dbContext.Permissions
                        .Where(p => requestedNames.Contains(p.SecretName) && p.SubjectType.Equals(SubjectType.Group))
                        .Select(p => new { p.SecretName, p.SubjectId, p.CanRead, p.CanWrite, p.CanGrantAccess, p.CanRevokeAccess })
                        .ToListAsync();
                    foreach (var row in groupRows)
                    {
                        if (groupIds.Contains(row.SubjectId))
                        {
                            AccumulatePermission(
                                effectiveBySecret,
                                row.SecretName,
                                ToPermissionType(row.CanRead, row.CanWrite, row.CanGrantAccess, row.CanRevokeAccess));
                        }
                    }
                }
            }

            return effectiveBySecret;
        }

        public async Task<IReadOnlyList<EffectiveSecretPermissions>> GetReadableSecretsAsync(SubjectType subjectType, string subjectId)
        {
            if (subjectType == SubjectType.User)
            {
                subjectId = Normalize(subjectId);
            }

            var directBySecret = new Dictionary<string, PermissionType>(StringComparer.Ordinal);
            var effectiveBySecret = new Dictionary<string, PermissionType>(StringComparer.Ordinal);

            var directRows = await this.dbContext.Permissions
                .Where(p => p.SubjectType.Equals(subjectType) && p.SubjectId.Equals(subjectId))
                .ToListAsync();
            foreach (var row in directRows)
            {
                AccumulatePermission(directBySecret, row.SecretName, ToPermissionType(row));
                AccumulatePermission(effectiveBySecret, row.SecretName, ToPermissionType(row));
            }

            if (subjectType == SubjectType.User && this.features.UseGroupsAuthorization)
            {
                var existingUser = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals(subjectId));
                if (existingUser is not null && !existingUser.ConsentRequired && existingUser.Groups is { Count: > 0 })
                {
                    // The container is partitioned by SecretName, so an unfiltered group scan grows
                    // with all group grants in the service; bounded SubjectId batches also keep a
                    // caller with thousands of groups from producing one huge IN clause.
                    var groupIds = existingUser.Groups.Select(g => g.AadGroupId).Distinct().ToList();
                    foreach (var batch in BatchBy(groupIds, GroupIdQueryBatchSize))
                    {
                        var groupRows = await this.dbContext.Permissions
                            .Where(p => p.SubjectType.Equals(SubjectType.Group) && batch.Contains(p.SubjectId))
                            .Select(p => new { p.SecretName, p.CanRead, p.CanWrite, p.CanGrantAccess, p.CanRevokeAccess })
                            .ToListAsync();
                        foreach (var row in groupRows)
                        {
                            AccumulatePermission(
                                effectiveBySecret,
                                row.SecretName,
                                ToPermissionType(row.CanRead, row.CanWrite, row.CanGrantAccess, row.CanRevokeAccess));
                        }
                    }
                }
            }

            return effectiveBySecret
                .Where(kvp => (kvp.Value & PermissionType.Read) == PermissionType.Read)
                .Select(kvp => new EffectiveSecretPermissions(
                    kvp.Key,
                    directBySecret.TryGetValue(kvp.Key, out var direct) ? direct : PermissionType.None,
                    kvp.Value))
                .ToList();
        }

        private async Task<PermissionType> GetGroupDerivedPermissionsAsync(string userId, string secretName)
        {
            var existingUser = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals(userId));
            if (existingUser is null || existingUser.ConsentRequired)
            {
                return PermissionType.None;
            }

            var userGroups = existingUser.Groups;
            if (userGroups is not { Count: > 0 })
            {
                return PermissionType.None;
            }

            var groupIds = userGroups.Select(g => g.AadGroupId).ToHashSet();
            var groupPermissions = await this.GetGroupPermissionsAsync(secretName);

            var effective = PermissionType.None;
            foreach (var groupPermission in groupPermissions)
            {
                if (groupIds.Contains(groupPermission.SubjectId))
                {
                    effective |= ToPermissionType(groupPermission);
                }
            }

            return effective;
        }

        private static void AccumulatePermission(Dictionary<string, PermissionType> effectiveBySecret, string secretName, PermissionType permission)
        {
            effectiveBySecret[secretName] = (effectiveBySecret.TryGetValue(secretName, out var existing) ? existing : PermissionType.None) | permission;
        }
    }
}
