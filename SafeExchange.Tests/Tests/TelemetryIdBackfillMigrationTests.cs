/// <summary>
/// TelemetryIdBackfillMigrationTests — integration tests for the migration-00011 backfill.
/// Tests exercise both the pure TelemetryIdBackfill helper and the end-to-end migration
/// logic against the Cosmos DB emulator via the shared CosmosTestOptions helper.
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.Azure.Cosmos;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Migrations;
    using SafeExchange.Core.Telemetry;
    using CoreUser = SafeExchange.Core.Model.User;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    // ---------------------------------------------------------------------------
    // Pure unit tests for TelemetryIdBackfill (no Cosmos required)
    // ---------------------------------------------------------------------------

    [TestFixture]
    public class TelemetryIdBackfillUnitTests
    {
        [Test]
        public void BackfillIfMissing_EmptyTelemetryId_ReturnsRewritten()
        {
            const string input = """{"id":"u1","PartitionKey":"USER","TelemetryId":""}""";
            var newId = Guid.NewGuid().ToString("n");
            var expiry = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var result = TelemetryIdBackfill.BackfillIfMissing(input, newId, expiry);
            Assert.That(result, Is.Not.Null, "Should rewrite a doc with an empty TelemetryId.");
            Assert.That(result, Does.Contain(newId));
        }

        [Test]
        public void BackfillIfMissing_MissingTelemetryId_ReturnsRewritten()
        {
            const string input = """{"id":"u2","PartitionKey":"USER"}""";
            var newId = Guid.NewGuid().ToString("n");
            var expiry = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var result = TelemetryIdBackfill.BackfillIfMissing(input, newId, expiry);
            Assert.That(result, Is.Not.Null, "Should rewrite a doc with a missing TelemetryId.");
            Assert.That(result, Does.Contain(newId));
        }

        [Test]
        public void BackfillIfMissing_NonEmptyTelemetryId_ReturnsNull()
        {
            const string input = """{"id":"u3","PartitionKey":"USER","TelemetryId":"abc"}""";
            var result = TelemetryIdBackfill.BackfillIfMissing(input, "should-not-be-used", DateTime.UtcNow);
            Assert.That(result, Is.Null, "Should not rewrite a doc that already has a TelemetryId.");
        }

        [Test]
        public void BackfillIfMissing_NullTelemetryId_ReturnsRewritten()
        {
            const string input = """{"id":"u4","PartitionKey":"USER","TelemetryId":null}""";
            var newId = Guid.NewGuid().ToString("n");
            var expiry = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var result = TelemetryIdBackfill.BackfillIfMissing(input, newId, expiry);
            Assert.That(result, Is.Not.Null, "Should rewrite a doc with a null TelemetryId.");
            Assert.That(result, Does.Contain(newId));
        }

        [Test]
        public void BackfillIfMissing_PreservesOtherFields()
        {
            const string input = """{"id":"u5","PartitionKey":"USER","DisplayName":"Alice","TelemetryId":""}""";
            var newId = Guid.NewGuid().ToString("n");
            var expiry = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var result = TelemetryIdBackfill.BackfillIfMissing(input, newId, expiry);
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Does.Contain("\"DisplayName\":\"Alice\"").Or.Contain("\"DisplayName\": \"Alice\""));
        }

        [Test]
        public void BackfillIfMissing_MalformedJson_ReturnsNull()
        {
            Assert.That(TelemetryIdBackfill.BackfillIfMissing("not json", "id", DateTime.UtcNow), Is.Null);
        }
    }

    // ---------------------------------------------------------------------------
    // Integration tests: seed via EF Core → run backfill logic → verify via EF Core
    // ---------------------------------------------------------------------------

    [TestFixture]
    public class TelemetryIdBackfillMigrationTests
    {
        private SafeExchangeDbContext dbContext;

        private DbContextOptions<SafeExchangeDbContext> dbContextOptions;

        private string connectionString;

        [OneTimeSetUp]
        public async Task OneTimeSetup()
        {
            var builder = new ConfigurationBuilder().AddUserSecrets<TelemetryIdBackfillMigrationTests>();
            var secretConfiguration = builder.Build();

            this.connectionString = secretConfiguration.GetConnectionString("CosmosDb");

            this.dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(
                    this.connectionString,
                    databaseName: $"{nameof(TelemetryIdBackfillMigrationTests)}Database",
                    CosmosTestOptions.UseGateway)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CosmosEventId.SyncNotSupported))
                .Options;

            this.dbContext = new SafeExchangeDbContext(this.dbContextOptions);
            await this.dbContext.Database.EnsureCreatedAsync();

            DateTimeProvider.SpecifiedDateTime = new DateTime(2026, 5, 28, 10, 0, 0, DateTimeKind.Utc);
            DateTimeProvider.UseSpecifiedDateTime = true;
        }

        [OneTimeTearDown]
        public void OneTimeCleanup()
        {
            DateTimeProvider.UseSpecifiedDateTime = false;
            this.dbContext.Database.EnsureDeleted();
            this.dbContext.Dispose();
        }

        [TearDown]
        public void Cleanup()
        {
            this.dbContext.ChangeTracker.Clear();
            this.dbContext.Users.RemoveRange(this.dbContext.Users.ToList());
            this.dbContext.SaveChanges();
        }

        /// <summary>
        /// Runs the migration-00011 backfill logic (same code path as <c>RunMigration00011Async</c>)
        /// against the emulator database.
        /// </summary>
        private async Task RunBackfillAsync(string databaseName)
        {
            using var client = CosmosTestOptions.CreateClient(this.connectionString);
            var database = client.GetDatabase(databaseName);
            var container = database.GetContainer("Users");

            var query = new QueryDefinition(
                "SELECT * FROM c WHERE NOT IS_DEFINED(c.TelemetryId) OR IS_NULL(c.TelemetryId) OR c.TelemetryId = \"\"");

            using var feed = container.GetItemQueryIterator<MigrationItem00011_User>(queryDefinition: query);
            while (feed.HasMoreResults)
            {
                var response = await feed.ReadNextAsync();
                foreach (var item in response)
                {
                    if (!string.IsNullOrEmpty(item.TelemetryId))
                    {
                        continue;
                    }

                    var streamResp = await container.ReadItemStreamAsync(
                        item.id, new PartitionKey(item.PartitionKey));

                    string original;
                    using (var reader = new StreamReader(streamResp.Content))
                    {
                        original = await reader.ReadToEndAsync();
                    }

                    var newTelemetryId = Guid.NewGuid().ToString("n");
                    var newExpiry = TelemetryIdRotator.NextWeekBoundaryUtc(DateTimeProvider.UtcNow);
                    var rewritten = TelemetryIdBackfill.BackfillIfMissing(original, newTelemetryId, newExpiry);
                    if (rewritten is null)
                    {
                        continue;
                    }

                    using var ms = new MemoryStream(Encoding.UTF8.GetBytes(rewritten));
                    await container.ReplaceItemStreamAsync(ms, item.id, new PartitionKey(item.PartitionKey));
                }
            }
        }

        [Test]
        public async Task Migration00011_EmptyTelemetryId_IsBackfilled()
        {
            // [GIVEN] A user with an empty TelemetryId.
            var user = new CoreUser("Alice", "oid-alice", "tid-test", "alice@test.test", "alice@test.test")
            {
                TelemetryId = string.Empty,
            };
            this.dbContext.Users.Add(user);
            await this.dbContext.SaveChangesAsync();
            this.dbContext.ChangeTracker.Clear();

            // [WHEN] Migration 00011 backfill runs.
            await this.RunBackfillAsync($"{nameof(TelemetryIdBackfillMigrationTests)}Database");

            // [THEN] The user now has a non-empty TelemetryId and a future TelemetryIdExpiresAt.
            var updated = await this.dbContext.Users.Where(u => u.Id == user.Id).FirstOrDefaultAsync();
            Assert.That(updated, Is.Not.Null);
            Assert.That(updated!.TelemetryId, Is.Not.Empty, "TelemetryId should be backfilled.");
            Assert.That(updated.TelemetryIdExpiresAt, Is.GreaterThan(DateTimeProvider.SpecifiedDateTime),
                "TelemetryIdExpiresAt should be in the future.");
        }

        [Test]
        public async Task Migration00011_ExistingTelemetryId_IsNotChanged()
        {
            // [GIVEN] A user who already has a TelemetryId and an expiry.
            const string existingId = "alreadyset00000000000000000000000";
            var existingExpiry = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var user = new CoreUser("Bob", "oid-bob", "tid-test", "bob@test.test", "bob@test.test")
            {
                TelemetryId = existingId,
                TelemetryIdExpiresAt = existingExpiry,
            };
            this.dbContext.Users.Add(user);
            await this.dbContext.SaveChangesAsync();
            this.dbContext.ChangeTracker.Clear();

            // [WHEN] Migration 00011 backfill runs.
            await this.RunBackfillAsync($"{nameof(TelemetryIdBackfillMigrationTests)}Database");

            // [THEN] The user's TelemetryId and expiry are unchanged.
            var updated = await this.dbContext.Users.Where(u => u.Id == user.Id).FirstOrDefaultAsync();
            Assert.That(updated, Is.Not.Null);
            Assert.That(updated!.TelemetryId, Is.EqualTo(existingId),
                "Existing TelemetryId must not be overwritten.");
        }

        [Test]
        public async Task Migration00011_TwoUsers_OnlyEmptyOneIsBackfilled()
        {
            // [GIVEN] Two users: one with an existing TelemetryId, one with empty.
            const string existingId = "existingid0000000000000000000000";
            var userWithId = new CoreUser("Charlie", "oid-charlie", "tid-test", "charlie@test.test", "charlie@test.test")
            {
                TelemetryId = existingId,
                TelemetryIdExpiresAt = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            var userWithoutId = new CoreUser("Diana", "oid-diana", "tid-test", "diana@test.test", "diana@test.test")
            {
                TelemetryId = string.Empty,
            };
            this.dbContext.Users.Add(userWithId);
            this.dbContext.Users.Add(userWithoutId);
            await this.dbContext.SaveChangesAsync();
            this.dbContext.ChangeTracker.Clear();

            // [WHEN] Migration 00011 backfill runs.
            await this.RunBackfillAsync($"{nameof(TelemetryIdBackfillMigrationTests)}Database");

            // [THEN] The user without a TelemetryId now has one.
            var updatedWithoutId = await this.dbContext.Users.Where(u => u.Id == userWithoutId.Id).FirstOrDefaultAsync();
            Assert.That(updatedWithoutId!.TelemetryId, Is.Not.Empty,
                "User without TelemetryId should be backfilled.");
            Assert.That(updatedWithoutId.TelemetryIdExpiresAt, Is.GreaterThan(DateTimeProvider.SpecifiedDateTime),
                "TelemetryIdExpiresAt should be in the future.");

            // [AND] The user with an existing TelemetryId is unchanged.
            var updatedWithId = await this.dbContext.Users.Where(u => u.Id == userWithId.Id).FirstOrDefaultAsync();
            Assert.That(updatedWithId!.TelemetryId, Is.EqualTo(existingId),
                "User with existing TelemetryId must not be changed.");
        }
    }
}
