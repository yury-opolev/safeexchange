/// <summary>
/// AuditEventCosmosTests — emulator-backed checks that origin attribution
/// (Channel / ClientLabel) round-trips through Cosmos, and that documents
/// written before those properties existed still read and verify.
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.Azure.Cosmos;
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
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json.Nodes;
    using System.Threading.Tasks;

    [TestFixture]
    public class AuditEventCosmosTests
    {
        private const string DatabaseName = nameof(AuditEventCosmosTests) + "Database";

        private DbContextOptions<SafeExchangeDbContext> dbContextOptions = null!;

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
                .AddUserSecrets<AuditEventCosmosTests>()
                .Build();

            this.dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(
                    secretConfiguration.GetConnectionString("CosmosDb")!,
                    databaseName: DatabaseName,
                    CosmosTestOptions.UseGateway)
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

        [Test]
        public async Task AppendAsync_McpOrigin_RoundTripsAndVerifies()
        {
            var secret = MakeAuditEnabledSecret("cosmos-mcp-secret");
            var writer = new AuditWriter(new SimpleDbContextFactory(this.dbContextOptions), NullLogger<AuditWriter>.Instance);

            await writer.AppendAsync(secret, SecretAuditEventType.SecretCreated, SubjectType.Application, "app", payload: null, NullLogger.Instance);
            await writer.AppendAsync(secret, SecretAuditEventType.SecretMetadataUpdated, SubjectType.Application, "app", payload: null, NullLogger.Instance, AuditOrigin.Create("mcp", "copilot-cli/1.0.114"));

            var events = await this.ReadEventsAsync(secret);
            Assert.That(events.Select(e => (e.Channel, e.ClientLabel)), Is.EqualTo(new[] { (string.Empty, string.Empty), ("mcp", "copilot-cli/1.0.114") }));
            AuditWriterTests.AssertChainVerifies(events);
        }

        [Test]
        public async Task DocumentWithoutOriginProperties_ReadsAsEmpty_AndTheChainContinues()
        {
            var secret = MakeAuditEnabledSecret("cosmos-legacy-secret");
            var writer = new AuditWriter(new SimpleDbContextFactory(this.dbContextOptions), NullLogger<AuditWriter>.Instance);
            await writer.AppendAsync(secret, SecretAuditEventType.SecretCreated, SubjectType.Application, "app", payload: null, NullLogger.Instance);
            await this.RemoveOriginPropertiesAsync(SecretAuditEvent.MakeId(secret.AuditInstanceId!, 1), secret.AuditInstanceId!);

            await writer.AppendAsync(secret, SecretAuditEventType.SecretMetadataUpdated, SubjectType.Application, "app", payload: null, NullLogger.Instance, AuditOrigin.Create("mcp", "agent/1"));

            var events = await this.ReadEventsAsync(secret);
            Assert.That(events, Has.Count.EqualTo(2));
            Assert.That(events[0].Channel, Is.Empty);
            Assert.That(events[0].ClientLabel, Is.Empty);
            Assert.That(events[1].Channel, Is.EqualTo("mcp"));
            AuditWriterTests.AssertChainVerifies(events);
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

        private async Task<List<SecretAuditEvent>> ReadEventsAsync(ObjectMetadata secret)
        {
            await using var db = new SafeExchangeDbContext(this.dbContextOptions);
            return await db.SecretAuditEvents.AsNoTracking()
                .Where(e => e.AuditInstanceId == secret.AuditInstanceId)
                .OrderBy(e => e.SequenceNumber)
                .ToListAsync();
        }

        // Rewrites a stored document without Channel/ClientLabel, i.e. as it was written before this change.
        private async Task RemoveOriginPropertiesAsync(string id, string auditInstanceId)
        {
            await using var db = new SafeExchangeDbContext(this.dbContextOptions);
            var container = db.Database.GetCosmosClient().GetContainer(DatabaseName, "SecretAuditEvents");
            var partitionKey = new PartitionKey(auditInstanceId);

            using var read = await container.ReadItemStreamAsync(id, partitionKey);
            read.EnsureSuccessStatusCode();
            using var reader = new StreamReader(read.Content);
            var document = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
            Assert.That(document.ContainsKey("Channel"), Is.True, "Arrange: new documents store Channel.");
            document.Remove("Channel");
            document.Remove("ClientLabel");

            using var body = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString()));
            using var replaced = await container.ReplaceItemStreamAsync(body, id, partitionKey);
            replaced.EnsureSuccessStatusCode();
        }
    }
}
