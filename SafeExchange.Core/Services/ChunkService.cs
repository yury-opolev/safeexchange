/// <summary>
/// ChunkService
/// </summary>

namespace SafeExchange.Core.Services
{
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Crypto;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Streams;
    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Mime;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Content bytes: chunk upload (legacy and hashed mode), chunk download and whole-content download.
    /// </summary>
    public sealed class ChunkService
    {
        /// <summary>
        /// <see cref="ChunkUploadRequest.OperationType"/> value that keeps a legacy upload open.
        /// </summary>
        public const string InterimOperationType = "interim";

        private const string DefaultContentType = "text/html; charset=utf-8";

        // The lock state to put back when the upload that took the lock fails before anything was saved.
        private sealed record UploadLock(ContentStatus Status, string AccessTicket, DateTime AccessTicketSetAt)
        {
            public void RestoreTo(ContentMetadata content)
            {
                content.Status = this.Status;
                content.AccessTicket = this.AccessTicket;
                content.AccessTicketSetAt = this.AccessTicketSetAt;
            }
        }

        private readonly Features features;

        private readonly SafeExchangeDbContext dbContext;

        private readonly IPurger purger;

        private readonly IBlobHelper blobHelper;

        private readonly IPermissionsManager permissionsManager;

        private readonly IAuditWriter auditWriter;

        private readonly AccessTicketManager accessTickets;

        private readonly ILogger log;

        public ChunkService(IConfiguration configuration, SafeExchangeDbContext dbContext, IPurger purger, IBlobHelper blobHelper, IPermissionsManager permissionsManager, IAuditWriter auditWriter, ILogger<ChunkService> log)
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
            this.blobHelper = blobHelper ?? throw new ArgumentNullException(nameof(blobHelper));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
            this.auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.accessTickets = new AccessTicketManager(accessTicketConfiguration, this.dbContext, this.purger, this.log);
        }

