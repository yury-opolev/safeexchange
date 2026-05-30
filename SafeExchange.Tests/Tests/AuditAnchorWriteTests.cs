/// <summary>
/// AuditAnchorWriteTests — emulator-backed regression for the Cosmos
/// PartitionKey/id mismatch that caused anchor writes to be silently
/// swallowed (AuditAnchorWriteFailed).  Before the fix, EnsureAnchorAsync
/// succeeded from the caller's perspective (AuditWriter swallows) but
/// nothing was persisted.  After the fix the row is present and
/// round-trips cleanly.
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging.Abstractions;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Threading.Tasks;

    [TestFixture]
    public class AuditAnchorWriteTests
    {
        private DbContextOptions<SafeExchangeDbContext> dbContextOptions;

        // Minimal IDbContextFactory backed by the shared options — no DI container needed.
        private sealed class SimpleDbContextFactory : IDbContextFactory<SafeExchangeDbContext>
        {
            private readonly DbContextOptions<SafeExchangeDbContext> options;

            public SimpleDbContextFactory(DbContextOptions<SafeExchangeDbContext> options)
                => this.options = options;

            public SafeExchangeDbContext CreateDbContext()
                => new SafeExchangeDbContext(this.options);
        }

        [OneTimeSetUp]
        public async Task OneTimeSetup()
        {
            var secretConfiguration = new ConfigurationBuilder()
                .AddUserSecrets<AuditAnchorWriteTests>()
                .Build();

            this.dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(
                    secretConfiguration.GetConnectionString("CosmosDb"),
                    databaseName: $"{nameof(AuditAnchorWriteTests)}Database",
                    CosmosTestOptions.UseGateway)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CosmosEventId.SyncNotSupported))
                .Options;

            await using var db = new SafeExchangeDbContext(this.dbContextOptions);
            await db.Database.EnsureCreatedAsync();
        }

        [OneTimeTearDown]
        public async Task OneTimeTeardown()
        {
            await using var db = new SafeExchangeDbContext(this.dbContextOptions);
            await db.Database.EnsureDeletedAsync();
        }

        [TearDown]
        public async Task Cleanup()
        {
            await using var db = new SafeExchangeDbContext(this.dbContextOptions);
            db.SecretAuditAnchors.RemoveRange(db.SecretAuditAnchors);
            await db.SaveChangesAsync();
        }

        private static ObjectMetadata MakeAuditEnabledSecret(string objectName)
        {
            var input = new MetadataCreationInput
            {
                ExpirationSettings = new ExpirationSettingsInput
                {
                    ScheduleExpiration = false,
                    ExpireAt = DateTime.UtcNow.AddYears(1),
                    ExpireOnIdleTime = false,
                    IdleTimeToExpire = TimeSpan.Zero,
                },
                AuditEnabled = true,
            };
            return new ObjectMetadata(objectName, input, "creator@test.test");
        }

        [Test]
        public async Task EnsureAnchorAsync_AuditEnabledSecret_PersistsAnchorRow()
        {
            // Arrange
            var secret = MakeAuditEnabledSecret("my-secret");
            Assert.That(secret.AuditEnabled, Is.True);
            Assert.That(secret.AuditInstanceId, Is.Not.Null.And.Not.Empty);

            var factory = new SimpleDbContextFactory(this.dbContextOptions);
            var writer = new AuditWriter(factory, NullLogger<AuditWriter>.Instance);

            // Act — before the fix this call succeeds silently but writes nothing
            // (the Cosmos 1001 BadRequest is swallowed).
            await writer.EnsureAnchorAsync(secret, "tester");

            // Assert — the row must now exist in the container.
            await using var db = new SafeExchangeDbContext(this.dbContextOptions);
            var anchor = await db.SecretAuditAnchors
                .FirstOrDefaultAsync(a => a.AuditInstanceId == secret.AuditInstanceId);

            Assert.That(anchor, Is.Not.Null, "Anchor row was not persisted — partition-key/id mismatch bug may still be present.");
            Assert.That(anchor!.SecretObjectName, Is.EqualTo("my-secret"));
            Assert.That(anchor.CreatedBy, Is.EqualTo("tester"));
        }

        [Test]
        public async Task EnsureAnchorAsync_CalledTwice_IsIdempotent()
        {
            // Arrange
            var secret = MakeAuditEnabledSecret("idempotent-secret");
            var factory = new SimpleDbContextFactory(this.dbContextOptions);
            var writer = new AuditWriter(factory, NullLogger<AuditWriter>.Instance);

            // Act — two calls must not throw or duplicate the row.
            await writer.EnsureAnchorAsync(secret, "tester");
            await writer.EnsureAnchorAsync(secret, "tester");

            // Assert — exactly one row.
            await using var db = new SafeExchangeDbContext(this.dbContextOptions);
            var count = await db.SecretAuditAnchors
                .CountAsync(a => a.AuditInstanceId == secret.AuditInstanceId);

            Assert.That(count, Is.EqualTo(1), "Idempotent EnsureAnchorAsync must not create duplicate rows.");
        }

        [Test]
        public async Task EnsureAnchorAsync_AnchorIdMatchesAuditInstanceId()
        {
            // Verify the document id == AuditInstanceId so Cosmos header PK matches the document PK.
            var secret = MakeAuditEnabledSecret("id-roundtrip-secret");
            var factory = new SimpleDbContextFactory(this.dbContextOptions);
            var writer = new AuditWriter(factory, NullLogger<AuditWriter>.Instance);

            await writer.EnsureAnchorAsync(secret, "tester");

            await using var db = new SafeExchangeDbContext(this.dbContextOptions);
            var anchor = await db.SecretAuditAnchors
                .FirstOrDefaultAsync(a => a.AuditInstanceId == secret.AuditInstanceId);

            Assert.That(anchor, Is.Not.Null);
            Assert.That(anchor!.id, Is.EqualTo(secret.AuditInstanceId), "Document id must equal AuditInstanceId to satisfy Cosmos partition-key header.");
        }
    }
}
