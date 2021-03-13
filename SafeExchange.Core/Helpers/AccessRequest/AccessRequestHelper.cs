/// <summary>
/// SafeExchange
/// </summary>

namespace SpaceOyster.SafeExchange.Core
{
    using Microsoft.Azure.Cosmos;
    using Microsoft.Extensions.Logging;
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    public class AccessRequestHelper
    {
        private readonly Container accessRequests;

        private readonly PermissionsHelper permissionsHelper;

        private readonly NotificationsHelper notificationsHelper;

        private readonly ILogger logger;

        public AccessRequestHelper(Container accessRequests, PermissionsHelper permissionsHelper, NotificationsHelper notificationsHelper, ILogger logger)
        {
            this.accessRequests = accessRequests ?? throw new ArgumentNullException(nameof(accessRequests));
            this.permissionsHelper = permissionsHelper ?? throw new ArgumentNullException(nameof(permissionsHelper));
            this.notificationsHelper = notificationsHelper ?? throw new ArgumentNullException(nameof(notificationsHelper));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async ValueTask RequestAccessAsync(string userId, string secretId, IList<PermissionType> permissions)
        {
            var permissionsCsv = PermissionsHelper.PermissionsToString(permissions);
            this.logger.LogInformation($"{nameof(RequestAccessAsync)} called by {userId} for secret {secretId}, permissions: {permissionsCsv}.");

            var now = DateTime.UtcNow;

            var existingRequests = await this.TryGetAccessRequestsAsync(userId, secretId, RequestStatus.InProgress);
            foreach (var existingRequest in existingRequests)
            {
                PermissionsHelper.TryParsePermissions(existingRequest.Permissions, out var existingPermissionsList);
                if (PermissionsHelper.AreEqual(existingPermissionsList, permissions))
                {
                    this.logger.LogInformation($"Found identical access request {existingRequest.id} from {userId} for secret {secretId}, skipping duplicate.");
                    return;
                }
            }

            var accessRequest = new AccessRequest()
            {
                id = GetNewId(),
                PartitionKey = GetPartitionKey(secretId),

                ObjectName = secretId,
                SubjectName = userId,
                Permissions = permissionsCsv,

                RequestedAt = now,
                Status = RequestStatus.InProgress
            };

            await this.accessRequests.UpsertItemAsync(accessRequest);
            this.logger.LogInformation($"Created access request {accessRequest.id} from {userId} for secret {secretId}.");

            await this.TryNotifyAsync(accessRequest);
        }

        public async ValueTask ApproveAccessRequestAsync(string userId, string requestId, string secretId)
        {
            this.logger.LogInformation($"{nameof(ApproveAccessRequestAsync)} called by {userId} for access request {requestId} ({secretId}).");

            var userHasGrantRights = await this.permissionsHelper.HasPermissionAsync(userId, secretId, PermissionType.GrantAccess);
            if (!userHasGrantRights)
            {
                this.logger.LogWarning($"User {userId} does not have '{PermissionType.GrantAccess}' permission on secret {secretId}, cannot approve.");
                return;
            }

            var permissionsAdded = await this.AddPermissionsAsync(userId, requestId, secretId);
            if (!permissionsAdded)
            {
                return;
            }

            var updatedAccessRequest = await this.UpdateAccessRequestAsync(userId, requestId, secretId, RequestStatus.Approved);
            await this.TryNotifyAsync(updatedAccessRequest);
        }

        public async ValueTask DenyAccessRequestAsync(string userId, string requestId, string secretId)
        {
            this.logger.LogInformation($"{nameof(DenyAccessRequestAsync)} called by {userId} for access request {requestId} ({secretId}).");

            var userHasGrantRights = await this.permissionsHelper.HasPermissionAsync(userId, secretId, PermissionType.GrantAccess);
            if (!userHasGrantRights)
            {
                this.logger.LogWarning($"User {userId} does not have '{PermissionType.GrantAccess}' permission on secret {secretId}, cannot approve.");
                return;
            }

            var updatedAccessRequest = await this.UpdateAccessRequestAsync(userId, requestId, secretId, RequestStatus.Rejected);
            await this.TryNotifyAsync(updatedAccessRequest);
        }

        private static string GetNewId()
        {
            return $"{Guid.NewGuid()}";
        }

        private static string GetPartitionKey(string secretName)
        {
            if (string.IsNullOrEmpty(secretName))
            {
                return "-";
            }

            return secretName.ToUpper().Substring(0, 1);
        }

        private async ValueTask<bool> AddPermissionsAsync(string userId, string requestId, string secretId)
        {
            this.logger.LogInformation($"{nameof(AddPermissionsAsync)} called by {userId} for access request {requestId} ({secretId}).");

            var partitionKey = new PartitionKey(AccessRequestHelper.GetPartitionKey(secretId));
            var itemResponse = await this.accessRequests.ReadItemAsync<AccessRequest>(requestId, partitionKey);
            if (itemResponse.Resource == default(AccessRequest))
            {
                return false;
            }

            var accessRequest = itemResponse.Resource;
            try
            {
                PermissionsHelper.TryParsePermissions(accessRequest.Permissions, out var permissions);
                foreach (var permission in permissions)
                {
                    await this.permissionsHelper.SetPermissionAsync(accessRequest.SubjectName, accessRequest.ObjectName, permission);
                }
                this.logger.LogInformation($"{userId} granted permissions ({accessRequest.Permissions}) for {accessRequest.SubjectName} to {accessRequest.ObjectName}.");

                return true;
            }
            catch (Exception exception)
            {
                this.logger.LogWarning($"Could not grant permissions for {accessRequest.SubjectName} to {accessRequest.ObjectName}, {exception.GetType()}: {exception.Message}");
                return false;
            }
        }

        private async ValueTask<AccessRequest> UpdateAccessRequestAsync(string userId, string requestId, string secretId, RequestStatus newStatus)
        {
            var now = DateTime.UtcNow;

            var partitionKey = new PartitionKey(AccessRequestHelper.GetPartitionKey(secretId));
            var itemResponse = await this.accessRequests.ReadItemAsync<AccessRequest>(requestId, partitionKey);
            if (itemResponse.Resource == default(AccessRequest))
            {
                return default(AccessRequest);
            }

            var accessRequest = new AccessRequest(itemResponse.Resource)
            {
                Status = newStatus,
                FinishedBy = userId,
                FinishedAt = now
            };

            var updatedItemResponse = await this.accessRequests.UpsertItemAsync(accessRequest);
            return updatedItemResponse.Resource;
        }

        private async ValueTask<IList<AccessRequest>> TryGetAccessRequestsAsync(string userId, string secretId, RequestStatus status)
        {
            // TODO ...
            return await Task.FromResult(default(IList<AccessRequest>));
        }

        private async ValueTask TryNotifyAsync(AccessRequest accessRequest)
        {
            this.logger.LogInformation($"{nameof(TryNotifyAsync)} called for access request {accessRequest.id}, status: {accessRequest.Status}.");

            List<string> userIdsToNotify = null;
            string messageText = null;
            if (accessRequest.Status == RequestStatus.InProgress)
            {
                var usersWithPermissions = await this.permissionsHelper.GetAllPermissionsAsync(accessRequest.ObjectName);
                userIdsToNotify = new List<string>(usersWithPermissions.Count);
                foreach (var user in usersWithPermissions)
                {
                    if (!user.CanGrantAccess || accessRequest.SubjectName.Equals(user.SubjectName, StringComparison.InvariantCultureIgnoreCase))
                    {
                        continue;
                    }

                    userIdsToNotify.Add(user.SubjectName);
                }
                messageText = $"Access requested ({accessRequest.Permissions}).";
            }
            else
            {
                userIdsToNotify = new List<string>(1) { accessRequest.SubjectName };
                messageText = accessRequest.Status == RequestStatus.Approved ? "Access granted." : "Access request rejected.";
            }

            var message = new NotificationMessage()
            {
                From = accessRequest.SubjectName,
                Topic = accessRequest.ObjectName,
                MessageText = messageText,
                Uri = ""
            };

            foreach (var userId in userIdsToNotify)
            {
                await this.notificationsHelper.TryNotifyAsync(userId, message);
            }
        }
    }
}