        /// <summary>
        /// Appends <paramref name="data"/> as the next chunk of a content item. The first chunk takes the
        /// upload lock and returns it as <see cref="ChunkCreationOutput.AccessTicket"/>.
        /// </summary>
        public async Task<ServiceResult<ChunkCreationOutput>> UploadChunkAsync(Caller caller, string secretId, string contentId, Stream data, ChunkUploadRequest request)
        {
            ArgumentNullException.ThrowIfNull(caller);
            ArgumentNullException.ThrowIfNull(data);
            ArgumentNullException.ThrowIfNull(request);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync<ServiceResult<ChunkCreationOutput>>(async () =>
            {
                var existingMetadata = await this.dbContext.Objects.FindAsync(secretId);
                if (existingMetadata == null)
                {
                    this.log.LogInformation($"Cannot upload content for secret '{secretId}', as secret not exists.");
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

                var existingContent = existingMetadata.Content.FirstOrDefault(c => c.ContentName.Equals(contentId));
                if (existingContent == null)
                {
                    this.log.LogInformation($"Cannot upload content '{contentId}' for secret '{secretId}', as content not exists.");
                    return ServiceResult.NotFound($"Content '{contentId}' does not exist.");
                }

                if (!string.IsNullOrEmpty(request.ChunkId))
                {
                    this.log.LogInformation($"Cannot upload content for secret '{secretId}' with specified chunk Id.");
                    return ServiceResult.BadRequest($"Cannot specify chunk id on upload.");
                }

                var accessTicket = request.AccessTicket ?? string.Empty;
                var existingAccessTicket = await this.accessTickets.TryGetAccessTicketAsync(existingContent);

                // Chunk 0 stored but its response (carrying the ticket) lost: the writer retries without a ticket.
                // It has write access (checked above) and proves it holds the bytes by their hash, so hand the ticket back.
                if (request.ExpectedIndex == 0
                    && string.IsNullOrEmpty(accessTicket)
                    && !string.IsNullOrEmpty(existingAccessTicket)
                    && !string.IsNullOrEmpty(request.ChunkHash)
                    && existingContent.Chunks.Count == 1
                    && existingContent.Chunks[0].Hash.Equals(request.ChunkHash, StringComparison.OrdinalIgnoreCase))
                {
                    this.log.LogInformation($"Chunk 0 of '{contentId}' is already stored with the same hash, returning its access ticket.");
                    return ServiceResult<ChunkCreationOutput>.Ok(existingContent.Chunks[0].ToCreationDto(existingAccessTicket));
                }

                if (!string.IsNullOrEmpty(existingAccessTicket) && !existingAccessTicket.Equals(accessTicket))
                {
                    this.log.LogInformation($"Cannot upload content for secret '{secretId}', is being changed by other user.");
                    return ServiceResult.Unprocessable("Content is being updated.");
                }

                if (request.ExpectedIndex is int expectedIndex)
                {
                    var chunkCount = existingContent.Chunks.Count;
                    if (expectedIndex >= 0 && expectedIndex < chunkCount
                        && !string.IsNullOrEmpty(request.ChunkHash)
                        && existingContent.Chunks[expectedIndex].Hash.Equals(request.ChunkHash, StringComparison.OrdinalIgnoreCase))
                    {
                        var storedChunk = existingContent.Chunks[expectedIndex];
                        this.log.LogInformation($"Chunk {expectedIndex} of '{contentId}' is already stored with the same hash, nothing to do.");
                        return ServiceResult<ChunkCreationOutput>.Ok(storedChunk.ToCreationDto(accessTicket));
                    }

                    if (expectedIndex != chunkCount)
                    {
                        return ServiceResult.Unprocessable($"Expected chunk index {chunkCount}.");
                    }
                }

                // Main HTML content always flows through the sanitising legacy path —
                // a hash header on main content is ignored (would otherwise bypass sanitisation).
                var hashHeaderPresent = !existingContent.IsMain && !string.IsNullOrEmpty(request.ChunkHash);

                var mode = UploadModeResolver.Resolve(
                    existingContent,
                    hashHeaderPresent: hashHeaderPresent,
                    allowLegacy: this.features.AllowLegacyAttachmentUploads,
                    ignoreHeader: this.features.IgnoreChunkHashHeader || existingContent.IsMain);

                var takesLock = string.IsNullOrEmpty(accessTicket)
                    || (request.ExpectedIndex.HasValue && string.IsNullOrEmpty(existingAccessTicket));

                if (request.ExpectedIndex.HasValue)
                {
                    // Committed content is never re-locked: that would let an expired ticket wipe it.
                    if (takesLock && existingContent.Status == ContentStatus.Ready)
                    {
                        this.log.LogInformation($"Cannot upload content '{contentId}' for secret '{secretId}', as content is already committed.");
                        return ServiceResult.Unprocessable($"Content '{contentId}' is already committed.");
                    }

                    if (mode == UploadMode.Reject)
                    {
                        return InconsistentUploadMode();
                    }
                }

                var lockTaken = default(UploadLock?);
                if (takesLock)
                {
                    // With ExpectedIndex, a caller ticket the content no longer holds (the lock expired and the
                    // upload was wiped) is stale: re-arm the lock and hand back the new ticket.
                    this.log.LogInformation($"Setting access ticket to '{existingContent.ContentName}'.");
                    accessTicket = AccessTicketManager.NewTicket();

                    lockTaken = new UploadLock(existingContent.Status, existingContent.AccessTicket, existingContent.AccessTicketSetAt);
                    existingContent.Status = ContentStatus.Updating;
                    existingContent.AccessTicket = accessTicket;
                    existingContent.AccessTicketSetAt = DateTimeProvider.UtcNow;
                }

                var newChunkName = $"{existingContent.ContentName}-{(existingContent.Chunks.Count):00000000}";

                if (mode == UploadMode.Reject)
                {
                    // Kept for the REST wire: a rejected upload without ExpectedIndex leaves the lock it took.
                    await this.dbContext.SaveChangesAsync();
                    return InconsistentUploadMode();
                }

                ServiceResult<ChunkCreationOutput> result;
                try
                {
                    result = mode == UploadMode.Hashed
                        ? await this.UploadHashedChunkAsync(
                            caller, data, existingContent, newChunkName, existingMetadata,
                            request.ChunkHash!, request.DeclaredLength, accessTicket)
                        : await this.UploadLegacyChunkAsync(
                            caller, data, existingContent, newChunkName, existingMetadata,
                            request.OperationType ?? string.Empty, accessTicket);
                }
                catch
                {
                    lockTaken?.RestoreTo(existingContent);
                    throw;
                }

                if (!result.IsOk)
                {
                    lockTaken?.RestoreTo(existingContent);
                }

                return result;
            });
        }

        /// <summary>
        /// Downloads one chunk of a ready content item. The bytes are read, and the read audited, when the
        /// returned <see cref="ContentDownload"/> is written.
        /// </summary>
        public async Task<ServiceResult<ContentDownload>> DownloadChunkAsync(Caller caller, string secretId, string contentId, string chunkId)
        {
            ArgumentNullException.ThrowIfNull(caller);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync<ServiceResult<ContentDownload>>(async () =>
            {
                var (failure, existingMetadata, existingContent) = await this.FindReadableContentAsync(caller, secretId, contentId, $"Content '{contentId}' does not exist.");
                if (failure is not null)
                {
                    return failure;
                }

                if (string.IsNullOrEmpty(chunkId))
                {
                    this.log.LogInformation($"Cannot download content for secret '{secretId}' without specified chunk Id.");
                    return ServiceResult.BadRequest($"Must specify chunk id on download.");
                }

                var existingChunk = existingContent!.Chunks.FirstOrDefault(c => c.ChunkName.Equals(chunkId));
                if (existingChunk == null)
                {
                    this.log.LogInformation($"Cannot download content for secret '{secretId}', as chunk '{chunkId}' for content '{contentId}' not exists.");
                    return ServiceResult.NotFound($"Chunk '{chunkId}' does not exist.");
                }

                var download = new ContentDownload(
                    ContentTypeOf(existingContent),
                    existingChunk.Length,
                    null,
                    (destination, cancellationToken) => ServiceOperation.RunAsync(async () =>
                    {
                        var dataStream = await this.blobHelper.DownloadAndDecryptBlobAsync(existingChunk.ChunkName);
                        await dataStream.CopyToAsync(destination, cancellationToken);

                        existingMetadata!.LastAccessedAt = DateTimeProvider.UtcNow;
                        await this.dbContext.SaveChangesAsync(cancellationToken);

                        await this.EmitContentReadAsync(existingMetadata, existingContent.ContentName, existingChunk.ChunkName, caller);
                    }));

                return ServiceResult<ContentDownload>.Ok(download);
            });
        }

        /// <summary>
        /// Downloads a ready content item as one stream (all chunks in order, one audited read per chunk).
        /// </summary>
        public async Task<ServiceResult<ContentDownload>> DownloadContentAsync(Caller caller, string secretId, string contentId)
        {
            ArgumentNullException.ThrowIfNull(caller);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync<ServiceResult<ContentDownload>>(async () =>
            {
                var (failure, existingMetadata, existingContent) = await this.FindReadableContentAsync(caller, secretId, contentId, $"Content '{contentId}' not exists.");
                if (failure is not null)
                {
                    return failure;
                }

                var contentLength = 0L;
                foreach (var chunk in existingContent!.Chunks)
                {
                    contentLength += chunk.Length;
                }

                var download = new ContentDownload(
                    ContentTypeOf(existingContent),
                    contentLength,
                    new ContentDisposition { FileName = existingContent.FileName }.ToString(),
                    (destination, cancellationToken) => ServiceOperation.RunAsync(async () =>
                    {
                        foreach (var chunk in existingContent.Chunks)
                        {
                            var chunkStream = await this.blobHelper.DownloadAndDecryptBlobAsync(chunk.ChunkName);
                            await chunkStream.CopyToAsync(destination, cancellationToken);
                            await this.EmitContentReadAsync(existingMetadata!, existingContent.ContentName, chunk.ChunkName, caller);
                        }

                        existingMetadata!.LastAccessedAt = DateTimeProvider.UtcNow;
                        await this.dbContext.SaveChangesAsync(cancellationToken);
                    }));

                return ServiceResult<ContentDownload>.Ok(download);
            });
        }

        private static string ContentTypeOf(ContentMetadata content)
            => string.IsNullOrEmpty(content.ContentType) ? DefaultContentType : content.ContentType;

        private static ServiceResult InconsistentUploadMode()
            => ServiceResult.Failure(
                ServiceStatus.InconsistentUploadMode, HttpStatusCode.BadRequest,
                "Chunk-hash header usage is inconsistent with the content's upload mode.");

        // Shared download checks, in today's order; only the content-missing message differs per endpoint.
        private async Task<(ServiceResult? Failure, ObjectMetadata? Metadata, ContentMetadata? Content)> FindReadableContentAsync(
            Caller caller, string secretId, string contentId, string contentMissingError)
        {
            var existingMetadata = await this.dbContext.Objects.FindAsync(secretId);
            if (existingMetadata == null)
            {
                this.log.LogInformation($"Cannot download content for secret '{secretId}', as secret not exists.");
                return (ServiceResult.NotFound($"Secret '{secretId}' does not exist."), null, null);
            }

            if (!existingMetadata.KeepInStorage)
            {
                this.log.LogInformation($"The data is not kept in storage, cannot use this api.");
                return (ServiceResult.Unprocessable("Cannot use this endpoint for previous versions data."), null, null);
            }

            if (!(await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Read)))
            {
                return (ServiceResult.InsufficientPermissions(PermissionType.Read, secretId), null, null);
            }

            var existingContent = existingMetadata.Content.FirstOrDefault(c => c.ContentName.Equals(contentId));
            if (existingContent == null)
            {
                this.log.LogInformation($"Cannot download content '{contentId}' for secret '{secretId}', as content not exists.");
                return (ServiceResult.NotFound(contentMissingError), null, null);
            }

            var existingAccessTicket = await this.accessTickets.TryGetAccessTicketAsync(existingContent);
            if (!string.IsNullOrEmpty(existingAccessTicket))
            {
                this.log.LogInformation($"Cannot download content '{contentId}' for secret '{secretId}', as content is being updated by other user.");
                return (ServiceResult.Unprocessable($"Content '{contentId}' is being updated."), null, null);
            }

            if (existingContent.Status != ContentStatus.Ready)
            {
                this.log.LogInformation($"Cannot download content '{contentId}' for secret '{secretId}', as content is not ready.");
                return (ServiceResult.Unprocessable($"Content '{contentId}' is not ready."), null, null);
            }

            return (null, existingMetadata, existingContent);
        }

