/// <summary>
/// SafeExchangeAccessRequest
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using System;
    using System.Net;
    using System.Security.Claims;
    using System.Web.Http;

    public class SafeExchangeAccessRequest
    {
        private readonly Features features;

        private readonly SafeExchangeDbContext dbContext;

        private readonly GlobalFilters globalFilters;

        private readonly ITokenHelper tokenHelper;

        private readonly IPurger purger;

        private readonly IPermissionsManager permissionsManager;

        public SafeExchangeAccessRequest(IConfiguration configuration, SafeExchangeDbContext dbContext, GlobalFilters globalFilters, ITokenHelper tokenHelper, IPurger purger, IPermissionsManager permissionsManager)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            this.features = new Features();
            configuration.GetSection("Features").Bind(this.features);

            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.purger = purger ?? throw new ArgumentNullException(nameof(purger));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
        }

        public async Task<HttpResponseData> Run(HttpRequestData request, string secretId, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResult ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            (SubjectType subjectType, string subjectId) = SubjectHelper.GetSubjectInfo(this.tokenHelper, principal);
            log.LogInformation($"{nameof(SafeExchangeAccessRequest)} triggered for '{secretId}' by {subjectType} {subjectId}, [{request.Method}].");

            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext);

            var existingMetadata = await this.dbContext.Objects.FindAsync(secretId);
            if (existingMetadata == null)
            {
                log.LogInformation($"Cannot request access to secret '{secretId}', because it not exists.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.NotFound,
                    new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretId}' not exists." });
            }

            switch (request.Method.ToLower())
            {
                case "post":
                    return await this.HandleAccessRequestCreation(request, secretId, subjectType, subjectId, log);

                case "patch":
                    return await this.HandleAccessRequestUpdate(request, secretId, subjectType, subjectId, log);

                case "delete":
                    return await this.HandleAccessRequestDeletion(request, secretId, subjectType, subjectId, log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        public async Task<HttpResponseData> RunList(HttpRequestData request, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResult ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            (SubjectType subjectType, string subjectId) = SubjectHelper.GetSubjectInfo(this.tokenHelper, principal);
            log.LogInformation($"{nameof(SafeExchangeAccessRequest)}-{nameof(RunList)} triggered by {subjectType} {subjectId} [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "get":
                    return await this.HandleAccessRequestList(request, subjectType, subjectId, log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        private async Task<HttpResponseData> HandleAccessRequestCreation(HttpRequestData request, string secretId, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () =>
        {
            var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            SubjectPermissionsInput? accessRequestInput;
            try
            {
                accessRequestInput = DefaultJsonSerializer.Deserialize<SubjectPermissionsInput>(requestBody);
            }
            catch
            {
                log.LogInformation($"Could not parse input data for access request.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Input data is not provided or incorrect." });
            }

            if (accessRequestInput == null)
            {
                log.LogInformation($"Input data for access request is not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Input data is not provided or incorrect." });
            }

            var requestedPermission = accessRequestInput.GetPermissionType();
            var existingRequest = await this.dbContext.AccessRequests
                .FirstOrDefaultAsync(ar => ar.Status == RequestStatus.InProgress && ar.ObjectName.Equals(secretId) && ar.SubjectType.Equals(subjectType) && ar.SubjectName.Equals(subjectId));

            if (existingRequest != null && existingRequest.Permission == requestedPermission)
            {
                log.LogInformation($"Found identical access request {existingRequest.Id} from {subjectType} {subjectId} for secret '{secretId}', skipping duplicate.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK,
                    new BaseResponseObject<string> { Status = "ok", Result = "ok" });
            }

            var accessRequest = new AccessRequest(secretId, subjectType, subjectId, accessRequestInput);

            var subjectPermissions = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals(secretId) && p.CanGrantAccess && !(p.SubjectType.Equals(subjectType) && p.SubjectName.Equals(subjectId)))
                .ToListAsync();
            var recipients = subjectPermissions.Select(p => new RequestRecipient() { AccessRequestId = accessRequest.Id, SubjectType = p.SubjectType, SubjectName = p.SubjectName }).ToList();
            accessRequest.Recipients = recipients;

            await this.dbContext.AccessRequests.AddAsync(accessRequest);
            await this.dbContext.SaveChangesAsync();

            log.LogInformation($"Created access request {accessRequest.Id} from {subjectType} {subjectId} for secret '{secretId}'.");

            await this.TryNotifyAsync(accessRequest, RequestStatus.InProgress);

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<string> { Status = "ok", Result = "ok" });

        }, nameof(HandleAccessRequestCreation), log);

        private async Task<HttpResponseData> HandleAccessRequestList(HttpRequestData request, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () =>
        {
            var outgoingRequests = await this.dbContext.AccessRequests.Where(
                ar =>
                    ar.SubjectType.Equals(subjectType) &&
                    ar.SubjectName.Equals(subjectId) &&
                    ar.Status == RequestStatus.InProgress)
                .AsNoTracking().ToListAsync();

            var incomingRequests = await this.dbContext.AccessRequests
                .FromSqlRaw(
                    " SELECT " +
                    "  AR.Id," +
                    "  AR.PartitionKey," +
                    "  AR.SubjectType," +
                    "  AR.SubjectName," +
                    "  AR.ObjectName," +
                    "  AR.Permission," +
                    "  AR.Recipients," +
                    "  AR.RequestedAt," +
                    "  AR.Status," +
                    "  AR.FinishedBy," +
                    "  AR.FinishedAt" +
                    " FROM AccessRequests AR" +
                    "  JOIN (SELECT VALUE RECIP FROM RECIP IN AR.Recipients WHERE RECIP.SubjectType = {0} AND RECIP.SubjectName = {1})" +
                    " WHERE AR.Status = {2}", subjectType, subjectId, RequestStatus.InProgress)
                .AsNoTracking().ToListAsync();

            var requests = new List<AccessRequestOutput>(outgoingRequests.Count + incomingRequests.Count);
            requests.AddRange(outgoingRequests.Select(ar => ar.ToDto()));
            requests.AddRange(incomingRequests.Select(ar => ar.ToDto()));

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<List<AccessRequestOutput>> { Status = "ok", Result = requests });

        }, nameof(HandleAccessRequestList), log);

        private async Task<HttpResponseData> HandleAccessRequestUpdate(HttpRequestData request, string secretId, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () =>
        {
            var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            AccessRequestUpdateInput? accessRequestInput;
            try
            {
                accessRequestInput = DefaultJsonSerializer.Deserialize<AccessRequestUpdateInput>(requestBody);
            }
            catch
            {
                log.LogInformation($"Could not parse input data for access request.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Input data is not provided or incorrect." });
            }

            if (string.IsNullOrEmpty(accessRequestInput?.RequestId))
            {
                log.LogInformation($"Input data for access request is not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Input data is not provided or incorrect." });
            }

            var existingRequest = await this.dbContext.AccessRequests.FindAsync(accessRequestInput.RequestId);
            if (existingRequest == null || !existingRequest.ObjectName.Equals(secretId))
            {
                log.LogWarning($"Cannot find access request '{accessRequestInput.RequestId}' for secret '{secretId}'.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Access request not exists or is for different secret." });
            }

            var foundRecipient = existingRequest.Recipients.FirstOrDefault(r => r.SubjectType.Equals(subjectType) && r.SubjectName.Equals(subjectId, StringComparison.OrdinalIgnoreCase));
            if (foundRecipient == null)
            {
                log.LogWarning($"{subjectType} '{subjectId}' is not in the list of request '{accessRequestInput.RequestId}' recipients on secret {secretId}.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "User is not a recipient." });
            }

            var userHasGrantRights = await this.permissionsManager.IsAuthorizedAsync(subjectType, subjectId, secretId, PermissionType.GrantAccess);
            if (!userHasGrantRights)
            {
                log.LogWarning($"{subjectType} {subjectId} does not have '{PermissionType.GrantAccess}' permission on secret '{secretId}', cannot approve.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Forbidden,
                    ActionResults.InsufficientPermissions(PermissionType.GrantAccess, secretId, string.Empty));
            }

            if (accessRequestInput.Approve)
            {
                await this.permissionsManager.SetPermissionAsync(existingRequest.SubjectType, existingRequest.SubjectName, secretId, existingRequest.Permission);
                existingRequest.Status = RequestStatus.Approved;
            }
            else
            {
                existingRequest.Status = RequestStatus.Rejected;
            }

            existingRequest.FinishedBy = $"{subjectType} {subjectId}";
            existingRequest.FinishedAt = DateTimeProvider.UtcNow;

            await this.dbContext.SaveChangesAsync();

            await this.TryNotifyAsync(existingRequest, RequestStatus.Approved);

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<string> { Status = "ok", Result = "ok" });

        }, nameof(HandleAccessRequestUpdate), log);

        private async Task<HttpResponseData> HandleAccessRequestDeletion(HttpRequestData request, string secretId, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () =>
        {
            var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            AccessRequestDeletionInput? accessRequestInput;
            try
            {
                accessRequestInput = DefaultJsonSerializer.Deserialize<AccessRequestDeletionInput>(requestBody);
            }
            catch
            {
                log.LogInformation($"Could not parse input data for access request.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Input data is not provided or incorrect." });
            }

            if (string.IsNullOrEmpty(accessRequestInput?.RequestId))
            {
                log.LogInformation($"Input data for access request is not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Input data is not provided or incorrect." });
            }

            var existingRequest = await this.dbContext.AccessRequests.FindAsync(accessRequestInput.RequestId);
            if (existingRequest == null || !existingRequest.ObjectName.Equals(secretId))
            {
                log.LogWarning($"Cannot find access request '{accessRequestInput.RequestId}' for secret '{secretId}'.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Access request not exists or is for different secret." });
            }

            if (!(existingRequest.SubjectType.Equals(subjectType) && existingRequest.SubjectName.Equals(subjectId, StringComparison.OrdinalIgnoreCase)))
            {
                log.LogWarning($"{subjectType} '{subjectId}' did not create request '{accessRequestInput.RequestId}' for secret {secretId}.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Forbidden,
                    ActionResults.InsufficientPermissions("AccessRequestCancellation", secretId, string.Empty));
            }

            this.dbContext.AccessRequests.Remove(existingRequest);
            await this.dbContext.SaveChangesAsync();

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<string> { Status = "ok", Result = "ok" });

        }, nameof(HandleAccessRequestDeletion), log);

        private async ValueTask TryNotifyAsync(AccessRequest accessRequest, RequestStatus currentStatus)
        {
            if (!this.features.UseNotifications)
            {
                return;
            }

            foreach (var recipient in accessRequest.Recipients)
            {
                // TODO ...
            }

            await Task.CompletedTask;
        }

        private static async Task<HttpResponseData> TryCatch(HttpRequestData request, Func<Task<HttpResponseData>> action, string actionName, ILogger log)
        {
            try
            {
                return await action();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, $"Exception in {actionName}: {ex.GetType()}: {ex.Message}");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.InternalServerError,
                    new BaseResponseObject<object> { Status = "error", Error = $"{ex.GetType()}: {ex.Message}" });
            }
        }
    }
}
