/// <summary>
/// Purger
/// </summary>

namespace SafeExchange.Core.Purger
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Model;
    using System;
    using System.Threading.Tasks;

    public class PurgeManager : IPurger
    {
        private readonly IConfiguration configuration;

        private readonly IBlobHelper blobHelper;

        private readonly ILogger<PurgeManager> log;

        public PurgeManager(IConfiguration configuration, IBlobHelper blobHelper, ILogger<PurgeManager> log)
        {
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            this.blobHelper = blobHelper ?? throw new ArgumentNullException(nameof(blobHelper));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public Task PurgeAsync(string secretId, SafeExchangeDbContext dbContext)
            => this.PurgeInternalAsync(secretId, dbContext, auditWriter: null, actorType: SubjectType.User, actorId: string.Empty, auditRetentionDays: 0);

        public Task PurgeAsync(string secretId, SafeExchangeDbContext dbContext, IAuditWriter auditWriter, SubjectType actorType, string actorId, int auditRetentionDays)
            => this.PurgeInternalAsync(secretId, dbContext, auditWriter, actorType, actorId, auditRetentionDays);

        private async Task PurgeInternalAsync(string secretId, SafeExchangeDbContext dbContext, IAuditWriter? auditWriter, SubjectType actorType, string actorId, int auditRetentionDays)
        {
            this.log.LogInformation($"Purging '{secretId}'.");

            var metadataToDelete = await dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));

            // Emit the SecretDeleted audit event before removing the metadata, so the
            // writer can still resolve AuditInstanceId/AuditEnabled off the live entity.
            if (metadataToDelete is not null && auditWriter is not null && metadataToDelete.AuditEnabled)
            {
                await auditWriter.AppendAsync(
                    metadataToDelete, SecretAuditEventType.SecretDeleted,
                    actorType, actorId, payload: new { }, this.log);
                await auditWriter.StampDeletionAsync(metadataToDelete, actorId, auditRetentionDays);
            }

            var existingPermissions = await dbContext.Permissions.Where(p => p.SecretName.Equals(secretId)).ToArrayAsync();
            dbContext.Permissions.RemoveRange(existingPermissions);
            await dbContext.SaveChangesAsync();
            log.LogInformation($"All permissions to access secret '{secretId}' deleted.");

            if (metadataToDelete != null)
            {
                dbContext.Objects.Remove(metadataToDelete);
                await dbContext.SaveChangesAsync();
            }

            log.LogInformation($"Metadata for secret '{secretId}' deleted.");

            var existingAccessRequests = await dbContext.AccessRequests.Where(ar => ar.ObjectName.Equals(secretId)).ToArrayAsync();
            dbContext.AccessRequests.RemoveRange(existingAccessRequests);
            await dbContext.SaveChangesAsync();
            log.LogInformation($"All access requests to secret '{secretId}' were deleted.");

            foreach (var content in metadataToDelete?.Content ?? Array.Empty<ContentMetadata>().ToList())
            {
                await this.DeleteContentDataAsync(content);
            }

            log.LogInformation($"All blobs for secret '{secretId}' were deleted.");
        }

        public Task<bool> PurgeIfNeededAsync(string secretId, SafeExchangeDbContext dbContext)
            => this.PurgeIfNeededInternalAsync(secretId, dbContext, auditWriter: null, auditRetentionDays: 0);

        public Task<bool> PurgeIfNeededAsync(string secretId, SafeExchangeDbContext dbContext, IAuditWriter auditWriter, int auditRetentionDays)
            => this.PurgeIfNeededInternalAsync(secretId, dbContext, auditWriter, auditRetentionDays);

        private async Task<bool> PurgeIfNeededInternalAsync(string secretId, SafeExchangeDbContext dbContext, IAuditWriter? auditWriter, int auditRetentionDays)
        {
            var now = DateTimeProvider.UtcNow;
            var objectMetadata = await dbContext.Objects.FirstOrDefaultAsync(om => om.ObjectName.Equals(secretId));
            if (objectMetadata == null)
            {
                return false;
            }

            // Automated/passive expiration is recorded with a "system" actor so that the
            // audit log clearly distinguishes operator-initiated deletes from policy purges.
            const string SystemActorId = "system:purger";

            if ((objectMetadata.ExpirationMetadata.ScheduleExpiration == true) && objectMetadata.ExpirationMetadata.ExpireAt <= now)
            {
                await this.PurgeInternalAsync(secretId, dbContext, auditWriter, SubjectType.User, SystemActorId, auditRetentionDays);
                return true;
            }

            var idleTime = now - objectMetadata.LastAccessedAt;
            if ((objectMetadata.ExpirationMetadata.ExpireOnIdleTime == true) && idleTime >= objectMetadata.ExpirationMetadata.IdleTimeToExpire)
            {
                await this.PurgeInternalAsync(secretId, dbContext, auditWriter, SubjectType.User, SystemActorId, auditRetentionDays);
                return true;
            }

            return false;
        }

        public async Task DeleteContentDataAsync(ContentMetadata content)
        {
            foreach (var chunk in content.Chunks)
            {
                this.log.LogInformation($"Deleting blob (if exists) for content '{content.ContentName}' (status: {content.Status}), chunk: '{chunk.ChunkName}'.");
                await this.blobHelper.DeleteBlobIfExistsAsync(chunk.ChunkName);
                this.log.LogInformation($"Deleted blob (if existed) for content '{content.ContentName}' (status: {content.Status}), chunk: '{chunk.ChunkName}'.");
            }
        }

        public async Task<bool> PurgeNotificationDataIfNeededAsync(string notificationDataId, SafeExchangeDbContext dbContext)
        {
            var now = DateTimeProvider.UtcNow;
            var notificationData = await dbContext.WebhookNotificationData.FirstOrDefaultAsync(wnd => wnd.Id.Equals(notificationDataId));
            if (notificationData == null)
            {
                return false;
            }

            if (notificationData.ExpireAt <= now)
            {
                await this.PurgeAsync(notificationDataId, dbContext);
                return true;
            }

            return false;
        }

        public async Task PurgeNotificationDataAsync(string notificationDataId, SafeExchangeDbContext dbContext)
        {
            this.log.LogInformation($"Purging notification data '{notificationDataId}'.");

            var dataToDelete = await dbContext.WebhookNotificationData.FirstOrDefaultAsync(wnd => wnd.Id.Equals(notificationDataId));
            if (dataToDelete != null)
            {
                dbContext.WebhookNotificationData.Remove(dataToDelete);
                await dbContext.SaveChangesAsync();
            }

            log.LogInformation($"Notification data '{notificationDataId}' deleted.");
        }
    }
}
