/// <summary>
/// SafeExchangeAccess
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Graph;
    using SafeExchange.Core.Groups;
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

        private readonly IGroupsManager groupsManager;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        private readonly IPurger purger;

        private readonly IPermissionsManager permissionsManager;

        private readonly IOrphanedSecretManager orphanedSecretManager;

        private readonly IOptionsMonitor<Features> features;

        public SafeExchangeAccess(
            SafeExchangeDbContext dbContext,
            IGroupsManager groupsManager,
            ITokenHelper tokenHelper,
            GlobalFilters globalFilters,
            IPurger purger,
            IPermissionsManager permissionsManager,
            IOrphanedSecretManager orphanedSecretManager,
            IOptionsMonitor<Features> features)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.groupsManager = groupsManager ?? throw new ArgumentNullException(nameof(groupsManager));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.purger = purger ?? throw new ArgumentNullException(nameof(purger));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
            this.orphanedSecretManager = orphanedSecretManager ?? throw new ArgumentNullException(nameof(orphanedSecretManager));
            this.features = features ?? throw new ArgumentNullException(nameof(features));
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
                return await ActionResults.ForbiddenAsync(request, "Application is not registered or disabled.");
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
                        return await this.GrantAccessAsync(existingMetadata.ObjectName, request, userCanRevokeAccess, subjectType, subjectId, log);
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

                case "patch":
                    {
                        return await this.PatchAccessAsync(existingMetadata.ObjectName, request, subjectType, subjectId, log);
                    }

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        private async Task<HttpResponseData> GrantAccessAsync(string secretId, HttpRequestData request, bool userCanRevokeAccess, SubjectType subjectType, string subjectId, ILogger log)
            => await ActionResults.TryCatchAsync(request, async () =>
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
                await this.ApplyGrantAsync(secretId, permissionInput, userCanRevokeAccess, subjectType, subjectId, log);
            }

            await this.dbContext.SaveChangesAsync();

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<string> { Status = "ok", Result = "ok" });
        }, nameof(GrantAccessAsync), log);

        private async Task ApplyGrantAsync(string secretId, SubjectPermissionsInput permissionInput, bool userCanRevokeAccess, SubjectType callerType, string callerId, ILogger log)
        {
            var permission = permissionInput.GetPermissionType();
            if (!userCanRevokeAccess)
            {
                permission &= ~PermissionType.RevokeAccess;
            }

            var inputSubjectType = permissionInput.SubjectType.ToModel();
            if (inputSubjectType.Equals(SubjectType.Group))
            {
                await this.GrantAccessToGroupAsync(secretId, permissionInput, permission, callerType, callerId, log);
            }
            else
            {
                log.LogInformation($"Setting permissions for '{secretId}': '{inputSubjectType} {permissionInput.SubjectName}' -> '{permission}'");
                await this.permissionsManager.SetPermissionAsync(inputSubjectType, permissionInput.SubjectName, permissionInput.SubjectName, secretId, permission);
            }
        }

        private async Task GrantAccessToGroupAsync(string secretId, SubjectPermissionsInput permissionInput, PermissionType permission, SubjectType subjectType, string subjectId, ILogger log)
        {
            if (Guid.TryParse(permissionInput.SubjectId, out _))
            {
                await this.GrantAccessToGroupIdAsync(secretId, permissionInput, permission, subjectType, subjectId, log);
                return;
            }

            await this.GrantAccessToGroupMailAsync(secretId, permissionInput, permission, subjectType, subjectId, log);
        }

        private async Task GrantAccessToGroupIdAsync(string secretId, SubjectPermissionsInput permissionInput, PermissionType permission, SubjectType subjectType, string subjectId, ILogger log)
        {
            await this.EnsureGroupExistsAsync(permissionInput, subjectType, subjectId);

            log.LogInformation($"Setting permissions for '{secretId}': group '{permissionInput.SubjectName}' ({permissionInput.SubjectId}) -> '{permission}'");
            await this.permissionsManager.SetPermissionAsync(subjectType, permissionInput.SubjectId, permissionInput.SubjectName, secretId, permission);
        }

        private async Task GrantAccessToGroupMailAsync(string secretId, SubjectPermissionsInput permissionInput, PermissionType permission, SubjectType subjectType, string subjectId, ILogger log)
        {
            var existingGroup = await this.groupsManager.TryFindGroupByMailAsync(permissionInput.SubjectName);
            if (existingGroup == default)
            {
                return;
            }

            log.LogInformation($"Setting permissions for '{secretId}': group mail '{permissionInput.SubjectName}', id: '{existingGroup.GroupId}' -> '{permission}'");
            await this.permissionsManager.SetPermissionAsync(subjectType, existingGroup.GroupId, existingGroup.DisplayName, secretId, permission);
        }

        private async Task<HttpResponseData> GetAccessListAsync(HttpRequestData request, string secretId, ILogger log)
            => await ActionResults.TryCatchAsync(request, async () =>
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
            => await ActionResults.TryCatchAsync(request, async () =>
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
                var inputSubjectType = permissionInput.SubjectType.ToModel();
                log.LogInformation($"Unsetting permissions for '{secretId}': '{inputSubjectType} {permissionInput.SubjectName}' -> '{permission}'");
                await this.permissionsManager.UnsetPermissionAsync(inputSubjectType, permissionInput.SubjectName, secretId, permission);
            }

            if (this.features.CurrentValue.UseAccessGiveUp)
            {
                await this.orphanedSecretManager.ApplyOrphanRuleAsync(secretId, this.dbContext);
            }

            await this.dbContext.SaveChangesAsync();

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<string> { Status = "ok", Result = "ok" });
        }, nameof(RevokeAccessAsync), log);

        private async Task<HttpResponseData> PatchAccessAsync(string secretId, HttpRequestData request, SubjectType subjectType, string subjectId, ILogger log)
            => await ActionResults.TryCatchAsync(request, async () =>
        {
            var input = await this.TryGetAccessUpdateInputAsync(request, log);
            var addCount = input?.Add?.Count ?? 0;
            var removeCount = input?.Remove?.Count ?? 0;

            if (addCount == 0 && removeCount == 0)
            {
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Access update body must include at least one of 'add' or 'remove'." });
            }

            if (addCount > 0)
            {
                if (!await this.permissionsManager.IsAuthorizedAsync(subjectType, subjectId, secretId, PermissionType.GrantAccess))
                {
                    var consentRequired = await this.permissionsManager.IsConsentRequiredAsync(subjectId);
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.Forbidden,
                        ActionResults.InsufficientPermissions(PermissionType.GrantAccess, secretId, consentRequired ? GraphDataProvider.ConsentRequiredSubStatus : string.Empty));
                }
            }

            if (removeCount > 0)
            {
                if (!await this.permissionsManager.IsAuthorizedAsync(subjectType, subjectId, secretId, PermissionType.RevokeAccess))
                {
                    var consentRequired = await this.permissionsManager.IsConsentRequiredAsync(subjectId);
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.Forbidden,
                        ActionResults.InsufficientPermissions(PermissionType.RevokeAccess, secretId, consentRequired ? GraphDataProvider.ConsentRequiredSubStatus : string.Empty));
                }
            }

            // Adds inherit the same RevokeAccess masking behaviour as POST: a caller without RevokeAccess
            // cannot grant the RevokeAccess flag to others. (If they have RevokeAccess and removeCount > 0
            // we already verified that above; otherwise check it explicitly.)
            var userCanRevokeAccess = removeCount > 0
                || await this.permissionsManager.IsAuthorizedAsync(subjectType, subjectId, secretId, PermissionType.RevokeAccess);

            foreach (var rem in input.Remove ?? new List<SubjectPermissionsInput>())
            {
                var permission = rem.GetPermissionType();
                var inputSubjectType = rem.SubjectType.ToModel();
                log.LogInformation($"PATCH unsetting permissions for '{secretId}': '{inputSubjectType} {rem.SubjectName}' -> '{permission}'");
                await this.permissionsManager.UnsetPermissionAsync(inputSubjectType, rem.SubjectName, secretId, permission);
            }

            foreach (var add in input.Add ?? new List<SubjectPermissionsInput>())
            {
                await this.ApplyGrantAsync(secretId, add, userCanRevokeAccess, subjectType, subjectId, log);
            }

            log.LogInformation($"Subject {subjectType} '{subjectId}' applied {addCount} adds and {removeCount} removes to '{secretId}'.");

            if (this.features.CurrentValue.UseAccessGiveUp)
            {
                await this.orphanedSecretManager.ApplyOrphanRuleAsync(secretId, this.dbContext);
            }

            await this.dbContext.SaveChangesAsync();

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<string> { Status = "ok", Result = "ok" });
        }, nameof(PatchAccessAsync), log);

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

        private async Task<AccessUpdateInput?> TryGetAccessUpdateInputAsync(HttpRequestData request, ILogger log)
        {
            var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            try
            {
                return DefaultJsonSerializer.Deserialize<AccessUpdateInput>(requestBody);
            }
            catch (Exception exception)
            {
                log.LogWarning(exception, "Could not parse input data for access update input.");
                return null;
            }
        }

        private async Task<GroupDictionaryItem> EnsureGroupExistsAsync(SubjectPermissionsInput permissionInput, SubjectType subjectType, string subjectId)
        {
            if (permissionInput.SubjectType != SubjectTypeInput.Group)
            {
                throw new ArgumentException($"{nameof(permissionInput)} is not of subject type {SubjectTypeInput.Group}.");
            }

            var groupIntput = new GroupInput()
            {
                DisplayName = permissionInput.SubjectName
            };

            return await this.groupsManager.PutGroupAsync(permissionInput.SubjectId, groupIntput, subjectType, subjectId);
        }

    }
}
