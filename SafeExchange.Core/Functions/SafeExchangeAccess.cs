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
    using SafeExchange.Core.Telemetry;
    using System;
    using System.Net;
    using System.Net.Sockets;
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

        private readonly IOrphanedSecretManager orphanedSecretManager;

        public SafeExchangeAccess(
            SafeExchangeDbContext dbContext,
            IGroupsManager groupsManager,
            ITokenHelper tokenHelper,
            GlobalFilters globalFilters,
            IPurger purger,
            IPermissionsManager permissionsManager,
            IAuditWriter auditWriter,
            IOrphanedSecretManager orphanedSecretManager)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.groupsManager = groupsManager ?? throw new ArgumentNullException(nameof(groupsManager));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.purger = purger ?? throw new ArgumentNullException(nameof(purger));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
            this.orphanedSecretManager = orphanedSecretManager ?? throw new ArgumentNullException(nameof(orphanedSecretManager));
            this.auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
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

            log.LogInformation($"{nameof(SafeExchangeAccess)} triggered for '{secretId}' by {subjectType} (tid {TelemetryContext.Current}), [{request.Method}].");

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

                case "patch":
                    {
                        return await this.PatchAccessAsync(existingMetadata, request, subjectType, subjectId, log);
                    }

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        private async Task<HttpResponseData> GrantAccessAsync(ObjectMetadata secret, HttpRequestData request, bool userCanRevokeAccess, SubjectType actorType, string actorId, ILogger log)
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
                await this.ApplyGrantAsync(secret, permissionInput, userCanRevokeAccess, actorType, actorId, log);
            }

            await this.dbContext.SaveChangesAsync();

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<string> { Status = "ok", Result = "ok" });
        }, nameof(GrantAccessAsync), log);

        private async Task ApplyGrantAsync(ObjectMetadata secret, SubjectPermissionsInput permissionInput, bool userCanRevokeAccess, SubjectType actorType, string actorId, ILogger log)
        {
            var secretId = secret.ObjectName;
            var permission = permissionInput.GetPermissionType();
            if (!userCanRevokeAccess)
            {
                permission &= ~PermissionType.RevokeAccess;
            }

            var targetSubjectType = permissionInput.SubjectType.ToModel();
            var (targetSubjectId, targetSubjectName, beforePerm) = await this.ResolveTargetAndBeforeAsync(secretId, targetSubjectType, permissionInput, log);
            
            if (targetSubjectType.Equals(SubjectType.Group))
            {
                await this.GrantAccessToGroupAsync(secretId, permissionInput, permission, actorType, actorId, log);
            }
            else
            {
                log.LogInformation($"Setting permissions for '{secretId}': '{targetSubjectType} {permissionInput.SubjectName}' -> '{permission}'");
                await this.permissionsManager.SetPermissionAsync(targetSubjectType, permissionInput.SubjectName, permissionInput.SubjectName, secretId, permission);
            }

            if (secret.AuditEnabled && !string.IsNullOrEmpty(targetSubjectId))
            {
                // SetPermissionAsync OR-merges into existing flags; report the resulting
                // effective permission as `to`, not the requested delta.
                var afterPerm = beforePerm | permission;
                await this.auditWriter.AppendAsync(
                    secret, SecretAuditEventType.PermissionGranted,
                    actorType, actorId,
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
                            from = AuditPayloads.PermissionFlags(beforePerm),
                            to = AuditPayloads.PermissionFlags(afterPerm),
                        },
                    }, log);
            }
        }

        private async Task ApplyRevokeAsync(ObjectMetadata secret, SubjectPermissionsInput permissionInput, SubjectType actorType, string actorId, ILogger log)
        {
            var secretId = secret.ObjectName;
            var permission = permissionInput.GetPermissionType();
            var targetSubjectType = permissionInput.SubjectType.ToModel();
            var (targetSubjectId, targetSubjectName, beforePerm) = await this.ResolveTargetAndBeforeAsync(secretId, targetSubjectType, permissionInput, log);

            // For groups, the row is keyed by GroupId; for users/applications subjectId == subjectName.
            // ResolveTargetAndBeforeAsync canonicalises that, so revoke uses the same lookup as grant.
            var unsetKey = string.IsNullOrEmpty(targetSubjectId) ? permissionInput.SubjectName : targetSubjectId;
            log.LogInformation($"Unsetting permissions for '{secretId}': '{targetSubjectType} {permissionInput.SubjectName}' -> '{permission}'");
            await this.permissionsManager.UnsetPermissionAsync(targetSubjectType, unsetKey, secretId, permission);

            if (secret.AuditEnabled && !string.IsNullOrEmpty(targetSubjectId))
            {
                var afterPerm = beforePerm & ~permission;
                await this.auditWriter.AppendAsync(
                    secret, SecretAuditEventType.PermissionRevoked,
                    actorType, actorId,
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
                            from = AuditPayloads.PermissionFlags(beforePerm),
                            to = AuditPayloads.PermissionFlags(afterPerm),
                        },
                    }, log);
            }
        }

        private async Task<(string subjectId, string subjectName, PermissionType beforePerm)> ResolveTargetAndBeforeAsync(
            string secretId, SubjectType targetSubjectType, SubjectPermissionsInput input, ILogger log)
        {
            // Canonicalise the target id so grant and revoke key off the same value.
            // Groups: GUID flavour uses SubjectId verbatim; mail flavour resolves to the
            // registered GroupId via groupsManager (same path as GrantAccessToGroupMailAsync).
            // Users / applications: SubjectName is the id.
            string subjectName = input.SubjectName ?? string.Empty;
            string subjectId;
            if (targetSubjectType == SubjectType.Group)
            {
                if (Guid.TryParse(input.SubjectId, out _))
                {
                    subjectId = input.SubjectId;
                }
                else
                {
                    var resolved = await this.groupsManager.TryFindGroupByMailAsync(subjectName);
                    subjectId = resolved == default ? subjectName : resolved.GroupId;
                }
            }
            else
            {
                subjectId = subjectName;
            }

            var existing = await this.dbContext.Permissions.FirstOrDefaultAsync(p =>
                p.SecretName == secretId && p.SubjectType == targetSubjectType && p.SubjectId == subjectId);
            return (subjectId, subjectName, AuditPayloads.ToPermissionType(existing));
        }

        /// <summary>
        /// Resolves the canonical (subjectId, subjectName) for an add target, mirroring the grant path:
        /// GUID groups are ensured to exist and keyed by GUID; mail groups resolve to the registered
        /// GroupId (returns null — a no-op — when the mail group is not registered); users and
        /// applications key off SubjectName.
        /// </summary>
        private async Task<(string subjectId, string subjectName)?> ResolveAddTargetAsync(
            SubjectType targetSubjectType, SubjectPermissionsInput add, SubjectType actorType, string actorId, ILogger log)
        {
            if (targetSubjectType != SubjectType.Group)
            {
                return (add.SubjectName, add.SubjectName);
            }

            if (Guid.TryParse(add.SubjectId, out _))
            {
                await this.EnsureGroupExistsAsync(add, actorType, actorId);
                return (add.SubjectId, add.SubjectName);
            }

            var existingGroup = await this.groupsManager.TryFindGroupByMailAsync(add.SubjectName);
            if (existingGroup == default)
            {
                return null;
            }

            return (existingGroup.GroupId, existingGroup.DisplayName);
        }

        private async Task GrantAccessToGroupAsync(string secretId, SubjectPermissionsInput permissionInput, PermissionType permission, SubjectType actorType, string actorId, ILogger log)
        {
            if (Guid.TryParse(permissionInput.SubjectId, out _))
            {
                await this.GrantAccessToGroupIdAsync(secretId, permissionInput, permission, actorType, actorId, log);
                return;
            }

            await this.GrantAccessToGroupMailAsync(secretId, permissionInput, permission, actorType, actorId, log);
        }

        private async Task GrantAccessToGroupIdAsync(string secretId, SubjectPermissionsInput permissionInput, PermissionType permission, SubjectType actorType, string actorId, ILogger log)
        {
            await this.EnsureGroupExistsAsync(permissionInput, actorType, actorId);

            log.LogInformation($"Setting permissions for '{secretId}': group '{permissionInput.SubjectName}' ({permissionInput.SubjectId}) -> '{permission}'");
            await this.permissionsManager.SetPermissionAsync(SubjectType.Group, permissionInput.SubjectId, permissionInput.SubjectName, secretId, permission);
        }

        private async Task GrantAccessToGroupMailAsync(string secretId, SubjectPermissionsInput permissionInput, PermissionType permission, SubjectType actorType, string actorId, ILogger log)
        {
            var existingGroup = await this.groupsManager.TryFindGroupByMailAsync(permissionInput.SubjectName);
            if (existingGroup == default)
            {
                return;
            }

            log.LogInformation($"Setting permissions for '{secretId}': group mail '{permissionInput.SubjectName}', id: '{existingGroup.GroupId}' -> '{permission}'");
            await this.permissionsManager.SetPermissionAsync(SubjectType.Group, existingGroup.GroupId, existingGroup.DisplayName, secretId, permission);
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
                await this.ApplyRevokeAsync(secret, permissionInput, actorType, actorId, log);
            }

            // ApplyOrphanRuleAsync self-gates on Features.UseAccessGiveUp and no-ops when off.
            await this.orphanedSecretManager.ApplyOrphanRuleAsync(secretId, this.dbContext);

            await this.dbContext.SaveChangesAsync();

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<string> { Status = "ok", Result = "ok" });
        }, nameof(RevokeAccessAsync), log);

        private async Task<HttpResponseData> PatchAccessAsync(ObjectMetadata secret, HttpRequestData request, SubjectType actorType, string actorId, ILogger log)
            => await ActionResults.TryCatchAsync(request, async () =>
        {
            var secretId = secret.ObjectName;
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
                if (!await this.permissionsManager.IsAuthorizedAsync(actorType, actorId, secretId, PermissionType.GrantAccess))
                {
                    var consentRequired = await this.permissionsManager.IsConsentRequiredAsync(actorId);
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.Forbidden,
                        ActionResults.InsufficientPermissions(PermissionType.GrantAccess, secretId, consentRequired ? GraphDataProvider.ConsentRequiredSubStatus : string.Empty));
                }
            }

            if (removeCount > 0)
            {
                if (!await this.permissionsManager.IsAuthorizedAsync(actorType, actorId, secretId, PermissionType.RevokeAccess))
                {
                    var consentRequired = await this.permissionsManager.IsConsentRequiredAsync(actorId);
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.Forbidden,
                        ActionResults.InsufficientPermissions(PermissionType.RevokeAccess, secretId, consentRequired ? GraphDataProvider.ConsentRequiredSubStatus : string.Empty));
                }
            }

            // Adds inherit the same RevokeAccess masking behaviour as POST: a caller without RevokeAccess
            // cannot grant the RevokeAccess flag to others. (If they have RevokeAccess and removeCount > 0
            // we already verified that above; otherwise check it explicitly.)
            var userCanRevokeAccess = removeCount > 0
                || await this.permissionsManager.IsAuthorizedAsync(actorType, actorId, secretId, PermissionType.RevokeAccess);

            // Coalesce every remove and add into a single net change per resolved subject, then apply
            // each once. Removing then re-adding the SAME subject in one request must NOT delete-then-
            // re-insert the same row inside one unit of work — Entity Framework would resolve that to a
            // delete and the subject would silently vanish from the access list. Instead we compute
            // '(existing & ~removed) | added' and write the result a single time.
            // (The older separate POST-grant / DELETE-revoke endpoints are intentionally left untouched.)
            var netChanges = new Dictionary<(SubjectType SubjectType, string SubjectId), PermissionNetChange>();

            PermissionNetChange GetNetChange(SubjectType targetSubjectType, string targetSubjectId, string targetSubjectName)
            {
                var key = (targetSubjectType, PermissionsManager.NormalizeSubjectId(targetSubjectType, targetSubjectId));
                if (!netChanges.TryGetValue(key, out var netChange))
                {
                    netChange = new PermissionNetChange(targetSubjectType, targetSubjectId, targetSubjectName);
                    netChanges[key] = netChange;
                }

                return netChange;
            }

            foreach (var rem in input.Remove ?? new List<SubjectPermissionsInput>())
            {
                var targetSubjectType = rem.SubjectType.ToModel();
                var (targetSubjectId, targetSubjectName, _) = await this.ResolveTargetAndBeforeAsync(secretId, targetSubjectType, rem, log);
                GetNetChange(targetSubjectType, targetSubjectId, targetSubjectName).Remove |= rem.GetPermissionType();
            }

            foreach (var add in input.Add ?? new List<SubjectPermissionsInput>())
            {
                var targetSubjectType = add.SubjectType.ToModel();
                var permission = add.GetPermissionType();
                if (!userCanRevokeAccess)
                {
                    permission &= ~PermissionType.RevokeAccess;
                }

                var resolved = await this.ResolveAddTargetAsync(targetSubjectType, add, actorType, actorId, log);
                if (resolved is null)
                {
                    // e.g. a mail-only group that is not registered — same no-op as the POST grant path.
                    continue;
                }

                var (targetSubjectId, targetSubjectName) = resolved.Value;
                GetNetChange(targetSubjectType, targetSubjectId, targetSubjectName).Add |= permission;
            }

            foreach (var netChange in netChanges.Values)
            {
                var (beforePerm, afterPerm) = await this.permissionsManager.ApplyNetPermissionAsync(
                    netChange.SubjectType, netChange.SubjectId, netChange.SubjectName, secretId, netChange.Remove, netChange.Add);

                if (secret.AuditEnabled && afterPerm != beforePerm && !string.IsNullOrEmpty(netChange.SubjectId))
                {
                    // One audit event per subject reflecting the true net effect. Direction picks the type:
                    // any newly-added flag -> Granted; otherwise (flags only removed) -> Revoked.
                    var eventType = (afterPerm & ~beforePerm) != PermissionType.None
                        ? SecretAuditEventType.PermissionGranted
                        : SecretAuditEventType.PermissionRevoked;

                    await this.auditWriter.AppendAsync(
                        secret, eventType,
                        actorType, actorId,
                        new
                        {
                            target = new
                            {
                                subjectType = netChange.SubjectType.ToString(),
                                subjectId = netChange.SubjectId,
                                subjectName = netChange.SubjectName,
                            },
                            permissions = new
                            {
                                from = AuditPayloads.PermissionFlags(beforePerm),
                                to = AuditPayloads.PermissionFlags(afterPerm),
                            },
                        }, log);
                }
            }

            log.LogInformation($"Subject {actorType} '{actorId}' applied {addCount} adds and {removeCount} removes to '{secretId}'.");

            // ApplyOrphanRuleAsync self-gates on Features.UseAccessGiveUp and no-ops when off.
            await this.orphanedSecretManager.ApplyOrphanRuleAsync(secretId, this.dbContext);

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
