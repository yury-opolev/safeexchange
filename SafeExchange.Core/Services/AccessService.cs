/// <summary>
/// AccessService
/// </summary>

namespace SafeExchange.Core.Services
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Graph;
    using SafeExchange.Core.Groups;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// Who can access a secret: grant, list, revoke and the atomic add/remove patch. The secret lookup and
    /// the permission checks of grant, list and revoke run before the operation part (REST ran them outside
    /// its error handler), so their exceptions propagate unwrapped.
    /// </summary>
    public sealed class AccessService
    {
        private readonly SafeExchangeDbContext dbContext;

        private readonly IGroupsManager groupsManager;

        private readonly IPermissionsManager permissionsManager;

        private readonly IAuditWriter auditWriter;

        private readonly IOrphanedSecretManager orphanedSecretManager;

        private readonly ILogger log;

        public AccessService(
            SafeExchangeDbContext dbContext,
            IGroupsManager groupsManager,
            IPermissionsManager permissionsManager,
            IAuditWriter auditWriter,
            IOrphanedSecretManager orphanedSecretManager,
            ILogger<AccessService> log)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.groupsManager = groupsManager ?? throw new ArgumentNullException(nameof(groupsManager));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
            this.auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
            this.orphanedSecretManager = orphanedSecretManager ?? throw new ArgumentNullException(nameof(orphanedSecretManager));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// Grants each entry's permissions (OR-merged into existing ones). <c>RevokeAccess</c> is only passed
        /// on when the caller holds it. <paramref name="permissions"/> is <c>null</c> when the host could not read it.
        /// </summary>
        public async Task<ServiceResult> GrantAsync(Caller caller, string secretId, IReadOnlyList<SubjectPermissionsInput>? permissions)
        {
            ArgumentNullException.ThrowIfNull(caller);
            var secret = await this.FindSecretAsync(secretId);
            if (secret is null)
            {
                return NotFound(secretId);
            }

            if (!await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.GrantAccess))
            {
                return await this.InsufficientPermissionsAsync(caller, PermissionType.GrantAccess, secretId);
            }

            var userCanRevokeAccess = await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.RevokeAccess);
            return await ServiceOperation.RunAsync(async () =>
            {
                if ((permissions?.Count ?? 0) == 0)
                {
                    this.log.LogInformation($"Permissions data for '{secret.ObjectName}' not provided.");
                    return InvalidInput("Access settings are not provided.");
                }

                foreach (var permissionInput in permissions!)
                {
                    await this.ApplyGrantAsync(secret, permissionInput, userCanRevokeAccess, caller);
                }

                await this.dbContext.SaveChangesAsync();
                return ServiceResult.Ok();
            });
        }

        /// <summary>
        /// The secret's access list with the caller's effective permissions (REST v3).
        /// </summary>
        public async Task<ServiceResult<AccessListOutput>> ListAsync(Caller caller, string secretId)
        {
            ArgumentNullException.ThrowIfNull(caller);
            var secret = await this.FindSecretAsync(secretId);
            if (secret is null)
            {
                return NotFound(secretId);
            }

            if (!await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Read))
            {
                return await this.InsufficientPermissionsAsync(caller, PermissionType.Read, secretId);
            }

            return await ServiceOperation.RunAsync(async () =>
            {
                var objectName = secret.ObjectName;
                var existingPermissions = await this.dbContext.Permissions.Where(p => p.SecretName.Equals(objectName)).ToListAsync();
                var effective = await this.permissionsManager.GetEffectivePermissionsAsync(caller.SubjectType, caller.SubjectId, objectName);

                return ServiceResult<AccessListOutput>.Ok(new AccessListOutput
                {
                    AccessList = existingPermissions.Select(p => p.ToDto()).ToList(),
                    CallerEffectivePermissions = EffectivePermissionsOutput.FromPermissionType(effective),
                });
            });
        }

        /// <summary>
        /// The secret's access list (REST v2).
        /// </summary>
        public async Task<ServiceResult<List<SubjectPermissionsOutput>>> ListV2Async(Caller caller, string secretId)
        {
            ArgumentNullException.ThrowIfNull(caller);
            var secret = await this.FindSecretAsync(secretId);
            if (secret is null)
            {
                return NotFound(secretId);
            }

            if (!await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Read))
            {
                return await this.InsufficientPermissionsAsync(caller, PermissionType.Read, secretId);
            }

            return await ServiceOperation.RunAsync(async () =>
            {
                var objectName = secret.ObjectName;
                var existingPermissions = await this.dbContext.Permissions.Where(p => p.SecretName.Equals(objectName)).ToListAsync();
                return ServiceResult<List<SubjectPermissionsOutput>>.Ok(existingPermissions.Select(p => p.ToDto()).ToList());
            });
        }

        /// <summary>
        /// Removes each entry's permissions, then applies the orphan rule. <paramref name="permissions"/> is
        /// <c>null</c> when the host could not read it.
        /// </summary>
        public async Task<ServiceResult> RevokeAsync(Caller caller, string secretId, IReadOnlyList<SubjectPermissionsInput>? permissions)
        {
            ArgumentNullException.ThrowIfNull(caller);
            var secret = await this.FindSecretAsync(secretId);
            if (secret is null)
            {
                return NotFound(secretId);
            }

            if (!await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.RevokeAccess))
            {
                return await this.InsufficientPermissionsAsync(caller, PermissionType.RevokeAccess, secretId);
            }

            return await ServiceOperation.RunAsync(async () =>
            {
                if ((permissions?.Count ?? 0) == 0)
                {
                    this.log.LogInformation($"Permissions data for '{secret.ObjectName}' not provided.");
                    return InvalidInput("Access settings are not provided.");
                }

                foreach (var permissionInput in permissions!)
                {
                    await this.ApplyRevokeAsync(secret, permissionInput, caller);
                }

                // ApplyOrphanRuleAsync self-gates on Features.UseAccessGiveUp and no-ops when off.
                await this.orphanedSecretManager.ApplyOrphanRuleAsync(secret.ObjectName, this.dbContext);

                await this.dbContext.SaveChangesAsync();
                return ServiceResult.Ok();
            });
        }

        /// <summary>
        /// Applies adds and removes as one net change per subject. Adds need <c>GrantAccess</c>, removes need
        /// <c>RevokeAccess</c>. <paramref name="input"/> is <c>null</c> when the host could not read it.
        /// </summary>
        public async Task<ServiceResult> PatchAsync(Caller caller, string secretId, AccessUpdateInput? input)
        {
            ArgumentNullException.ThrowIfNull(caller);
            var secret = await this.FindSecretAsync(secretId);
            if (secret is null)
            {
                return NotFound(secretId);
            }

            return await ServiceOperation.RunAsync(() => this.PatchSecretAccessAsync(secret, caller, input));
        }

        private static ServiceResult NotFound(string secretId)
            => ServiceResult.NotFound($"Secret '{secretId}' not exists");

        private static ServiceResult InvalidInput(string error)
            => ServiceResult.Failure(ServiceStatus.Error, HttpStatusCode.BadRequest, error);

        private async Task<ObjectMetadata?> FindSecretAsync(string secretId)
        {
            var existingMetadata = await this.dbContext.Objects.FindAsync(secretId);
            if (existingMetadata == null)
            {
                this.log.LogInformation($"Cannot handle permissions for secret '{secretId}', as it not exists.");
            }

            return existingMetadata;
        }

        private async Task<ServiceResult> InsufficientPermissionsAsync(Caller caller, PermissionType permission, string secretId)
        {
            var consentRequired = await this.permissionsManager.IsConsentRequiredAsync(caller.SubjectId);
            return ServiceResult.InsufficientPermissions(permission, secretId, consentRequired ? GraphDataProvider.ConsentRequiredSubStatus : string.Empty);
        }

        private async Task<ServiceResult> PatchSecretAccessAsync(ObjectMetadata secret, Caller caller, AccessUpdateInput? input)
        {
            var secretId = secret.ObjectName;
            var actorType = caller.SubjectType;
            var actorId = caller.SubjectId;
            var addCount = input?.Add?.Count ?? 0;
            var removeCount = input?.Remove?.Count ?? 0;

            if (addCount == 0 && removeCount == 0)
            {
                return InvalidInput("Access update body must include at least one of 'add' or 'remove'.");
            }

            if (addCount > 0)
            {
                if (!await this.permissionsManager.IsAuthorizedAsync(actorType, actorId, secretId, PermissionType.GrantAccess))
                {
                    return await this.InsufficientPermissionsAsync(caller, PermissionType.GrantAccess, secretId);
                }
            }

            if (removeCount > 0)
            {
                if (!await this.permissionsManager.IsAuthorizedAsync(actorType, actorId, secretId, PermissionType.RevokeAccess))
                {
                    return await this.InsufficientPermissionsAsync(caller, PermissionType.RevokeAccess, secretId);
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

            foreach (var rem in input!.Remove ?? new List<SubjectPermissionsInput>())
            {
                var targetSubjectType = rem.SubjectType.ToModel();
                var (targetSubjectId, targetSubjectName, _) = await this.ResolveTargetAndBeforeAsync(secretId, targetSubjectType, rem);
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

                var resolved = await this.ResolveAddTargetAsync(targetSubjectType, add, actorType, actorId);
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

                    await this.AppendPermissionEventAsync(secret, eventType, caller, netChange.SubjectType, netChange.SubjectId, netChange.SubjectName, beforePerm, afterPerm);
                }
            }

            this.log.LogInformation($"Subject {actorType} '{actorId}' applied {addCount} adds and {removeCount} removes to '{secretId}'.");

            // ApplyOrphanRuleAsync self-gates on Features.UseAccessGiveUp and no-ops when off.
            await this.orphanedSecretManager.ApplyOrphanRuleAsync(secretId, this.dbContext);

            await this.dbContext.SaveChangesAsync();
            return ServiceResult.Ok();
        }

        private async Task ApplyGrantAsync(ObjectMetadata secret, SubjectPermissionsInput permissionInput, bool userCanRevokeAccess, Caller caller)
        {
            var secretId = secret.ObjectName;
            var permission = permissionInput.GetPermissionType();
            if (!userCanRevokeAccess)
            {
                permission &= ~PermissionType.RevokeAccess;
            }

            var targetSubjectType = permissionInput.SubjectType.ToModel();
            var (targetSubjectId, targetSubjectName, beforePerm) = await this.ResolveTargetAndBeforeAsync(secretId, targetSubjectType, permissionInput);

            if (targetSubjectType.Equals(SubjectType.Group))
            {
                await this.GrantAccessToGroupAsync(secretId, permissionInput, permission, caller.SubjectType, caller.SubjectId);
            }
            else
            {
                this.log.LogInformation($"Setting permissions for '{secretId}': '{targetSubjectType} {permissionInput.SubjectName}' -> '{permission}'");
                await this.permissionsManager.SetPermissionAsync(targetSubjectType, permissionInput.SubjectName, permissionInput.SubjectName, secretId, permission);
            }

            if (secret.AuditEnabled && !string.IsNullOrEmpty(targetSubjectId))
            {
                // SetPermissionAsync OR-merges into existing flags; report the resulting
                // effective permission as `to`, not the requested delta.
                var afterPerm = beforePerm | permission;
                await this.AppendPermissionEventAsync(secret, SecretAuditEventType.PermissionGranted, caller, targetSubjectType, targetSubjectId, targetSubjectName, beforePerm, afterPerm);
            }
        }

        private async Task ApplyRevokeAsync(ObjectMetadata secret, SubjectPermissionsInput permissionInput, Caller caller)
        {
            var secretId = secret.ObjectName;
            var permission = permissionInput.GetPermissionType();
            var targetSubjectType = permissionInput.SubjectType.ToModel();
            var (targetSubjectId, targetSubjectName, beforePerm) = await this.ResolveTargetAndBeforeAsync(secretId, targetSubjectType, permissionInput);

            // For groups, the row is keyed by GroupId; for users/applications subjectId == subjectName.
            // ResolveTargetAndBeforeAsync canonicalises that, so revoke uses the same lookup as grant.
            var unsetKey = string.IsNullOrEmpty(targetSubjectId) ? permissionInput.SubjectName : targetSubjectId;
            this.log.LogInformation($"Unsetting permissions for '{secretId}': '{targetSubjectType} {permissionInput.SubjectName}' -> '{permission}'");
            await this.permissionsManager.UnsetPermissionAsync(targetSubjectType, unsetKey, secretId, permission);

            if (secret.AuditEnabled && !string.IsNullOrEmpty(targetSubjectId))
            {
                var afterPerm = beforePerm & ~permission;
                await this.AppendPermissionEventAsync(secret, SecretAuditEventType.PermissionRevoked, caller, targetSubjectType, targetSubjectId, targetSubjectName, beforePerm, afterPerm);
            }
        }

        private ValueTask AppendPermissionEventAsync(
            ObjectMetadata secret, SecretAuditEventType eventType, Caller caller,
            SubjectType targetSubjectType, string targetSubjectId, string targetSubjectName,
            PermissionType beforePerm, PermissionType afterPerm)
            => this.auditWriter.AppendAsync(
                secret, eventType,
                caller.SubjectType, caller.SubjectId,
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
                }, this.log, AuditOrigin.FromCaller(caller));

        private async Task<(string subjectId, string subjectName, PermissionType beforePerm)> ResolveTargetAndBeforeAsync(
            string secretId, SubjectType targetSubjectType, SubjectPermissionsInput input)
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
            SubjectType targetSubjectType, SubjectPermissionsInput add, SubjectType actorType, string actorId)
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

        private async Task GrantAccessToGroupAsync(string secretId, SubjectPermissionsInput permissionInput, PermissionType permission, SubjectType actorType, string actorId)
        {
            if (Guid.TryParse(permissionInput.SubjectId, out _))
            {
                await this.EnsureGroupExistsAsync(permissionInput, actorType, actorId);

                this.log.LogInformation($"Setting permissions for '{secretId}': group '{permissionInput.SubjectName}' ({permissionInput.SubjectId}) -> '{permission}'");
                await this.permissionsManager.SetPermissionAsync(SubjectType.Group, permissionInput.SubjectId, permissionInput.SubjectName, secretId, permission);
                return;
            }

            var existingGroup = await this.groupsManager.TryFindGroupByMailAsync(permissionInput.SubjectName);
            if (existingGroup == default)
            {
                return;
            }

            this.log.LogInformation($"Setting permissions for '{secretId}': group mail '{permissionInput.SubjectName}', id: '{existingGroup.GroupId}' -> '{permission}'");
            await this.permissionsManager.SetPermissionAsync(SubjectType.Group, existingGroup.GroupId, existingGroup.DisplayName, secretId, permission);
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
