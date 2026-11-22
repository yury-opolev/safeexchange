/// <summary>
/// SecretService
/// </summary>

namespace SafeExchange.Core.Services
{
    using Microsoft.Azure.Cosmos;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Telemetry;
    using SafeExchange.Core.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// Secret metadata: create, read, update, delete and the v2 / v3 secret lists.
    /// </summary>
    public sealed class SecretService
    {
        private readonly Features features;

        private readonly SafeExchangeDbContext dbContext;

        private readonly IPurger purger;

        private readonly IPermissionsManager permissionsManager;

        private readonly IAuditWriter auditWriter;

        private readonly ILogger log;

        public SecretService(IConfiguration configuration, SafeExchangeDbContext dbContext, IPurger purger, IPermissionsManager permissionsManager, IAuditWriter auditWriter, ILogger<SecretService> log)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            this.features = new Features();
            configuration.GetSection("Features").Bind(this.features);

            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.purger = purger ?? throw new ArgumentNullException(nameof(purger));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
            this.auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// The caller's readable secrets with direct and effective permissions (REST v3 list).
        /// </summary>
        public async Task<ServiceResult<List<SecretListItemOutput>>> ListAsync(Caller caller, IEnumerable<string>? tags)
        {
            ArgumentNullException.ThrowIfNull(caller);
            return await ServiceOperation.RunAsync<ServiceResult<List<SecretListItemOutput>>>(async () =>
            {
                var (tagsOk, requiredTags, tagsError) = TagValidator.TryNormalizeList(tags ?? Array.Empty<string>());
                if (!tagsOk)
                {
                    this.log.LogInformation($"Invalid tag filter: {tagsError}.");
                    return ServiceResult.BadRequest(tagsError!);
                }

                var readableSecrets = await this.permissionsManager.GetReadableSecretsAsync(caller.SubjectType, caller.SubjectId);

                var names = readableSecrets.Select(e => e.SecretName).Distinct().ToList();
                var tagMap = await this.LoadTagMapAsync(names, requiredTags);

                if (requiredTags.Count > 0)
                {
                    var matched = new HashSet<string>(tagMap.Keys);
                    readableSecrets = readableSecrets.Where(e => matched.Contains(e.SecretName)).ToList();
                }

                var dtos = readableSecrets.Select(e => new SecretListItemOutput
                {
                    ObjectName = e.SecretName,
                    SubjectType = caller.SubjectType.ToDto(),
                    SubjectName = caller.SubjectId,
                    SubjectId = caller.SubjectId,
                    CanRead = (e.Direct & PermissionType.Read) == PermissionType.Read,
                    CanWrite = (e.Direct & PermissionType.Write) == PermissionType.Write,
                    CanGrantAccess = (e.Direct & PermissionType.GrantAccess) == PermissionType.GrantAccess,
                    CanRevokeAccess = (e.Direct & PermissionType.RevokeAccess) == PermissionType.RevokeAccess,
                    CallerEffectivePermissions = EffectivePermissionsOutput.FromPermissionType(e.Effective),
                    Tags = tagMap.TryGetValue(e.SecretName, out var t) ? t.ToList() : new List<string>(),
                }).ToList();

                return ServiceResult<List<SecretListItemOutput>>.Ok(dtos);
            });
        }

