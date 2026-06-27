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

            var permission = PermissionType.None;
            if (subjectPermissions.CanRead)
            {
                permission |= PermissionType.Read;
            }
            if (subjectPermissions.CanWrite)
            {
                permission |= PermissionType.Write;
            }
            if (subjectPermissions.CanGrantAccess)
            {
                permission |= PermissionType.GrantAccess;
            }
            if (subjectPermissions.CanRevokeAccess)
            {
                permission |= PermissionType.RevokeAccess;
            }

            return permission;
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
                this.logger.LogInformation($"User '{userName}' has not consented to the AAD application to get group memberships, cannot authorize via groups.");
                return false;
            }

            var userGroups = existingUser.Groups;
            if (userGroups == default || userGroups.Count == 0)
            {
                this.logger.LogInformation($"User '{userName}' does not have any group memberships, cannot authorize via groups.");
                return false;
            }

            var groupPermissions = await this.GetGroupPermissionsAsync(secretName);
            foreach (var groupPermission in groupPermissions)
            {
                var foundGroup = userGroups.FirstOrDefault(g => g.AadGroupId.Equals(groupPermission.SubjectId));
                if (foundGroup != default && IsPresentPermission(groupPermission, permission))
                {
                    this.logger.LogInformation($"User '{userName}' has {permission} permissions for '{secretName}' via group {groupPermission.SubjectName} ({foundGroup.AadGroupId}).");
                    return true;
                }
            }

            this.logger.LogInformation($"User '{userName}' does not have {permission} permissions for '{secretName}' via groups ({userGroups.Count} groups total).");
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
    }
}
