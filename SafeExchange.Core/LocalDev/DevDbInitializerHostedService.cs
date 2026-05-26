/// <summary>
/// DevDbInitializerHostedService — LOCAL SPIKE ONLY.
///
/// Ensures the Cosmos emulator database + containers exist on startup when
/// SAEX_DEV_MODE=true. In the cloud the containers are provisioned by ARM, so
/// the app never calls EnsureCreated; locally we create them from the EF model.
///
/// Part of the LocalDev harness — see LocalDev/README.md.
/// </summary>

namespace SafeExchange.Core.LocalDev
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    public class DevDbInitializerHostedService : IHostedService
    {
        private readonly IDbContextFactory<SafeExchangeDbContext> contextFactory;
        private readonly ILogger<DevDbInitializerHostedService> log;

        public DevDbInitializerHostedService(IDbContextFactory<SafeExchangeDbContext> contextFactory, ILogger<DevDbInitializerHostedService> log)
        {
            this.contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var context = await this.contextFactory.CreateDbContextAsync(cancellationToken);
                var created = await context.Database.EnsureCreatedAsync(cancellationToken);
                this.log.LogWarning("DEV: Cosmos emulator database ensured (created now: {Created}).", created);
            }
            catch (Exception ex)
            {
                this.log.LogError(ex, "DEV: failed to ensure Cosmos emulator database. Is the emulator running?");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