        private async Task<ServiceResult<ChunkCreationOutput>> UploadLegacyChunkAsync(
            Caller caller,
            Stream dataStream,
            ContentMetadata existingContent,
            string newChunkName,
            ObjectMetadata existingMetadata,
            string operationType,
            string accessTicket)
        {
            var dataLength = 0L;
            if (existingContent.IsMain)
            {
                dataLength = await this.UploadMainContentAsync(newChunkName, dataStream);
            }
            else
            {
                await this.blobHelper.EncryptAndUploadBlobAsync(newChunkName, dataStream);

                try
                {
                    dataLength = dataStream.Length;
                }
                catch (NotSupportedException exception)
                {
                    this.log.LogWarning(exception, $"Cannot get content length for '{existingContent.ContentName}': '{newChunkName}'.");
                }
            }

            var newChunk = new ChunkMetadata()
            {
                ChunkName = newChunkName,
                Hash = string.Empty,
                Length = dataLength,
            };

            existingContent.Chunks.Add(newChunk);

            if (InterimOperationType.Equals(operationType, StringComparison.InvariantCultureIgnoreCase))
            {
                existingContent.AccessTicketSetAt = DateTimeProvider.UtcNow;
            }
            else
            {
                this.log.LogInformation($"Clearing access ticket from '{existingContent.ContentName}'.");
                accessTicket = string.Empty;
                existingContent.Status = ContentStatus.Ready;
                existingContent.AccessTicket = string.Empty;
                existingContent.AccessTicketSetAt = DateTime.MinValue;
            }

            existingMetadata.LastAccessedAt = DateTimeProvider.UtcNow;
            await this.dbContext.SaveChangesAsync();

            await this.EmitContentWrittenAsync(existingMetadata, existingContent.ContentName, newChunkName, caller);

            return ServiceResult<ChunkCreationOutput>.Ok(newChunk.ToCreationDto(accessTicket));
        }

