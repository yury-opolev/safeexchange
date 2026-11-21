/// <summary>
/// AccessTicketManager
/// </summary>

namespace SafeExchange.Core.Services
{
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Purger;
    using System;
    using System.Threading.Tasks;

    /// <summary>
    /// The upload lock on a content item (<see cref="ContentMetadata.AccessTicket"/>), shared by
    /// <see cref="ContentService"/> and <see cref="ChunkService"/>.
    /// </summary>
    internal sealed class AccessTicketManager
    {
        private readonly AccessTicketConfiguration configuration;

        private readonly SafeExchangeDbContext dbContext;

        private readonly IPurger purger;

        private readonly ILogger log;

        public AccessTicketManager(AccessTicketConfiguration configuration, SafeExchangeDbContext dbContext, IPurger purger, ILogger log)
        {
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.purger = purger ?? throw new ArgumentNullException(nameof(purger));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public static string NewTicket() => $"{Guid.NewGuid()}-{Random.Shared.NextInt64():00000000}";

        /// <summary>
        /// Returns the content's current ticket. An expired ticket (when a timeout is configured) is dropped
        /// first, together with all uploaded data, and the content is reset to blank.
        /// </summary>
        public async ValueTask<string> TryGetAccessTicketAsync(ContentMetadata content)
        {
            if (string.IsNullOrEmpty(content.AccessTicket))
            {
                return String.Empty;
            }

            var utcNow = DateTimeProvider.UtcNow;
            if ((this.configuration.AccessTicketTimeout > TimeSpan.Zero)
                && utcNow > (content.AccessTicketSetAt + this.configuration.AccessTicketTimeout))
            {
                this.log.LogInformation($"Access ticket '{content.AccessTicket}' expired (was set at {content.AccessTicketSetAt}) and is to be dropped. All data will be erased.");

                await this.purger.DeleteContentDataAsync(content);

                content.Chunks.Clear();
                content.Status = ContentStatus.Blank;
                content.RunningHashState = null;

                content.AccessTicket = String.Empty;
                content.AccessTicketSetAt = DateTime.MinValue;

                await this.dbContext.SaveChangesAsync();
            }

            return content.AccessTicket;
        }
    }
}