        /// <summary>
        /// The caller's direct read permission rows (REST v2 list).
        /// </summary>
        public async Task<ServiceResult<List<SubjectPermissionsOutput>>> ListV2Async(Caller caller, IEnumerable<string>? tags)
        {
            ArgumentNullException.ThrowIfNull(caller);
            return await ServiceOperation.RunAsync<ServiceResult<List<SubjectPermissionsOutput>>>(async () =>
            {
                var (tagsOk, requiredTags, tagsError) = TagValidator.TryNormalizeList(tags ?? Array.Empty<string>());
                if (!tagsOk)
                {
                    this.log.LogInformation($"Invalid tag filter: {tagsError}.");
                    return ServiceResult.BadRequest(tagsError!);
                }

                var subjectType = caller.SubjectType;
                var subjectId = caller.SubjectId;
                var permissions = await this.dbContext.Permissions
                    .Where(p => p.SubjectType.Equals(subjectType) && p.SubjectId.Equals(subjectId) && p.CanRead)
                    .ToListAsync();

                var names = permissions.Select(p => p.SecretName).Distinct().ToList();
                var tagMap = await this.LoadTagMapAsync(names, requiredTags);

                if (requiredTags.Count > 0)
                {
                    var matched = new HashSet<string>(tagMap.Keys);
                    permissions = permissions.Where(p => matched.Contains(p.SecretName)).ToList();
                }

                var dtos = permissions.Select(p =>
                {
                    var dto = p.ToDto();
                    dto.Tags = tagMap.TryGetValue(p.SecretName, out var t) ? t.ToList() : new List<string>();
                    return dto;
                }).ToList();

                return ServiceResult<List<SubjectPermissionsOutput>>.Ok(dtos);
            });
        }

        /// <summary>
        /// Creates a secret and gives the caller full permissions. <paramref name="input"/> is <c>null</c> when
        /// the host could not read it (answered like missing expiration settings).
        /// </summary>
        public async Task<ServiceResult<ObjectMetadataOutput>> CreateAsync(Caller caller, string secretId, MetadataCreationInput? input)
        {
            ArgumentNullException.ThrowIfNull(caller);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync<ServiceResult<ObjectMetadataOutput>>(async () =>
            {
                var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
                if (existingMetadata != null)
                {
                    this.log.LogInformation($"Cannot create secret '{secretId}', as already exists");
                    return ServiceResult.Conflict($"Secret '{secretId}' already exists.");
                }

                if ((input is null) || input.ExpirationSettings == null)
                {
                    this.log.LogInformation($"Expiration settings for '{secretId}' not provided.");
                    return ServiceResult.BadRequest("Expiration settings are not provided.");
                }

                // Creation-time naming contract, mirrored by the UI for immediate feedback.
                // Deliberately not applied to read/update/delete: existing identifiers accepted
                // under earlier behavior stay usable, subject to normal authorization.
                if (!SecretNameValidator.TryValidate(secretId, out var secretIdError))
                {
                    this.log.LogInformation($"Secret id value is not valid: {secretIdError}");
                    return ServiceResult.BadRequest(secretIdError!);
                }

                var (tagsOk, normalisedTags, tagsError) = TagValidator.TryNormalizeList(input.Tags);
                if (!tagsOk)
                {
                    this.log.LogInformation($"Invalid tags for secret '{secretId}': {tagsError}.");
                    return ServiceResult.BadRequest(tagsError!);
                }
                input.Tags = normalisedTags.ToList();

                var createdMetadata = await this.TryClaimNameAsync(secretId, input, caller);
                if (createdMetadata is null)
                {
                    this.log.LogInformation($"Cannot create secret '{secretId}', as already exists");
                    return ServiceResult.Conflict($"Secret '{secretId}' already exists.");
                }

                await this.SetCallerFullPermissionsAsync(secretId, caller);
                this.log.LogInformation($"Metadata for secret '{secretId}' added, full permissions for {caller.SubjectType} '(tid {TelemetryContext.Current})' are set.");

                if (createdMetadata.AuditEnabled)
                {
                    await this.auditWriter.EnsureAnchorAsync(createdMetadata, caller.SubjectId);
                    var creationPayload = new
                    {
                        tags = createdMetadata.Tags ?? new List<string>(),
                        expirationSettings = createdMetadata.ExpirationMetadata.ToDto(),
                        auditEnabled = true,
                    };
                    await this.auditWriter.AppendAsync(
                        createdMetadata, SecretAuditEventType.SecretCreated,
                        caller.SubjectType, caller.SubjectId, creationPayload, this.log, AuditOrigin.FromCaller(caller));
                }

                return ServiceResult<ObjectMetadataOutput>.Ok(createdMetadata.ToDto());
            });
        }

