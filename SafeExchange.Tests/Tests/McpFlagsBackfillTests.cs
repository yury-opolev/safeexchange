/// <summary>
/// McpFlagsBackfillTests — the McpAllowed / McpBlockedByAdmin backfill (migration 00013): the pure
/// document rewrite, and the migration against the Cosmos DB emulator.
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.Azure.Cosmos;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging.Abstractions;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Migrations;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json.Nodes;
    using System.Threading.Tasks;
    using CoreUser = SafeExchange.Core.Model.User;

    [TestFixture]
    public class McpFlagsBackfillTests
    {
        [Test]
        public void BackfillIfMissing_AddsBothFlagsFalse_WhenMissing()
        {
            var result = McpFlagsBackfill.BackfillIfMissing("""{"id":"u1","PartitionKey":"USER"}""");

            Assert.That(result, Is.EqualTo("""{"id":"u1","PartitionKey":"USER","McpAllowed":false,"McpBlockedByAdmin":false}"""));
        }

        [Test]
        public void BackfillIfMissing_TreatsNullAsMissing_AndKeepsAnExistingFlag()
        {
            var result = McpFlagsBackfill.BackfillIfMissing("""{"id":"u1","McpAllowed":true,"McpBlockedByAdmin":null}""");

            Assert.That(result, Is.EqualTo("""{"id":"u1","McpAllowed":true,"McpBlockedByAdmin":false}"""));
        }

        [TestCase("""{"id":"u1","McpAllowed":false,"McpBlockedByAdmin":false}""")]
        [TestCase("""{"id":"u1","McpAllowed":true,"McpBlockedByAdmin":true}""")]
        [TestCase("not json")]
        [TestCase("[]")]
        public void BackfillIfMissing_ReturnsNull_WhenNothingToDo(string document)
        {
            Assert.That(McpFlagsBackfill.BackfillIfMissing(document), Is.Null);
        }
    }

    [TestFixture]
    public class McpFlagsBackfillMigrationTests
    {
        private const string DatabaseName = $"{nameof(McpFlagsBackfillMigrationTests)}Database";

        private SafeExchangeDbContext dbContext = null!;

        private CosmosClient cosmosClient = null!;

        private Container users = null!;

        [OneTimeSetUp]
        public async Task OneTimeSetup()
        {
            var connectionString = new ConfigurationBuilder().AddUserSecrets<McpFlagsBackfillMigrationTests>().Build().GetConnectionString("CosmosDb");
            var options = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(connectionString!, DatabaseName, CosmosTestOptions.UseGateway)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CosmosEventId.SyncNotSupported))
                .Options;

            this.dbContext = new SafeExchangeDbContext(options);
            await this.dbContext.Database.EnsureCreatedAsync();

            this.cosmosClient = CosmosTestOptions.CreateClient(connectionString!);
            this.users = this.cosmosClient.GetDatabase(DatabaseName).GetContainer("Users");
        }

        [OneTimeTearDown]
        public void OneTimeCleanup()
        {
            TestDatabase.EnsureDeletedTolerant(this.dbContext.Database);
            this.dbContext.Dispose();
            this.cosmosClient.Dispose();
        }

        [TearDown]
        public void Cleanup()
        {
            this.dbContext.ChangeTracker.Clear();
            this.dbContext.Users.RemoveRange(this.dbContext.Users.ToList());
            this.dbContext.SaveChanges();
            this.dbContext.ChangeTracker.Clear();
        }

        [Test]
        public async Task LegacyUserDocument_WithoutTheFlags_ReadsAsFalse()
        {
            var legacy = await this.SeedLegacyUserAsync("legacy-read@test.test");

            var user = await this.dbContext.Users.AsNoTracking().SingleAsync(u => u.Id == legacy.Id);

            Assert.That((await this.ReadRawAsync(legacy.Id)).ContainsKey("McpAllowed"), Is.False, "Arrange: the document has no McpAllowed.");
            Assert.That(user.McpAllowed, Is.False);
            Assert.That(user.McpBlockedByAdmin, Is.False);
        }

        [Test]
        public async Task Migration00013_WritesBothFlagsFalse_OntoLegacyUsers_AndIsIdempotent()
        {
            var legacy = await this.SeedLegacyUserAsync("legacy@test.test");

            var first = await MigrationsHelper.BackfillMcpFlagsAsync(this.users, NullLogger.Instance);
            var second = await MigrationsHelper.BackfillMcpFlagsAsync(this.users, NullLogger.Instance);

            var raw = await this.ReadRawAsync(legacy.Id);
            Assert.That(first, Is.EqualTo(1));
            Assert.That(second, Is.EqualTo(0));
            Assert.That(raw["McpAllowed"]!.GetValue<bool>(), Is.False);
            Assert.That(raw["McpBlockedByAdmin"]!.GetValue<bool>(), Is.False);
            Assert.That(raw["AadUpn"]!.GetValue<string>(), Is.EqualTo("legacy@test.test"));
        }

        [Test]
        public async Task Migration00013_LeavesUsersThatHaveTheFlags_Unchanged()
        {
            var current = new CoreUser("Current", "oid-current", "tid-test", "current@test.test", "current@test.test")
            {
                McpAllowed = true,
                McpBlockedByAdmin = true,
            };
            this.dbContext.Users.Add(current);
            await this.dbContext.SaveChangesAsync();
            var etagBefore = (await this.ReadRawAsync(current.Id))["_etag"]!.GetValue<string>();

            var backfilled = await MigrationsHelper.BackfillMcpFlagsAsync(this.users, NullLogger.Instance);

            var raw = await this.ReadRawAsync(current.Id);
            Assert.That(backfilled, Is.EqualTo(0));
            Assert.That(raw["_etag"]!.GetValue<string>(), Is.EqualTo(etagBefore));
            Assert.That(raw["McpAllowed"]!.GetValue<bool>(), Is.True);
            Assert.That(raw["McpBlockedByAdmin"]!.GetValue<bool>(), Is.True);
        }

        // A user written before the flags existed: saved through EF, then the two properties removed from the stored document.
        private async Task<CoreUser> SeedLegacyUserAsync(string upn)
        {
            var user = new CoreUser(upn, $"oid-{upn}", "tid-test", upn, upn);
            this.dbContext.Users.Add(user);
            await this.dbContext.SaveChangesAsync();
            this.dbContext.ChangeTracker.Clear();

            var document = await this.ReadRawAsync(user.Id);
            document.Remove("McpAllowed");
            document.Remove("McpBlockedByAdmin");
            using var content = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString()));
            using var replaced = await this.users.ReplaceItemStreamAsync(content, user.Id, new PartitionKey(CoreUser.DefaultPartitionKey));
            replaced.EnsureSuccessStatusCode();
            return user;
        }

        private async Task<JsonObject> ReadRawAsync(string id)
        {
            using var response = await this.users.ReadItemStreamAsync(id, new PartitionKey(CoreUser.DefaultPartitionKey));
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(response.Content);
            return JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
        }
    }
}