        private async Task<ServiceResult<ChunkCreationOutput>> UploadHashedChunkAsync(
            Caller caller,
            Stream data,
            ContentMetadata existingContent,
            string newChunkName,
            ObjectMetadata existingMetadata,
            string headerHash,
            long? declaredLength,
            string accessTicket)
        {
            var running = existingContent.RunningHashState is { Length: > 0 }
                ? SerializableSha256.Restore(existingContent.RunningHashState)
                : new SerializableSha256();

            using var chunkHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long bytesRead;
            using (var tee = new HashingReadStream(data, chunkHasher, running))
            {
                await this.blobHelper.EncryptAndUploadBlobAsync(newChunkName, tee);
                bytesRead = tee.BytesRead;
            }

            var dataLength = declaredLength ?? bytesRead;
            var serverHash = Convert.ToHexString(chunkHasher.GetHashAndReset()).ToLowerInvariant();
            if (!serverHash.Equals(headerHash, StringComparison.OrdinalIgnoreCase))
            {
                this.log.LogWarning(
                    "Chunk hash mismatch on {ChunkName}: client claimed {Claimed}, server computed {Computed}",
                    newChunkName, headerHash, serverHash);
                await this.blobHelper.DeleteBlobIfExistsAsync(newChunkName);

                return ServiceResult.Failure(
                    ServiceStatus.ChunkHashMismatch, HttpStatusCode.UnprocessableEntity,
                    "Received chunk bytes do not match the client-asserted hash.",
                    detail: new ChunkHashMismatch { Expected = headerHash, Actual = serverHash });
            }

            existingContent.Chunks.Add(new ChunkMetadata
            {
                ChunkName = newChunkName,
                Hash = serverHash,
                Length = dataLength,
            });
            existingContent.RunningHashState = running.SaveState();

            // Hashed mode: ContentStatus.Ready is flipped only at commit - NOT here.
            // Keep access-ticket alive. Update LastAccessedAt as existing code does.
            existingContent.AccessTicketSetAt = DateTimeProvider.UtcNow;
            existingMetadata.LastAccessedAt = DateTimeProvider.UtcNow;
            await this.dbContext.SaveChangesAsync().ConfigureAwait(false);

            this.log.LogInformation(
                "Chunk hash verified for '{ContentId}' chunk {ChunkIndex} ({Hash}, {Length} bytes)",
                existingContent.ContentName, existingContent.Chunks.Count - 1, serverHash, dataLength);

            await this.EmitContentWrittenAsync(existingMetadata, existingContent.ContentName, newChunkName, caller);

            return ServiceResult<ChunkCreationOutput>.Ok(new ChunkCreationOutput
            {
                ChunkName = newChunkName,
                Hash = serverHash,
                Length = dataLength,
                AccessTicket = accessTicket,
            });
        }

