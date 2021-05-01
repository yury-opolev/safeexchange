/// <summary>
/// SafeExchange
/// </summary>

namespace SpaceOyster.SafeExchange.Core
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Azure.Cosmos;
    using Microsoft.Extensions.Logging;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
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

        public async ValueTask<IList<AccessRequest>> GetAccessRequestsToHandleAsync(string userId)
        {
            this.logger.LogInformation($"{nameof(GetAccessRequestsToHandleAsync)} called by {userId}.");

            var query = new QueryDefinition(
                "SELECT AR.id, AR.SubjectName, AR.ObjectName, AR.Permissions, AR.RequestedAt FROM AccessRequests AR " +
                "JOIN(SELECT VALUE RECIP FROM RECIP IN AR.Recipients WHERE RECIP.Name = @user_id) " +
                "WHERE AR.Status = @status")
                .WithParameter("@user_id", userId)
                .WithParameter("@status", RequestStatus.InProgress);

            return await ProcessQueryAsync(query);
        }
         
        public async ValueTask<IList<AccessRequest>> GetAccessRequestsFromAsync(string userId)
        {
            this.logger.LogInformation($"{nameof(GetAccessRequestsFromAsync)} called by {userId}.");

            var query = new QueryDefinition("SELECT * FROM AccessRequests AR WHERE AR.SubjectName = @user_id AND AR.Status = @status")
                .WithParameter("@user_id", userId)
                .WithParameter("@status", RequestStatus.InProgress);

            return await ProcessQueryAsync(query);
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

            List<RequestRecipient> recipientsList = await this.GetSubjectsWithGrantRightsAsync(secretId, userId);
            var accessRequest = new AccessRequest()
            {
                id = GetNewId(),
                PartitionKey = GetPartitionKey(secretId),

                ObjectName = secretId,
                SubjectName = userId,
                Permissions = permissionsCsv,

                Recipients = recipientsList.ToArray(),

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

            var existingRequest = await this.GetAccessRequestAsync(requestId, secretId);
            if (existingRequest == default(AccessRequest))
            {
                this.logger.LogWarning($"Cannot find access request '{requestId}' on secret {secretId}.");
                return;
            }

            var foundRecipient = existingRequest.Recipients.FirstOrDefault(r => r.Name.Equals(userId, StringComparison.OrdinalIgnoreCase));
            if (foundRecipient == default(RequestRecipient))
            {
                this.logger.LogWarning($"User '{userId}' is not in the list of request '{requestId}' recipients on secret {secretId}.");
                return;
            }

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

            var existingRequest = await this.GetAccessRequestAsync(requestId, secretId);
            if (existingRequest == default(AccessRequest))
            {
                this.logger.LogWarning($"Cannot find access request '{requestId}' on secret {secretId}.");
                return;
            }

            var foundRecipient = existingRequest.Recipients.FirstOrDefault(r => r.Name.Equals(userId, StringComparison.OrdinalIgnoreCase));
            if (foundRecipient == default(RequestRecipient))
            {
                this.logger.LogWarning($"User '{userId}' is not in the list of request '{requestId}' recipients on secret {secretId}.");
                return;
            }

            var userHasGrantRights = await this.permissionsHelper.HasPermissionAsync(userId, secretId, PermissionType.GrantAccess);
            if (!userHasGrantRights)
            {
                this.logger.LogWarning($"User {userId} does not have '{PermissionType.GrantAccess}' permission on secret {secretId}, cannot approve.");
                return;
            }

            var updatedAccessRequest = await this.UpdateAccessRequestAsync(userId, requestId, secretId, RequestStatus.Rejected);
            await this.TryNotifyAsync(updatedAccessRequest);
        }

        public async ValueTask<IActionResult> DeleteAccessRequestAsync(string userId, string requestId, string secretId)
        {
            this.logger.LogInformation($"{nameof(DeleteAccessRequestAsync)} called by {userId} for access request {requestId} ({secretId}).");

            var existingRequest = await this.GetAccessRequestAsync(requestId, secretId);
            if (existingRequest == default(AccessRequest))
            {
                this.logger.LogWarning($"Cannot find access request '{requestId}' on secret {secretId}.");
                return new OkObjectResult(new { status = "ok" });
            }

            var isRequester = existingRequest.SubjectName.Equals(userId, StringComparison.OrdinalIgnoreCase);
            if (!isRequester)
            {
                this.logger.LogWarning($"User '{userId}' did not created request '{requestId}' for secret {secretId}.");
                return PermissionsHelper.InsufficientPermissionsResult("AccessRequestCancellation", secretId);
            }

            var deleted = await this.DeleteAccessRequestInternalAsync(requestId, secretId);
            if (deleted)
            {
                return new OkObjectResult(new { status = "ok" });
            }
            else
            {
                return new ObjectResult(new { status = "error", error = $"Could not delete access request." }) { StatusCode = StatusCodes.Status500InternalServerError };
            }
        }

        private async Task<List<RequestRecipient>> GetSubjectsWithGrantRightsAsync(string secretId, string excludeId)
        {
            var existingPermissions = await this.permissionsHelper.GetAllPermissionsAsync(secretId);
            var recipientsList = new List<RequestRecipient>(existingPermissions.Count);
            foreach (var existingPermission in existingPermissions)
            {
                if (!existingPermission.SubjectName.Equals(excludeId, StringComparison.OrdinalIgnoreCase) && existingPermission.CanGrantAccess)
                {
                    recipientsList.Add(new RequestRecipient() { Name = existingPermission.SubjectName });
                }
            }

            return recipientsList;
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

            var accessRequest = await this.GetAccessRequestAsync(requestId, secretId);
            if (accessRequest == default(AccessRequest))
            {
                return false;
            }

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

            var existingAccessRequest = await this.GetAccessRequestAsync(requestId, secretId);
            if (existingAccessRequest == default(AccessRequest))
            {
                return default(AccessRequest);
            }

            var accessRequest = new AccessRequest(existingAccessRequest)
            {
                Status = newStatus,
                FinishedBy = userId,
                FinishedAt = now
            };

            var updatedItemResponse = await this.accessRequests.UpsertItemAsync(accessRequest);
            return updatedItemResponse.Resource;
        }

        private async ValueTask<bool> DeleteAccessRequestInternalAsync(string requestId, string secretId)
        {
            try
            {
                var partitionKey = new PartitionKey(AccessRequestHelper.GetPartitionKey(secretId));
                await this.accessRequests.DeleteItemAsync<AccessRequest>(requestId, partitionKey);
                return true;
            }
            catch (CosmosException ex)
            {
                this.logger.LogWarning($"Access request '{requestId}' ({secretId}) cannot be deleted: {ex}");
                return false;
            }
        }

        private async ValueTask<AccessRequest> GetAccessRequestAsync(string requestId, string secretId)
        {
            try
            {
                var partitionKey = new PartitionKey(AccessRequestHelper.GetPartitionKey(secretId));
                var itemResponse = await this.accessRequests.ReadItemAsync<AccessRequest>(requestId, partitionKey);
                return itemResponse.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return default(AccessRequest);
            }
        }

        private async ValueTask<IList<AccessRequest>> TryGetAccessRequestsAsync(string userId, string secretId, RequestStatus status)
        {
            var query = new QueryDefinition("SELECT * FROM AccessRequests AR WHERE AR.SubjectName = @user_id AND AR.ObjectName = @secret_id AND AR.Status = @status")
                .WithParameter("@user_id", userId)
                .WithParameter("@secret_id", secretId)
                .WithParameter("@status", status);

            return await ProcessQueryAsync(query);
        }

        private async ValueTask<IList<AccessRequest>> ProcessQueryAsync(QueryDefinition query)
        {
            var result = new List<AccessRequest>();
            using (var resultSetIterator = this.accessRequests.GetItemQueryIterator<AccessRequest>(query))
            {
                while (resultSetIterator.HasMoreResults)
                {
                    var response = await resultSetIterator.ReadNextAsync();
                    result.AddRange(response);
                }
            }

            return result;
        }

        private async ValueTask TryNotifyAsync(AccessRequest accessRequest)
        {
            var notifications = Environment.GetEnvironmentVariable("FEATURES-USE-NOTIFICATIONS");
            if (!("TRUE".Equals(notifications, StringComparison.InvariantCultureIgnoreCase)))
            {
                return;
            }

            this.logger.LogInformation($"{nameof(TryNotifyAsync)} called for access request {accessRequest.id}, status: {accessRequest.Status}.");

            List<string> userIdsToNotify = null;
            string messageText = null;
            string urlString = null;
            if (accessRequest.Status == RequestStatus.InProgress)
            {
                userIdsToNotify = new List<string>(accessRequest.Recipients.Length);
                foreach (var requestRecipient in accessRequest.Recipients)
                {
                    userIdsToNotify.Add(requestRecipient.Name);
                }
                messageText = $"Somebody requested access to {accessRequest.ObjectName} ({accessRequest.Permissions}).";
                urlString = "/accessrequests";
            }
            else
            {
                userIdsToNotify = new List<string>(1) { accessRequest.SubjectName };
                messageText = accessRequest.Status == RequestStatus.Approved ? $"Access to {accessRequest.ObjectName} granted." : $"Access request to {accessRequest.ObjectName} rejected.";
                if (accessRequest.Status == RequestStatus.Approved)
                {
                    messageText = $"Access to {accessRequest.ObjectName} granted.";
                    urlString = $"/viewdata/{accessRequest.ObjectName}";
                }
                else
                {
                    messageText = $"Access request to {accessRequest.ObjectName} rejected.";
                    urlString = "/accessrequests";
                }
            }

            var message = new NotificationMessage()
            {
                Message = messageText,
                Url = urlString
            };

            foreach (var userId in userIdsToNotify)
            {
                await this.notificationsHelper.TryNotifyAsync(userId, message);
            }
        }
    }
}
