/// SafeExchange

namespace SpaceOyster.SafeExchange.Core
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;
    using Microsoft.Azure.Cosmos.Table;
    using Microsoft.Azure.Cosmos;
    using System.Net;

    public class PermissionsHelper
    {
        private readonly Container subjectPermissions;

        private readonly Container groupDictionary;

        private readonly IGraphClientProvider graphClientProvider;

        private readonly string[] graphScopes = new string[] { "User.Read" };

        public PermissionsHelper(Container subjectPermissions, Container groupDictionary, IGraphClientProvider graphClientProvider)
        {
            this.subjectPermissions = subjectPermissions ?? throw new ArgumentNullException(nameof(subjectPermissions));
            this.groupDictionary = groupDictionary; // null allowed
            this.graphClientProvider = graphClientProvider; // null allowed
        }

        public async Task SetPermissionAsync(string userName, string secretName, PermissionType permission)
        {
            var canRead = permission == PermissionType.Read;
            var canWrite = permission == PermissionType.Write;
            var canGrantAccess = permission == PermissionType.GrantAccess;
            var canRevokeAccess = permission == PermissionType.RevokeAccess;

            var subjectPermissions = await this.GetSubjectPermissionsAsync(secretName, userName);
            if (subjectPermissions != default(SubjectPermissions))
            {
                canRead = canRead || subjectPermissions.CanRead;
                canWrite = canWrite || subjectPermissions.CanWrite;
                canGrantAccess = canGrantAccess || subjectPermissions.CanGrantAccess;
                canRevokeAccess = canRevokeAccess || subjectPermissions.CanRevokeAccess;
            }

            var subjectPermission = new SubjectPermissions()
            {
                id = PermissionsHelper.GetId(secretName, userName),
                PartitionKey = PermissionsHelper.GetPartitionKey(secretName),

                SecretName = secretName,
                SubjectName = userName,

                CanRead = canRead,
                CanWrite = canWrite,
                CanGrantAccess = canGrantAccess,
                CanRevokeAccess = canRevokeAccess
            };

            await this.subjectPermissions.UpsertItemAsync(subjectPermission);
        }

        public async Task<bool> HasPermissionAsync(string userName, string secretName, PermissionType permission)
        {
            var subjectPermissions = await this.GetSubjectPermissionsAsync(secretName, userName);
            if (subjectPermissions == default(SubjectPermissions))
            {
                return false;
            }

            return IsPresentPermission(subjectPermissions, permission);
        }

        public async Task DeletePermissionAsync(string userName, string secretName, PermissionType permission)
        {
            var subjectPermission = await this.GetSubjectPermissionsAsync(secretName, userName);
            if (subjectPermission == default(SubjectPermissions))
            {
                return;
            }

            switch (permission)
            {
                case PermissionType.Read:
                    subjectPermission.CanRead = false;
                    break;

                case PermissionType.Write:
                    subjectPermission.CanWrite = false;
                    break;

                case PermissionType.GrantAccess:
                    subjectPermission.CanGrantAccess = false;
                    break;

                case PermissionType.RevokeAccess:
                    subjectPermission.CanRevokeAccess = false;
                    break;
            }

            if (!subjectPermission.CanRead && !subjectPermission.CanWrite && !subjectPermission.CanGrantAccess && !subjectPermission.CanRevokeAccess)
            {
                await this.subjectPermissions.DeleteItemAsync<SubjectPermissions>(PermissionsHelper.GetId(secretName, userName), new PartitionKey(PermissionsHelper.GetPartitionKey(secretName)));
            }
            else
            {
                await this.subjectPermissions.UpsertItemAsync(subjectPermission);
            }
        }

        public async Task DeleteAllPermissionsAsync(string secretName)
        {
            var rows  = await this.GetAllPermissionsAsync(secretName);
            foreach (var row in rows)
            {
                await this.subjectPermissions.DeleteItemAsync<SubjectPermissions>(row.id, new PartitionKey(row.PartitionKey));
            }
        }

        public async Task<IList<SubjectPermissions>> GetAllPermissionsAsync(string secretName)
        {
            return await this.GetAllRows(secretName);
        }

        public async Task<IList<SubjectPermissions>> ListSecretsWithPermissionAsync(string userName, PermissionType permission)
        {
            return await this.GetAllRowsForSubjectPermissions(userName, permission);
        }

        public async Task<bool> IsAuthorizedAsync(string userName, string secretName, PermissionType permission, TokenResult tokenResult, ILogger log)
        {
            var isAuthorized = await this.HasPermissionAsync(userName, secretName, permission);
            log.LogInformation($"User '{userName}' {(isAuthorized ? "has" : "does not have")} direct {permission} permissions for '{secretName}'.");
            
            if (!isAuthorized)
            {
                isAuthorized = await this.IsGroupAuthorizedAsync(tokenResult, userName, secretName, permission, log);
            }

            return isAuthorized;
        }

        public async Task<bool> IsGroupAuthorizedAsync(TokenResult tokenResult, string userName, string secretName, PermissionType permission, ILogger log)
        {
            var groupAuthorization = Environment.GetEnvironmentVariable("FEATURES-USE-GROUP-AUTHORIZATION");
            if (!("TRUE".Equals(groupAuthorization, StringComparison.InvariantCultureIgnoreCase)))
            {
                return false;
            }

            var userGroupIds = await this.GetUserGroups(tokenResult, log);
            var existingPermissions = await this.GetAllPermissionsAsync(secretName);
            foreach (var existingPermission in existingPermissions)
            {
                var groupId = await this.GetUserGroupIdAsync(existingPermission.SubjectName, log);
                if (string.IsNullOrEmpty(groupId))
                {
                    continue;
                }

                if (userGroupIds.Contains(groupId) && IsPresentPermission(existingPermission, permission))
                {
                    log.LogInformation($"User '{userName}' has {permission} permissions for '{secretName}' via group {existingPermission.SubjectName} ({groupId}).");
                    return true;
                }
            }
            
            log.LogInformation($"User '{userName}' does not have {permission} permissions for '{secretName}' via groups ({userGroupIds.Count} groups total).");
            return false;
        }

        private async Task<IList<string>> GetUserGroups(TokenResult tokenResult, ILogger log)
        {
            if (this.graphClientProvider == null)
            {
                throw new ArgumentNullException(nameof(this.graphClientProvider));
            }

            var graphClient = await this.graphClientProvider.GetGraphClientAsync(tokenResult, this.graphScopes, log);
            var userMembershipIds = await GroupsHelper.TryGetMemberOfAsync(graphClient, log);
            return userMembershipIds;
        }

        private async Task<string> GetUserGroupIdAsync(string groupName, ILogger log)
        {
            try
            {
                var existingGroup = await this.groupDictionary.ReadItemAsync<GroupDictionaryItem>(groupName, new PartitionKey("-"));
                if (existingGroup.Resource == default(GroupDictionaryItem))
                {
                    log.LogInformation($"No userGroup '{groupName}' is registered.");
                    return string.Empty;
                }

                return existingGroup.Resource.GroupId;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                log.LogInformation($"No userGroup '{groupName}' is registered.");
                return string.Empty;
            }
        }

        public static IActionResult InsufficientPermissionsResult(PermissionType permission, string secretId)
        {
            return new ObjectResult(new { error = $"Insufficient permissions to do '{permission}' action on '{secretId}'" }) { StatusCode = StatusCodes.Status401Unauthorized };
        }

        private async Task<IList<SubjectPermissions>> GetAllRows(string secretName)
        {
            var query = new QueryDefinition("SELECT * FROM SubjectPermissions SP WHERE SP.SecretName = @secret_name")
                .WithParameter("@secret_name", secretName);

            var result = new List<SubjectPermissions>();
            using (var resultSetIterator = subjectPermissions.GetItemQueryIterator<SubjectPermissions>(query))
            {
                while (resultSetIterator.HasMoreResults)
                {
                    var response = await resultSetIterator.ReadNextAsync();
                    result.AddRange(response);
                }
            }

            return result;
        }

        private async Task<IList<SubjectPermissions>> GetAllRowsForSubjectPermissions(string subjectName, PermissionType permission)
        {
            var query = new QueryDefinition("SELECT * FROM SubjectPermissions SP WHERE SP.SubjectName = @subject_name AND SP.CanRead = @can_read")
                .WithParameter("@subject_name", subjectName)
                .WithParameter("@can_read", true);

            var result = new List<SubjectPermissions>();
            using (var resultSetIterator = subjectPermissions.GetItemQueryIterator<SubjectPermissions>(query))
            {
                while (resultSetIterator.HasMoreResults)
                {
                    var response = await resultSetIterator.ReadNextAsync();
                    result.AddRange(response);
                }
            }

            return result;
        }

        private bool IsPresentPermission(SubjectPermissions subjectPermissions, PermissionType permission)
        {
            switch (permission)
            {
                case PermissionType.Read:
                    return subjectPermissions.CanRead;

                case PermissionType.Write:
                    return subjectPermissions.CanWrite;

                case PermissionType.GrantAccess:
                    return subjectPermissions.CanGrantAccess;

                case PermissionType.RevokeAccess:
                    return subjectPermissions.CanRevokeAccess;

                default:
                    return false;
            }
        }

        public async Task<SubjectPermissions> GetSubjectPermissionsAsync(string secretName, string userName)
        {
            try
            {
                var partitionKey = new PartitionKey(PermissionsHelper.GetPartitionKey(secretName));
                var itemResponse = await this.subjectPermissions.ReadItemAsync<SubjectPermissions>(PermissionsHelper.GetId(secretName, userName), partitionKey);
                return itemResponse.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return default(SubjectPermissions);
            }
        }

        private static string GetId(string secretName, string userName)
        {
            return $"{secretName}:{userName}";
        }

        private static string GetPartitionKey(string secretName)
        {
            if (string.IsNullOrEmpty(secretName))
            {
                return "-";
            }

            return secretName.ToUpper().Substring(0, 1);
        }
    }
}