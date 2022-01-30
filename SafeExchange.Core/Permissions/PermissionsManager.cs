﻿/// <summary>
/// Authorizer
/// </summary>

namespace SafeExchange.Core.Permissions
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Model;
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

        private List<GroupDictionaryItem> GroupItems;

        private readonly ILogger<PermissionsManager> logger;

        public PermissionsManager(IConfiguration configuration, SafeExchangeDbContext dbContext, ILogger<PermissionsManager> logger)
        {
            this.features = new Features();
            configuration.GetSection("Features").Bind(this.features);

            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<bool> IsAuthorizedAsync(string userUpn, string secretId, PermissionType permission)
        {
            userUpn = Normalize(userUpn);

            var isAuthorized = await this.HasPermissionAsync(userUpn, secretId, permission);
            this.logger.LogInformation($"User '{userUpn}' {(isAuthorized ? "has" : "does not have")} direct {permission} permissions for '{secretId}'.");

            if (!isAuthorized)
            {
                isAuthorized = await this.IsGroupAuthorizedAsync(userUpn, secretId, permission);
            }

            return isAuthorized;
        }

        public async Task SetPermissionAsync(string userUpn, string secretId, PermissionType permission)
        {
            userUpn = Normalize(userUpn);

            var canRead = (permission & PermissionType.Read) == PermissionType.Read;
            var canWrite = (permission & PermissionType.Write) == PermissionType.Write;
            var canGrantAccess = (permission & PermissionType.GrantAccess) == PermissionType.GrantAccess;
            var canRevokeAccess = (permission & PermissionType.RevokeAccess) == PermissionType.RevokeAccess;

            var subjectPermissions = await this.GetSubjectPermissionsAsync(secretId, userUpn);
            if (subjectPermissions != null)
            {
                subjectPermissions.CanRead = canRead || subjectPermissions.CanRead;
                subjectPermissions.CanWrite = canWrite || subjectPermissions.CanWrite;
                subjectPermissions.CanGrantAccess = canGrantAccess || subjectPermissions.CanGrantAccess;
                subjectPermissions.CanRevokeAccess = canRevokeAccess || subjectPermissions.CanRevokeAccess;
            }
            else
            {
                var newPermissions = new SubjectPermissions(secretId, userUpn)
                {
                    CanRead = canRead,
                    CanWrite = canWrite,
                    CanGrantAccess = canGrantAccess,
                    CanRevokeAccess = canRevokeAccess
                };

                await this.dbContext.Permissions.AddAsync(newPermissions);
            }
        }

        public async Task UnsetPermissionAsync(string userUpn, string secretId, PermissionType permission)
        {
            userUpn = Normalize(userUpn);

            var unsetRead = (permission & PermissionType.Read) == PermissionType.Read;
            var unsetWrite = (permission & PermissionType.Write) == PermissionType.Write;
            var unsetGrantAccess = (permission & PermissionType.GrantAccess) == PermissionType.GrantAccess;
            var unsetRevokeAccess = (permission & PermissionType.RevokeAccess) == PermissionType.RevokeAccess;

            var subjectPermissions = await this.GetSubjectPermissionsAsync(secretId, userUpn);
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

        private async Task<bool> HasPermissionAsync(string userName, string secretName, PermissionType permission)
        {
            var permissions = await this.dbContext.Permissions.FirstOrDefaultAsync(p => p.SecretName.Equals(secretName) && p.SubjectName.Equals(userName));
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

            var userGroups = existingUser.Groups;
            var existingPermissions = await this.GetAllPermissionsAsync(secretName);
            foreach (var existingPermission in existingPermissions)
            {
                if (!this.IsGroup(existingPermission.SubjectName))
                {
                    continue;
                }

                var foundGroup = userGroups.FirstOrDefault(g => g.AadGroupId.Equals(existingPermission.SubjectName));
                if (foundGroup != default && IsPresentPermission(existingPermission, permission))
                {
                    this.logger.LogInformation($"User '{userName}' has {permission} permissions for '{secretName}' via group {existingPermission.SubjectName} ({foundGroup.AadGroupId}).");
                    return true;
                }
            }

            this.logger.LogInformation($"User '{userName}' does not have {permission} permissions for '{secretName}' via groups ({userGroups.Count} groups total).");
            return false;
        }

        private bool IsGroup(string subjectName)
        {
            if (this.GroupItems is null)
            {
                this.GroupItems = this.dbContext.GroupDictionary.ToList();
            }

            return this.GroupItems.FirstOrDefault(g => g.GroupMail.Equals(subjectName)) != default;
        }

        private async Task<List<SubjectPermissions>> GetAllPermissionsAsync(string secretName)
        {
            return await this.dbContext.Permissions.Where(p => p.SecretName.Equals(secretName)).ToListAsync();
        }

        public async Task<SubjectPermissions?> GetSubjectPermissionsAsync(string secretName, string userUpn)
        {
            return await this.dbContext.Permissions.FindAsync(secretName, userUpn);
        }
    }
}