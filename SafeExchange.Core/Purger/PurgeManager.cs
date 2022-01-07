/// <summary>
/// Purger
/// </summary>

namespace SafeExchange.Core.Purger
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
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

        public async Task PurgeAsync(string secretId, SafeExchangeDbContext dbContext)
        {
            this.log.LogInformation($"Purging '{secretId}'.");

            var existingPermissions = await dbContext.Permissions.Where(p => p.SecretName.Equals(secretId)).ToArrayAsync();
            dbContext.Permissions.RemoveRange(existingPermissions);
            await dbContext.SaveChangesAsync();
            log.LogInformation($"All permissions to access secret '{secretId}' deleted.");

            var metadataToDelete = await dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
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

        public async Task<bool> PurgeIfNeededAsync(string secretId, SafeExchangeDbContext dbContext)
        {
            var now = DateTimeProvider.UtcNow;
            var objectMetadata = await dbContext.Objects.FirstOrDefaultAsync(om => om.ObjectName.Equals(secretId));
            if (objectMetadata == null)
            {
                return false;
            }

            if ((objectMetadata.ExpirationMetadata.ScheduleExpiration == true) && objectMetadata.ExpirationMetadata.ExpireAt <= now)
            {
                await this.PurgeAsync(secretId, dbContext);
                return true;
            }

            var idleTime = now - objectMetadata.LastAccessedAt;
            if ((objectMetadata.ExpirationMetadata.ExpireOnIdleTime == true) && idleTime >= objectMetadata.ExpirationMetadata.IdleTimeToExpire)
            {
                await this.PurgeAsync(secretId, dbContext);
                return true;
            }

            return false;
        }

        public async Task DeleteContentDataAsync(ContentMetadata content)
        {
            foreach (var chunk in content.Chunks)
            {
                await this.blobHelper.DeleteBlobIfExistsAsync(chunk.ChunkName);
            }
        }
    }
}
