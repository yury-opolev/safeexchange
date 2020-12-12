/// SafeExchange

namespace SpaceOyster.SafeExchange
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;
    using Microsoft.Azure.Cosmos.Table;

    public class PermissionsHelper
    {
        private CloudTable subjectPermissionsTable;

        private CloudTable groupDictionaryTable;

        private bool initialized = false;

        private readonly IGraphClientProvider graphClientProvider;

        private readonly string[] graphScopes = new string[] { "User.Read" };

        public PermissionsHelper(CloudTable subjectPermissionsTable, CloudTable groupDictionaryTable, IGraphClientProvider graphClientProvider)
        {
            this.subjectPermissionsTable = subjectPermissionsTable ?? throw new ArgumentNullException(nameof(subjectPermissionsTable));
            this.groupDictionaryTable = groupDictionaryTable;
            this.graphClientProvider = graphClientProvider;
        }

        public async Task InitializeAsync()
        {
            if (this.initialized)
            {
                return;
            }

            await this.subjectPermissionsTable.CreateIfNotExistsAsync();
            if (this.groupDictionaryTable != null)
            {
                await this.groupDictionaryTable.CreateIfNotExistsAsync();
            }
            this.initialized = true;
        }

        public async Task SetPermissionAsync(string userName, string secretName, PermissionType permission)
        {
            var canRead = permission == PermissionType.Read;
            var canWrite = permission == PermissionType.Write;
            var canGrantAccess = permission == PermissionType.GrantAccess;
            var canRevokeAccess = permission == PermissionType.RevokeAccess;

            await this.InitializeAsync();
            var existingRow = await this.subjectPermissionsTable
                .ExecuteAsync(TableOperation.Retrieve<SubjectPermissions>(
                    PermissionsHelper.GetPartitionKey(secretName),
                    PermissionsHelper.GetRowKey(userName)));
            if (existingRow.Result is SubjectPermissions subjectPermissions)
            {
                canRead = canRead || subjectPermissions.CanRead;
                canWrite = canWrite || subjectPermissions.CanWrite;
                canGrantAccess = canGrantAccess || subjectPermissions.CanGrantAccess;
                canRevokeAccess = canRevokeAccess || subjectPermissions.CanRevokeAccess;
            }

            var subjectPermission = new SubjectPermissions()
            {
                PartitionKey = PermissionsHelper.GetPartitionKey(secretName),
                RowKey = PermissionsHelper.GetRowKey(userName),

                SecretName = secretName,
                SubjectName = userName,

                CanRead = canRead,
                CanWrite = canWrite,
                CanGrantAccess = canGrantAccess,
                CanRevokeAccess = canRevokeAccess
            };

            await this.subjectPermissionsTable.ExecuteAsync(TableOperation.InsertOrMerge(subjectPermission));
        }

        public async Task<bool> HasPermissionAsync(string userName, string secretName, PermissionType permission)
        {
            await this.InitializeAsync();
            var result = await this.subjectPermissionsTable
                .ExecuteAsync(TableOperation.Retrieve<SubjectPermissions>(
                    PermissionsHelper.GetPartitionKey(secretName),
                    PermissionsHelper.GetRowKey(userName)));

            if (result.Result is SubjectPermissions subjectPermissions)
            {
                return IsPresentPermission(subjectPermissions, permission);
            }
            return false;
        }

        public async Task DeletePermissionAsync(string userName, string secretName, PermissionType permission)
        {
            await this.InitializeAsync();
            var existingRow = await this.subjectPermissionsTable
                .ExecuteAsync(TableOperation.Retrieve<SubjectPermissions>(
                    PermissionsHelper.GetPartitionKey(secretName),
                    PermissionsHelper.GetRowKey(userName)));

            if (!(existingRow.Result is SubjectPermissions subjectPermission))
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
                await this.subjectPermissionsTable.ExecuteAsync(TableOperation.Delete(subjectPermission));
            }
            else
            {
                await this.subjectPermissionsTable.ExecuteAsync(TableOperation.InsertOrMerge(subjectPermission));
            }
        }

        public async Task DeleteAllPermissionsAsync(string secretName)
        {
            var rows  = await this.GetAllPermissionsAsync(secretName);
            foreach (var row in rows)
            {
                await this.subjectPermissionsTable.ExecuteAsync(TableOperation.Delete(row));
            }
        }

        public async Task<IList<SubjectPermissions>> GetAllPermissionsAsync(string secretName)
        {
            await this.InitializeAsync();
            return await this.GetAllRows(secretName);
        }

        public async Task<IList<SubjectPermissions>> ListSecretsWithPermissionAsync(string userName, PermissionType permission)
        {
            await this.InitializeAsync();
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

            var graphClient = this.graphClientProvider.GetGraphClient(tokenResult, this.graphScopes, log);
            var userMembershipIds = await GroupsHelper.TryGetMemberOfAsync(graphClient, log);
            return userMembershipIds;
        }

        private async Task<string> GetUserGroupIdAsync(string groupName, ILogger log)
        {
            var existingRow = await this.groupDictionaryTable
                .ExecuteAsync(TableOperation.Retrieve<GroupDictionaryItem>(
                    PermissionsHelper.GetPartitionKey(groupName),
                    string.Empty));

            if (!(existingRow.Result is GroupDictionaryItem groupItem))
            {
                return string.Empty;
            }

            return groupItem.GroupId;
        }

        public static IActionResult InsufficientPermissionsResult(PermissionType permission, string secretId)
        {
            return new ObjectResult(new { error = $"Insufficient permissions to do '{permission}' action on '{secretId}'" }) { StatusCode = StatusCodes.Status401Unauthorized };
        }

        private async Task<IList<SubjectPermissions>> GetAllRows(string secretName)
        {
            var result = new List<SubjectPermissions>();

            var query = new TableQuery<SubjectPermissions>()
                .Where(TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, GetPartitionKey(secretName)));
            TableContinuationToken continuationToken = null;

            do
            {
                var page = await this.subjectPermissionsTable.ExecuteQuerySegmentedAsync(query, continuationToken);
                continuationToken = page.ContinuationToken;
                result.AddRange(page.Results ?? new List<SubjectPermissions>());
            }
            while (continuationToken != null);

            return result;
        }

        private async Task<IList<SubjectPermissions>> GetAllRowsForSubjectPermissions(string subjectName, PermissionType permission)
        {
            var result = new List<SubjectPermissions>();

            var rowKeyFilter = TableQuery.GenerateFilterCondition("RowKey", QueryComparisons.Equal, GetRowKey(subjectName));
            var canReadFilter = TableQuery.GenerateFilterConditionForBool("CanRead", QueryComparisons.Equal, true);

            var query = new TableQuery<SubjectPermissions>()
                .Where(TableQuery.CombineFilters(rowKeyFilter, TableOperators.And, canReadFilter))
                .Select(new string[] { "SecretName", "CanRead", "CanWrite", "CanGrantAccess", "CanRevokeAccess" });
            TableContinuationToken continuationToken = null;

            do
            {
                var page = await this.subjectPermissionsTable.ExecuteQuerySegmentedAsync(query, continuationToken);
                continuationToken = page.ContinuationToken;
                result.AddRange(page.Results ?? new List<SubjectPermissions>());
            }
            while (continuationToken != null);

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

        private static string GetPartitionKey(string keyName)
        {
            return Base64Helper.StringToBase64(keyName);
        }

        private static string GetRowKey(string keyName)
        {
            return Base64Helper.StringToBase64(keyName);
        }
    }
}