        private async Task<long> UploadMainContentAsync(string chunkName, Stream inputStream)
        {
            this.log.LogInformation("Sanitizing main content.");

            string content;
            using (var reader = new StreamReader(inputStream))
            {
                content = await reader.ReadToEndAsync();
            }

            var lengthBefore = content.Length;
            content = MainContentSanitizer.Sanitize(content);
            this.log.LogInformation($"Lenght before sanitizing: {lengthBefore}, after sanitizing: {content.Length}.");

            using (var dataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)))
            {
                await this.blobHelper.EncryptAndUploadBlobAsync(chunkName, dataStream);
                return dataStream.Length;
            }
        }

        // Audit-payload guarantee: secretId/contentId/chunkId are opaque identifiers, never
        // derived from secret plaintext. The writer is a no-op for non-audited secrets.
        private ValueTask EmitContentReadAsync(ObjectMetadata secret, string contentId, string chunkId, Caller caller)
            => this.auditWriter.AppendAsync(secret, SecretAuditEventType.ContentRead, caller.SubjectType, caller.SubjectId, new { contentId, chunkId }, this.log, AuditOrigin.FromCaller(caller));

        private ValueTask EmitContentWrittenAsync(ObjectMetadata secret, string contentId, string chunkId, Caller caller)
            => this.auditWriter.AppendAsync(secret, SecretAuditEventType.ContentWritten, caller.SubjectType, caller.SubjectId, new { contentId, chunkId }, this.log, AuditOrigin.FromCaller(caller));
    }
}
