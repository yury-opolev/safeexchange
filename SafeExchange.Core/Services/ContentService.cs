/// <summary>
/// ContentService
/// </summary>

namespace SafeExchange.Core.Services
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Crypto;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using System;
    using System.Linq;
    using System.Net;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;

    /// <summary>
    /// Content items of a secret: create, update, drop (clear the data), delete, and commit (finish a
    /// hashed-mode upload). Commit lives here, not in <see cref="ChunkService"/>, because it is a content
    /// state change that needs no blob storage.
    /// </summary>
    public sealed class ContentService
    {
        private const int MaxContentTypeLength = 255;

        private static readonly Regex HexRegex = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

        private readonly Features features;

        private readonly SafeExchangeDbContext dbContext;

        private readonly IPurger purger;

        private readonly IPermissionsManager permissionsManager;

        private readonly IAuditWriter auditWriter;

        private readonly AccessTicketManager accessTickets;

        private readonly ILogger log;

        public ContentService(IConfiguration configuration, SafeExchangeDbContext dbContext, IPurger purger, IPermissionsManager permissionsManager, IAuditWriter auditWriter, ILogger<ContentService> log)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            this.features = new Features();
            configuration.GetSection("Features").Bind(this.features);

            var accessTicketConfiguration = new AccessTicketConfiguration();
            configuration.GetSection("AccessTickets").Bind(accessTicketConfiguration);

            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.purger = purger ?? throw new ArgumentNullException(nameof(purger));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
            this.auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.accessTickets = new AccessTicketManager(accessTicketConfiguration, this.dbContext, this.purger, this.log);
        }

        /// <summary>
        /// Adds a content item. <paramref name="contentId"/> must be empty (REST passes its route value);
        /// <paramref name="input"/> is <c>null</c> when the host could not read it.
        /// </summary>
        public async Task<ServiceResult<ContentMetadataOutput>> CreateAsync(Caller caller, string secretId, string contentId, ContentMetadataCreationInput? input)
        {
            ArgumentNullException.ThrowIfNull(caller);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync<ServiceResult<ContentMetadataOutput>>(async () =>
            {
                var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
                if (existingMetadata == null)
                {
                    this.log.LogInformation($"Cannot create content for secret '{secretId}', as secret not exists.");
                    return ServiceResult.NotFound($"Secret '{secretId}' does not exist.");
                }

                if (!string.IsNullOrEmpty(contentId))
                {
                    this.log.LogInformation($"Cannot create content for secret '{secretId}' with specified name.");
                    return ServiceResult.BadRequest("Cannot specify content name on creation.");
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Write)))
                {
                    return ServiceResult.InsufficientPermissions(PermissionType.Write, secretId);
                }

                if ((input == null) || string.IsNullOrEmpty(input.ContentType))
                {
                    this.log.LogInformation($"Content settings for '{secretId}' not provided.");
                    return ServiceResult.BadRequest("Content settings are not provided.");
                }

                if (!IsAcceptableContentType(input.ContentType))
                {
                    this.log.LogInformation($"Content type for '{secretId}' new content is in incorrect format.");
                    return ServiceResult.BadRequest("Content type is in incorrect format.");
                }

                var newContent = new ContentMetadata(input);
                existingMetadata.Content.Add(newContent);
                existingMetadata.LastAccessedAt = DateTimeProvider.UtcNow;
                await this.dbContext.SaveChangesAsync();

                this.log.LogInformation($"Created content meta for '{secretId}': {newContent.ContentName} ({(newContent.IsMain ? "main" : "not main")}), status: {newContent.Status}.");

                return ServiceResult<ContentMetadataOutput>.Ok(newContent.ToDto());
            });
        }

        /// <summary>
        /// Updates a content item's content type and file name. <paramref name="input"/> is <c>null</c> when
        /// the host could not read it. An unknown <paramref name="contentId"/> throws (REST: 500), as before.
        /// </summary>
        public async Task<ServiceResult<ContentMetadataOutput>> UpdateAsync(Caller caller, string secretId, string contentId, ContentMetadataUpdateInput? input)
        {
            ArgumentNullException.ThrowIfNull(caller);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync<ServiceResult<ContentMetadataOutput>>(async () =>
            {
                var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
                if (existingMetadata == null)
                {
                    this.log.LogInformation($"Cannot update content for secret '{secretId}', as secret not exists.");
                    return ServiceResult.NotFound($"Secret '{secretId}' does not exist.");
                }

                if (!existingMetadata.KeepInStorage)
                {
                    this.log.LogInformation($"The data is not kept in storage, cannot use this api.");
                    return ServiceResult.Unprocessable("Cannot use this endpoint for previous versions data.");
                }

                if (string.IsNullOrEmpty(contentId))
                {
                    this.log.LogInformation($"Cannot update content for secret '{secretId}' without specified name.");
                    return ServiceResult.BadRequest("Must specify content name on update.");
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Write)))
                {
                    return ServiceResult.InsufficientPermissions(PermissionType.Write, secretId);
                }

                if ((input == null) || string.IsNullOrEmpty(input.ContentType))
                {
                    this.log.LogInformation($"Content settings for '{secretId}' content '{contentId}' not provided.");
                    return ServiceResult.BadRequest("Content settings are not provided.");
                }

                if (!IsAcceptableContentType(input.ContentType))
                {
                    this.log.LogInformation($"Content type for '{secretId}' content '{contentId}' is in incorrect format.");
                    return ServiceResult.BadRequest("Content type is in incorrect format.");
                }

                var existingContent = existingMetadata.Content.First(c => c.ContentName.Equals(contentId));
                existingContent.ContentType = input.ContentType;
                existingContent.FileName = input.FileName ?? string.Empty;
                existingMetadata.LastAccessedAt = DateTimeProvider.UtcNow;
                await this.dbContext.SaveChangesAsync();

                this.log.LogInformation($"Updated content meta for '{secretId}': {existingContent.ContentName} ({(existingContent.IsMain ? "main" : "not main")}), status: {existingContent.Status}.");

                return ServiceResult<ContentMetadataOutput>.Ok(existingContent.ToDto());
            });
        }

        /// <summary>
        /// Deletes all uploaded data of a content item and keeps the (now blank) item.
        /// </summary>
        public async Task<ServiceResult<ContentMetadataOutput>> DropAsync(Caller caller, string secretId, string contentId)
        {
            ArgumentNullException.ThrowIfNull(caller);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync<ServiceResult<ContentMetadataOutput>>(async () =>
            {
                var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
                if (existingMetadata == null)
                {
                    this.log.LogInformation($"Cannot drop content for secret '{secretId}', as secret not exists.");
                    return ServiceResult.NotFound($"Secret '{secretId}' does not exist.");
                }

                if (!existingMetadata.KeepInStorage)
                {
                    this.log.LogInformation($"The data is not kept in storage, cannot use this api.");
                    return ServiceResult.Unprocessable("Cannot use this endpoint for previous versions data.");
                }

                if (string.IsNullOrEmpty(contentId))
                {
                    this.log.LogInformation($"Cannot drop content for secret '{secretId}' without specified name.");
                    return ServiceResult.BadRequest("Must specify content name on drop.");
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Write)))
                {
                    return ServiceResult.InsufficientPermissions(PermissionType.Write, secretId);
                }

                var existingContent = existingMetadata.Content.First(c => c.ContentName.Equals(contentId));
                var existingAccessTicket = await this.accessTickets.TryGetAccessTicketAsync(existingContent);
                if (!string.IsNullOrEmpty(existingAccessTicket))
                {
                    this.log.LogInformation($"Content '{contentId}' for secret '{secretId}' status is being changed by other user, cannot drop.");
                    return ServiceResult.Unprocessable("Content is being updated.");
                }

                await this.DeleteAllChunksAsync(existingMetadata, existingContent, false);

                this.log.LogInformation($"Dropped content meta for '{secretId}': {existingContent.ContentName} ({(existingContent.IsMain ? "main" : "not main")}), status: {existingContent.Status}.");

                return ServiceResult<ContentMetadataOutput>.Ok(existingContent.ToDto());
            });
        }

        /// <summary>
        /// Deletes a (non-main) content item and its data.
        /// </summary>
        public async Task<ServiceResult> DeleteAsync(Caller caller, string secretId, string contentId)
        {
            ArgumentNullException.ThrowIfNull(caller);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync(async () =>
            {
                var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
                if (existingMetadata == null)
                {
                    this.log.LogInformation($"Cannot delete content for secret '{secretId}', as secret not exists.");
                    return ServiceResult.NotFound($"Secret '{secretId}' does not exist.");
                }

                if (!existingMetadata.KeepInStorage)
                {
                    this.log.LogInformation($"The data is not kept in storage, cannot use this api.");
                    return ServiceResult.Unprocessable("Cannot use this endpoint for previous versions data.");
                }

                if (string.IsNullOrEmpty(contentId))
                {
                    this.log.LogInformation($"Cannot delete content for secret '{secretId}' without specified name.");
                    return ServiceResult.BadRequest("Must specify content name on deletion.");
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Write)))
                {
                    return ServiceResult.InsufficientPermissions(PermissionType.Write, secretId);
                }

                var existingContent = existingMetadata.Content.First(c => c.ContentName.Equals(contentId));

                if (existingContent.IsMain)
                {
                    this.log.LogInformation($"Cannot delete main content for secret '{secretId}'.");
                    return ServiceResult.BadRequest("Cannot delete main content.");
                }

                var existingAccessTicket = await this.accessTickets.TryGetAccessTicketAsync(existingContent);
                if (!string.IsNullOrEmpty(existingAccessTicket))
                {
                    this.log.LogInformation($"Content '{contentId}' for secret '{secretId}' status is being updated, cannot delete.");
                    return ServiceResult.Unprocessable("Content is being updated.");
                }

                await this.DeleteAllChunksAsync(existingMetadata, existingContent, true);

                this.log.LogInformation($"Deleted content meta for '{secretId}': {existingContent.ContentName} ({(existingContent.IsMain ? "main" : "not main")}), status: {existingContent.Status}.");

                return ServiceResult.Ok();
            });
        }

        /// <summary>
        /// Finishes a hashed-mode upload: checks the upload lock (<paramref name="accessTicket"/>) and the
        /// client's whole-content hash against the server's running hash, then marks the content ready.
        /// </summary>
        public async Task<ServiceResult<ContentCommitOutput>> CommitAsync(Caller caller, string secretId, string contentId, string accessTicket, ContentCommitInput input)
        {
            ArgumentNullException.ThrowIfNull(caller);
            ArgumentNullException.ThrowIfNull(input);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync<ServiceResult<ContentCommitOutput>>(async () =>
            {
                var existingMetadata = await this.dbContext.Objects.FindAsync(secretId);
                if (existingMetadata is null)
                {
                    return ServiceResult.NotFound($"Secret '{secretId}' does not exist.");
                }

                if (!existingMetadata.KeepInStorage)
                {
                    return ServiceResult.Unprocessable("Cannot commit data for previous-version secrets.");
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Write)))
                {
                    return ServiceResult.InsufficientPermissions(PermissionType.Write, secretId);
                }

                var existingContent = existingMetadata.Content.FirstOrDefault(c => c.ContentName.Equals(contentId));
                if (existingContent is null)
                {
                    return ServiceResult.NotFound($"Content '{contentId}' does not exist.");
                }

                if (existingContent.IsMain)
                {
                    return ServiceResult.Unprocessable("Main content does not support explicit commit.");
                }

                // Deliberately no expiry handling here: the lock is compared as stored.
                if (string.IsNullOrEmpty(accessTicket) || !accessTicket.Equals(existingContent.AccessTicket))
                {
                    return ServiceResult.Unauthorized("Access ticket missing or invalid.");
                }

                if (existingContent.RunningHashState is null || existingContent.RunningHashState.Length == 0)
                {
                    return ServiceResult.Failure(ServiceStatus.NoUploadState, HttpStatusCode.UnprocessableEntity, "No hashed-mode upload in progress for this content.");
                }

                if (input.IsMalformed)
                {
                    return ServiceResult.BadRequest("Body is not valid JSON.");
                }

                var clientHash = input.Hash?.ToLowerInvariant() ?? string.Empty;
                if (!HexRegex.IsMatch(clientHash))
                {
                    return ServiceResult.BadRequest("hash must be 64 hex characters.");
                }

                var running = SerializableSha256.Restore(existingContent.RunningHashState);
                var serverHash = Convert.ToHexString(running.Finish()).ToLowerInvariant();

                if (!serverHash.Equals(clientHash))
                {
                    this.log.LogWarning("Commit hash mismatch for '{ContentId}': client {Client}, server {Server}", contentId, clientHash, serverHash);
                    return ServiceResult.Failure(
                        ServiceStatus.HashMismatch, HttpStatusCode.UnprocessableEntity,
                        "Client-asserted whole-content hash does not match server computation.",
                        detail: new ChunkHashMismatch { Expected = clientHash, Actual = serverHash });
                }

                existingContent.Hash = serverHash;
                existingContent.Status = ContentStatus.Ready;
                existingContent.AccessTicket = string.Empty;
                existingContent.AccessTicketSetAt = DateTime.MinValue;
                existingContent.RunningHashState = null;
                existingMetadata.LastAccessedAt = DateTimeProvider.UtcNow;
                await this.dbContext.SaveChangesAsync();

                this.log.LogInformation(
                    "Content commit verified for '{ContentId}' across {ChunkCount} chunk(s) (hash {Hash})",
                    contentId, existingContent.Chunks.Count, serverHash);

                // Audit payload intentionally omits serverHash (plaintext-derived integrity hash).
                // Only opaque identifiers — fileName/contentType — are surfaced.
                await this.auditWriter.AppendAsync(
                    existingMetadata, SecretAuditEventType.ContentCommitted,
                    caller.SubjectType, caller.SubjectId,
                    new { contentId, fileName = existingContent.FileName, contentType = existingContent.ContentType }, this.log);

                return ServiceResult<ContentCommitOutput>.Ok(new ContentCommitOutput { ContentName = contentId, Hash = serverHash });
            });
        }

        private async Task DeleteAllChunksAsync(ObjectMetadata metadata, ContentMetadata content, bool removeContent)
        {
            this.log.LogInformation($"Deleting all chunks for content '{content.ContentName}', {content.Chunks.Count} chunks. {(removeContent ? "Remove content." : "Do not remove content.")}");

            try
            {
                content.Status = ContentStatus.Updating;

                this.log.LogInformation($"Setting access ticket to '{content.ContentName}'.");
                content.AccessTicket = AccessTicketManager.NewTicket();
                content.AccessTicketSetAt = DateTimeProvider.UtcNow;

                await this.dbContext.SaveChangesAsync();

                await this.purger.DeleteContentDataAsync(content);
            }
            catch (Exception exception)
            {
                this.log.LogError(exception, $"Exception inside exclusive operation on content '{content.ContentName}'.");
            }
            finally
            {
                content.Chunks.Clear();
                content.Status = ContentStatus.Blank;
                content.RunningHashState = null;

                this.log.LogInformation($"Clearing access ticket from '{content.ContentName}'.");
                content.AccessTicket = string.Empty;
                content.AccessTicketSetAt = DateTime.MinValue;

                if (removeContent)
                {
                    metadata.Content.Remove(content);
                }

                metadata.LastAccessedAt = DateTimeProvider.UtcNow;

                await this.dbContext.SaveChangesAsync();
            }
        }

        private static bool IsAcceptableContentType(string contentType)
        {
            if (string.IsNullOrEmpty(contentType) || contentType.Length > MaxContentTypeLength)
            {
                return false;
            }

            foreach (var ch in contentType)
            {
                if (char.IsControl(ch))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