        public async Task<ServiceResult<ObjectMetadataOutput>> GetAsync(Caller caller, string secretId)
        {
            ArgumentNullException.ThrowIfNull(caller);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync<ServiceResult<ObjectMetadataOutput>>(async () =>
            {
                var metadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
                if (metadata == null)
                {
                    this.log.LogInformation($"Cannot get secret '{secretId}', as no metadata exists.");
                    return ServiceResult.NotFound($"Secret '{secretId}' does not exist.");
                }

                if (!metadata.KeepInStorage)
                {
                    this.log.LogInformation($"The data is not kept in storage, cannot use this api.");
                    return ServiceResult.Unprocessable("Cannot use this endpoint for previous versions data.");
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Read)))
                {
                    return ServiceResult.InsufficientPermissions(PermissionType.Read, secretId);
                }

                metadata.LastAccessedAt = DateTimeProvider.UtcNow;
                await this.dbContext.SaveChangesAsync();

                return ServiceResult<ObjectMetadataOutput>.Ok(metadata.ToDto());
            });
        }

        /// <summary>
        /// Updates expiration settings and, when given, tags. <paramref name="input"/> is <c>null</c> when the
        /// host could not read it (answered like missing expiration settings).
        /// </summary>
        public async Task<ServiceResult<ObjectMetadataOutput>> UpdateAsync(Caller caller, string secretId, MetadataUpdateInput? input)
        {
            ArgumentNullException.ThrowIfNull(caller);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync<ServiceResult<ObjectMetadataOutput>>(async () =>
            {
                var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
                if (existingMetadata == null)
                {
                    this.log.LogInformation($"Cannot update secret '{secretId}', as no metadata exists.");
                    return ServiceResult.NotFound($"Secret '{secretId}' does not exist.");
                }

                if (!existingMetadata.KeepInStorage)
                {
                    this.log.LogInformation($"The data is not kept in storage, cannot use this api.");
                    return ServiceResult.Unprocessable("Cannot use this endpoint for previous versions data.");
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Write)))
                {
                    return ServiceResult.InsufficientPermissions(PermissionType.Write, secretId);
                }

                if ((input is null) || input.ExpirationSettings == null)
                {
                    this.log.LogInformation($"Expiration settings for '{secretId}' not provided.");
                    return ServiceResult.BadRequest("Expiration settings are not provided.");
                }

                if (string.IsNullOrEmpty(secretId))
                {
                    this.log.LogInformation("Secret id value is not provided.");
                    return ServiceResult.BadRequest("Secret id value is not provided.");
                }

                if (input.Tags is not null)
                {
                    var (tagsOk, normalisedTags, tagsError) = TagValidator.TryNormalizeList(input.Tags);
                    if (!tagsOk)
                    {
                        this.log.LogInformation($"Invalid tags for secret '{secretId}': {tagsError}.");
                        return ServiceResult.BadRequest(tagsError!);
                    }
                    input.Tags = normalisedTags.ToList();
                }

                // Build the audit diff against the live entity BEFORE UpdateMetadataAsync mutates
                // it in place. MetadataDiffBuilder is the single canonical implementation; do not
                // re-inline diff logic here.
                string? diffJson = null;
                if (existingMetadata.AuditEnabled)
                {
                    diffJson = MetadataDiffBuilder.BuildDiff(existingMetadata, input);
                }

                var updatedMetadata = await this.UpdateMetadataAsync(existingMetadata, input);
                this.log.LogInformation($"{caller.SubjectType} '(tid {TelemetryContext.Current})' updated metadata for secret '{existingMetadata.ObjectName}'.");

                if (diffJson is not null)
                {
                    var diffObj = System.Text.Json.JsonSerializer.Deserialize<object>(diffJson);
                    await this.auditWriter.AppendAsync(
                        updatedMetadata, SecretAuditEventType.SecretMetadataUpdated,
                        caller.SubjectType, caller.SubjectId, diffObj, this.log, AuditOrigin.FromCaller(caller));
                }

                return ServiceResult<ObjectMetadataOutput>.Ok(updatedMetadata.ToDto());
            });
        }

        public async Task<ServiceResult> DeleteAsync(Caller caller, string secretId)
        {
            ArgumentNullException.ThrowIfNull(caller);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync(async () =>
            {
                var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
                if (existingMetadata == null)
                {
                    this.log.LogInformation($"Cannot delete secret '{secretId}', as no metadata exists.");
                    return ServiceResult.NotFound($"Secret '{secretId}' does not exist.");
                }

                if (!existingMetadata.KeepInStorage)
                {
                    this.log.LogInformation($"The data is not kept in storage, cannot use this api.");
                    return ServiceResult.Unprocessable("Cannot use this endpoint for previous versions data.");
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Write)))
                {
                    return ServiceResult.InsufficientPermissions(PermissionType.Write, secretId);
                }

                await this.purger.PurgeAsync(secretId, this.dbContext, this.auditWriter, caller.SubjectType, caller.SubjectId, this.features.AuditRetentionDays, AuditOrigin.FromCaller(caller));
                this.log.LogInformation($"{caller.SubjectType} '(tid {TelemetryContext.Current})' deleted secret '{secretId}'");

                return ServiceResult.Ok();
            });
        }

        private async Task<Dictionary<string, List<string>>> LoadTagMapAsync(List<string> names, IReadOnlyList<string> requiredTags)
        {
            var tagMap = new Dictionary<string, List<string>>();
            if (names.Count == 0)
            {
                return tagMap;
            }

            IQueryable<ObjectMetadata> tagQuery = this.dbContext.Objects
                .Where(o => names.Contains(o.ObjectName));

            foreach (var rt in requiredTags)
            {
                var tag = rt; // local capture so closure binds correctly
                tagQuery = tagQuery.Where(o => o.Tags.Contains(tag));
            }

            var tagPairs = await tagQuery
                .Select(o => new { o.ObjectName, o.Tags })
                .ToListAsync();

            foreach (var pair in tagPairs)
            {
                tagMap[pair.ObjectName] = pair.Tags?.ToList() ?? new List<string>();
            }

            return tagMap;
        }

        // The object and permission rows live in different containers, so a single save is not atomic.
        // The object is saved alone first: a create that loses the name race must not leave a permission row.
        private async Task<ObjectMetadata?> TryClaimNameAsync(string secretId, MetadataCreationInput metadataInput, Caller caller)
        {
            var objectMetadata = new ObjectMetadata(secretId, metadataInput, $"{caller.SubjectType} {caller.SubjectId}");
            var entity = await this.dbContext.Objects.AddAsync(objectMetadata);

            try
            {
                await this.dbContext.SaveChangesAsync();
            }
            catch (DbUpdateException dbUpdateException)
                when (dbUpdateException.InnerException is CosmosException cosmosException &&
                      cosmosException.StatusCode == HttpStatusCode.Conflict)
            {
                this.dbContext.Entry(objectMetadata).State = EntityState.Detached;
                return null;
            }

            return entity.Entity;
        }

        private async Task SetCallerFullPermissionsAsync(string secretId, Caller caller)
        {
            var subjectName = caller.SubjectId;
            await this.permissionsManager.SetPermissionAsync(caller.SubjectType, caller.SubjectId, subjectName, secretId, PermissionType.Full);

            await this.dbContext.SaveChangesAsync();
        }

        private async Task<ObjectMetadata> UpdateMetadataAsync(ObjectMetadata existingMetadata, MetadataUpdateInput metadataInput)
        {
            var updatedExpirationMetadata = new ExpirationMetadata(metadataInput.ExpirationSettings);
            existingMetadata.ExpirationMetadata = updatedExpirationMetadata;
            existingMetadata.LastAccessedAt = DateTimeProvider.UtcNow;

            if (metadataInput.Tags is not null)
            {
                existingMetadata.Tags = metadataInput.Tags.ToList();
            }

            await this.dbContext.SaveChangesAsync();

            return existingMetadata;
        }
    }
}
