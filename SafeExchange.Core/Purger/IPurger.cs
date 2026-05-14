/// <summary>
/// IPurger
/// </summary>

namespace SafeExchange.Core.Purger
{
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Model;
    using System;

    public interface IPurger
    {
        /// <summary>
        /// Purge secret if it's condition for purging is true. Asynchronous.
        /// </summary>
        /// <param name="secretId">Id of the secret to purge.</param>
        /// <param name="dbContext">Database context.</param>
        /// <returns>True if secret was purged, false otherwise.</returns>
        public Task<bool> PurgeIfNeededAsync(string secretId, SafeExchangeDbContext dbContext);

        /// <summary>
        /// Purge secret if its condition for purging is true, recording the deletion
        /// as a "system" actor SecretDeleted audit event when audit is enabled.
        /// </summary>
        public Task<bool> PurgeIfNeededAsync(string secretId, SafeExchangeDbContext dbContext, IAuditWriter auditWriter, int auditRetentionDays);

        /// <summary>
        /// Delete all secret content and all it's metadata. Asynchronous.
        /// </summary>
        /// <param name="secretId">Id of the secret to purge.</param>
        /// <param name="dbContext">Database context.</param>
        /// <returns>Task, representing asynchronous operation.</returns>
        public Task PurgeAsync(string secretId, SafeExchangeDbContext dbContext);

        /// <summary>
        /// Delete a secret and stamp its audit anchor with the supplied actor and
        /// retention window. Emits a SecretDeleted audit event before the metadata
        /// row is removed.
        /// </summary>
        public Task PurgeAsync(string secretId, SafeExchangeDbContext dbContext, IAuditWriter auditWriter, SubjectType actorType, string actorId, int auditRetentionDays);

        /// <summary>
        /// Delete all content chunks. Asynchronous.
        /// </summary>
        /// <param name="content">Content metadata with list of chunks.</param>
        /// <returns>Task, representing asynchronous operation.</returns>
        public Task DeleteContentDataAsync(ContentMetadata content);

        /// <summary>
        /// Delete notification data if it's condition for purging is true. Asynchronous.
        /// </summary>
        /// <param name="notificationDataId">Id of the notification data item to purge.</param>
        /// <param name="dbContext">Database context.</param>
        /// <returns>True if notification data was purged, false otherwise.</returns>
        public Task<bool> PurgeNotificationDataIfNeededAsync(string notificationDataId, SafeExchangeDbContext dbContext);

        /// <summary>
        /// Delete notification data. Asynchronous.
        /// </summary>
        /// <param name="notificationDataId">Id of the notification data item to purge.</param>
        /// <param name="dbContext">Database context.</param>
        /// <returns>Task, representing asynchronous operation.</returns>
        public Task PurgeNotificationDataAsync(string notificationDataId, SafeExchangeDbContext dbContext);
    }
}
