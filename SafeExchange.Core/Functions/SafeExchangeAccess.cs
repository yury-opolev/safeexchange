/// <summary>
/// SafeExchangeAccess
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Graph;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using System;
    using System.Net;
    using System.Security.Claims;

    public class SafeExchangeAccess
    {
        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        private readonly IPurger purger;

        private readonly IPermissionsManager permissionsManager;

        public SafeExchangeAccess(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, IPurger purger, IPermissionsManager permissionsManager)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.purger = purger ?? throw new ArgumentNullException(nameof(purger));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
        }

        public async Task<HttpResponseData> Run(
            HttpRequestData request,
            string secretId, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            (SubjectType subjectType, string subjectId) = await SubjectHelper.GetSubjectInfoAsync(this.tokenHelper, principal, this.dbContext);
            if (SubjectType.Application.Equals(subjectType) && string.IsNullOrEmpty(subjectId))
            {
                await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Forbidden,
                    new BaseResponseObject<object> { Status = "forbidden", Error = "Application is not registered or disabled." });
            }

            log.LogInformation($"{nameof(SafeExchangeAccess)} triggered for '{secretId}' by {subjectType} {subjectId}, [{request.Method}].");

            var existingMetadata = await this.dbContext.Objects.FindAsync(secretId);
            if (existingMetadata == null)
            {
                log.LogInformation($"Cannot handle permissions for secret '{secretId}', as it not exists.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.NotFound,
                    new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretId}' not exists" });
            }

            switch (request.Method.ToLower())
            {
                case "post":
                    {
                        if (!await this.permissionsManager.IsAuthorizedAsync(subjectType, subjectId, secretId, PermissionType.GrantAccess))
                        {
                            var consentRequired = await this.permissionsManager.IsConsentRequiredAsync(subjectId);
                            return await ActionResults.CreateResponseAsync(
                                request, HttpStatusCode.Forbidden,
                                ActionResults.InsufficientPermissions(PermissionType.GrantAccess, secretId, consentRequired ? GraphDataProvider.ConsentRequiredSubStatus : string.Empty));
                        }

                        var userCanRevokeAccess = await this.permissionsManager.IsAuthorizedAsync(subjectType, subjectId, secretId, PermissionType.RevokeAccess);
                        return await this.GrantAccessAsync(existingMetadata.ObjectName, request, userCanRevokeAccess, log);
                    }

                case "get":
                    {
                        if (!await this.permissionsManager.IsAuthorizedAsync(subjectType, subjectId, secretId, PermissionType.Read))
                        {
                            var consentRequired = await this.permissionsManager.IsConsentRequiredAsync(subjectId);
                            return await ActionResults.CreateResponseAsync(
                                request, HttpStatusCode.Forbidden,
                                ActionResults.InsufficientPermissions(PermissionType.Read, secretId, consentRequired ? GraphDataProvider.ConsentRequiredSubStatus : string.Empty));
                        }

                        return await this.GetAccessListAsync(request, existingMetadata.ObjectName, log);
                    }

                case "delete":
                    {
                        if (!await this.permissionsManager.IsAuthorizedAsync(subjectType, subjectId, secretId, PermissionType.RevokeAccess))
                        {
                            var consentRequired = await this.permissionsManager.IsConsentRequiredAsync(subjectId);
                            return await ActionResults.CreateResponseAsync(
                                request, HttpStatusCode.Forbidden,
                                ActionResults.InsufficientPermissions(PermissionType.RevokeAccess, secretId, consentRequired ? GraphDataProvider.ConsentRequiredSubStatus : string.Empty));
                        }

                        return await this.RevokeAccessAsync(existingMetadata.ObjectName, request, log);
                    }

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        private async Task<HttpResponseData> GrantAccessAsync(string secretId, HttpRequestData request, bool userCanRevokeAccess, ILogger log)
            => await TryCatch(request, async () =>
        {
            var permissionsInput = await this.TryGetPermissionsInputAsync(request, log);
            if ((permissionsInput?.Count ?? 0) == 0)
            {
                log.LogInformation($"Permissions data for '{secretId}' not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Access settings are not provided." });
            }

            foreach (var permissionInput in permissionsInput ?? Array.Empty<SubjectPermissionsInput>().ToList())
            {
                var permission = permissionInput.GetPermissionType();
                if (!userCanRevokeAccess)
                {
                    permission &= ~PermissionType.RevokeAccess;
                }

                var subjectType = permissionInput.SubjectType.ToModel();
                log.LogInformation($"Setting permissions for '{secretId}': '{subjectType} {permissionInput.SubjectName}' -> '{permission}'");
                await this.permissionsManager.SetPermissionAsync(subjectType, permissionInput.SubjectName, secretId, permission);
            }

            await this.dbContext.SaveChangesAsync();

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<string> { Status = "ok", Result = "ok" });
        }, nameof(GrantAccessAsync), log);

        private async Task<HttpResponseData> GetAccessListAsync(HttpRequestData request, string secretId, ILogger log)
            => await TryCatch(request, async () =>
        {
            var existingPermissions = await this.dbContext.Permissions.Where(p => p.SecretName.Equals(secretId)).ToListAsync();

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<List<SubjectPermissionsOutput>>
                {
                    Status = "ok",
                    Result = existingPermissions.Select(p => p.ToDto()).ToList()
                });
        }, nameof(GetAccessListAsync), log);

        private async Task<HttpResponseData> RevokeAccessAsync(string secretId, HttpRequestData request, ILogger log)
            => await TryCatch(request, async () =>
        {
            var permissionsInput = await this.TryGetPermissionsInputAsync(request, log);
            if ((permissionsInput?.Count ?? 0) == 0)
            {
                log.LogInformation($"Permissions data for '{secretId}' not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Access settings are not provided." });
            }

            foreach (var permissionInput in permissionsInput ?? Array.Empty<SubjectPermissionsInput>().ToList())
            {
                var permission = permissionInput.GetPermissionType();
                var subjectType = permissionInput.SubjectType.ToModel();
                log.LogInformation($"Unsetting permissions for '{secretId}': '{subjectType} {permissionInput.SubjectName}' -> '{permission}'");
                await this.permissionsManager.UnsetPermissionAsync(permissionInput.SubjectType.ToModel(), permissionInput.SubjectName, secretId, permission);
            }

            await this.dbContext.SaveChangesAsync();

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<string> { Status = "ok", Result = "ok" });
        }, nameof(RevokeAccessAsync), log);

        private async Task<List<SubjectPermissionsInput>?> TryGetPermissionsInputAsync(HttpRequestData request, ILogger log)
        {
            var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            try
            {
                return DefaultJsonSerializer.Deserialize<List<SubjectPermissionsInput>>(requestBody);
            }
            catch (Exception exception)
            {
                log.LogWarning(exception, "Could not parse input data for permissions input.");
                return null;
            }
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
