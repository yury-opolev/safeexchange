/// <summary>
/// SafeExchangeAccess
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Audit;
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

        private readonly IAuditWriter auditWriter;

        public SafeExchangeAccess(SafeExchangeDbContext dbContext, IGroupsManager groupsManager, ITokenHelper tokenHelper, GlobalFilters globalFilters, IPurger purger, IPermissionsManager permissionsManager, IAuditWriter auditWriter)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.groupsManager = groupsManager ?? throw new ArgumentNullException(nameof(groupsManager));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.purger = purger ?? throw new ArgumentNullException(nameof(purger));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
            this.auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        }

        private static object PermissionFlags(PermissionType p) => new
        {
            canRead = (p & PermissionType.Read) != 0,
            canWrite = (p & PermissionType.Write) != 0,
            canGrantAccess = (p & PermissionType.GrantAccess) != 0,
            canRevokeAccess = (p & PermissionType.RevokeAccess) != 0,
        };

        private static PermissionType ToPermissionType(SubjectPermissions? existing)
        {
            if (existing is null)
            {
                return PermissionType.None;
            }
            var p = PermissionType.None;
            if (existing.CanRead) p |= PermissionType.Read;
            if (existing.CanWrite) p |= PermissionType.Write;
            if (existing.CanGrantAccess) p |= PermissionType.GrantAccess;
            if (existing.CanRevokeAccess) p |= PermissionType.RevokeAccess;
            return p;
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
                        return await this.GrantAccessAsync(existingMetadata, request, userCanRevokeAccess, subjectType, subjectId, log);
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

                        return await this.RevokeAccessAsync(existingMetadata, request, subjectType, subjectId, log);
                    }

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        private async Task<HttpResponseData> GrantAccessAsync(ObjectMetadata secret, HttpRequestData request, bool userCanRevokeAccess, SubjectType subjectType, string subjectId, ILogger log)
            => await ActionResults.TryCatchAsync(request, async () =>
        {
            var secretId = secret.ObjectName;
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

                var targetSubjectType = permissionInput.SubjectType.ToModel();
                var (targetSubjectId, targetSubjectName, beforePerm) = await this.ResolveTargetAndBeforeAsync(secretId, targetSubjectType, permissionInput, log);

                if (targetSubjectType.Equals(SubjectType.Group))
                {
                    await this.GrantAccessToGroupAsync(secretId, permissionInput, permission, subjectType, subjectId, log);
                }
                else
                {
                    log.LogInformation($"Setting permissions for '{secretId}': '{targetSubjectType} {permissionInput.SubjectName}' -> '{permission}'");
                    await this.permissionsManager.SetPermissionAsync(targetSubjectType, permissionInput.SubjectName, permissionInput.SubjectName, secretId, permission);
                }

                if (secret.AuditEnabled && !string.IsNullOrEmpty(targetSubjectId))
                {
                    await this.auditWriter.AppendAsync(
                        secret, SecretAuditEventType.PermissionGranted,
                        subjectType, subjectId, subjectId,
                        new
                        {
                            target = new
                            {
                                subjectType = targetSubjectType.ToString(),
                                subjectId = targetSubjectId,
                                subjectName = targetSubjectName,
                            },
                            permissions = new
                            {
                                from = PermissionFlags(beforePerm),
                                to = PermissionFlags(permission),
                            },
                        }, log);
                }
            }

            await this.dbContext.SaveChangesAsync();

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<string> { Status = "ok", Result = "ok" });
        }, nameof(GrantAccessAsync), log);

        private async Task<(string subjectId, string subjectName, PermissionType beforePerm)> ResolveTargetAndBeforeAsync(
            string secretId, SubjectType targetSubjectType, SubjectPermissionsInput input, ILogger log)
        {
            // For groups, the target id may come in two flavours (group GUID or mail); for
            // users / applications the SubjectName is the id. Look up an existing row to
            // capture the "before" permission flags for the audit payload.
            string subjectId;
            string subjectName = input.SubjectName ?? string.Empty;
            if (targetSubjectType == SubjectType.Group)
            {
                subjectId = Guid.TryParse(input.SubjectId, out _) ? input.SubjectId : subjectName;
            }
            else
            {
                subjectId = subjectName;
            }

            var existing = await this.dbContext.Permissions.FirstOrDefaultAsync(p =>
                p.SecretName == secretId && p.SubjectType == targetSubjectType && p.SubjectId == subjectId);
            return (subjectId, subjectName, ToPermissionType(existing));
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

        private async Task<HttpResponseData> RevokeAccessAsync(ObjectMetadata secret, HttpRequestData request, SubjectType actorType, string actorId, ILogger log)
            => await ActionResults.TryCatchAsync(request, async () =>
        {
            var secretId = secret.ObjectName;
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
                var targetSubjectType = permissionInput.SubjectType.ToModel();

                var (targetSubjectId, targetSubjectName, beforePerm) = await this.ResolveTargetAndBeforeAsync(secretId, targetSubjectType, permissionInput, log);

                log.LogInformation($"Unsetting permissions for '{secretId}': '{targetSubjectType} {permissionInput.SubjectName}' -> '{permission}'");
                await this.permissionsManager.UnsetPermissionAsync(targetSubjectType, permissionInput.SubjectName, secretId, permission);

                if (secret.AuditEnabled && !string.IsNullOrEmpty(targetSubjectId))
                {
                    var afterPerm = beforePerm & ~permission;
                    await this.auditWriter.AppendAsync(
                        secret, SecretAuditEventType.PermissionRevoked,
                        actorType, actorId, actorId,
                        new
                        {
                            target = new
                            {
                                subjectType = targetSubjectType.ToString(),
                                subjectId = targetSubjectId,
                                subjectName = targetSubjectName,
                            },
                            permissions = new
                            {
                                from = PermissionFlags(beforePerm),
                                to = PermissionFlags(afterPerm),
                            },
                        }, log);
                }
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
