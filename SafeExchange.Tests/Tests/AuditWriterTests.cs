/// <summary>
/// AuditWriterTests — the no-op fast-path, plus EF InMemory coverage of origin
/// attribution (channel / client label) and the hash chain. Cosmos-specific
/// behaviour lives in AuditEventCosmosTests.
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging.Abstractions;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;

    [TestFixture]
    public class AuditWriterTests
    {
        private sealed class InMemoryDbContextFactory : IDbContextFactory<SafeExchangeDbContext>
        {
            private readonly DbContextOptions<SafeExchangeDbContext> options = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseInMemoryDatabase($"{nameof(AuditWriterTests)}-{Guid.NewGuid()}")
                .Options;

            public SafeExchangeDbContext CreateDbContext() => new(this.options);
        }

        private sealed class ThrowingDbContextFactory : IDbContextFactory<SafeExchangeDbContext>
        {
            public SafeExchangeDbContext CreateDbContext()
                => throw new InvalidOperationException("Factory must not be called for non-audited secrets.");
        }

        private static ObjectMetadata MakeSecret(bool auditEnabled)
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
                AuditEnabled = auditEnabled,
            };
            return new ObjectMetadata("s1", input, "User u@x");
        }

        [Test]
        public async Task AppendAsync_AuditDisabledSecret_DoesNotTouchDbContext()
        {
            var secret = MakeSecret(auditEnabled: false);
            Assert.That(secret.AuditEnabled, Is.False);
            Assert.That(secret.AuditInstanceId, Is.Null);

            var writer = new AuditWriter(new ThrowingDbContextFactory(), NullLogger<AuditWriter>.Instance);

            // Must not throw — the no-op fast-path returns before touching the factory.
            await writer.AppendAsync(secret, SecretAuditEventType.SecretCreated, SubjectType.User, "u@x", payload: new { }, NullLogger.Instance);
        }

        [Test]
        public async Task EnsureAnchorAsync_AuditDisabledSecret_DoesNotTouchDbContext()
        {
            var secret = MakeSecret(auditEnabled: false);
            var writer = new AuditWriter(new ThrowingDbContextFactory(), NullLogger<AuditWriter>.Instance);
            await writer.EnsureAnchorAsync(secret, "u@x");
        }

        [Test]
        public async Task StampDeletionAsync_AuditDisabledSecret_DoesNotTouchDbContext()
        {
            var secret = MakeSecret(auditEnabled: false);
            var writer = new AuditWriter(new ThrowingDbContextFactory(), NullLogger<AuditWriter>.Instance);
            await writer.StampDeletionAsync(secret, "u@x", 365);
        }

        [Test]
        public async Task AppendAsync_NullSecret_DoesNotTouchDbContext()
        {
            var writer = new AuditWriter(new ThrowingDbContextFactory(), NullLogger<AuditWriter>.Instance);
            await writer.AppendAsync(null!, SecretAuditEventType.SecretCreated, SubjectType.User, "u@x", payload: null, NullLogger.Instance);
        }

        [Test]
        public void Constructor_NullFactory_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new AuditWriter(null!, NullLogger<AuditWriter>.Instance));
        }

        [Test]
        public void Constructor_NullLogger_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new AuditWriter(new ThrowingDbContextFactory(), null!));
        }

        [Test]
        public async Task AppendAsync_WithoutOrigin_PersistsEmptyOriginAndTheOriginalHash()
        {
            var factory = new InMemoryDbContextFactory();
            var secret = MakeSecret(auditEnabled: true);
            var writer = new AuditWriter(factory, NullLogger<AuditWriter>.Instance);

            await writer.AppendAsync(secret, SecretAuditEventType.SecretCreated, SubjectType.Application, "app", payload: null, NullLogger.Instance);

            var stored = (await ReadEventsAsync(factory, secret)).Single();
            Assert.That(stored.Channel, Is.Empty);
            Assert.That(stored.ClientLabel, Is.Empty);
            Assert.That(stored.Hash, Is.EqualTo(AuditEventHasher.ComputeHash(
                stored.AuditInstanceId, stored.SequenceNumber, stored.EventType, stored.OccurredAt,
                stored.ActorSubjectType, stored.ActorSubjectId, stored.ActorDisplayName, stored.Payload, stored.PrevHash)));
        }

        [Test]
        public async Task AppendAsync_WithMcpOrigin_PersistsAndHashesChannelAndLabel()
        {
            var factory = new InMemoryDbContextFactory();
            var secret = MakeSecret(auditEnabled: true);
            var writer = new AuditWriter(factory, NullLogger<AuditWriter>.Instance);

            await writer.AppendAsync(
                secret, SecretAuditEventType.SecretCreated, SubjectType.Application, "app", payload: null, NullLogger.Instance,
                AuditOrigin.Create("mcp", "copilot-cli/1.0.114"));

            var stored = (await ReadEventsAsync(factory, secret)).Single();
            Assert.That(stored.Channel, Is.EqualTo("mcp"));
            Assert.That(stored.ClientLabel, Is.EqualTo("copilot-cli/1.0.114"));
            Assert.That(stored.Hash, Is.EqualTo(AuditEventHasher.ComputeHash(
                stored.AuditInstanceId, stored.SequenceNumber, stored.EventType, stored.OccurredAt,
                stored.ActorSubjectType, stored.ActorSubjectId, stored.ActorDisplayName, stored.Payload, stored.PrevHash,
                "mcp", "copilot-cli/1.0.114")));
            Assert.That(stored.Hash, Is.Not.EqualTo(AuditEventHasher.ComputeHash(
                stored.AuditInstanceId, stored.SequenceNumber, stored.EventType, stored.OccurredAt,
                stored.ActorSubjectType, stored.ActorSubjectId, stored.ActorDisplayName, stored.Payload, stored.PrevHash)));
        }

        [Test]
        public async Task AppendAsync_MixedOrigins_ChainVerifies()
        {
            var factory = new InMemoryDbContextFactory();
            var secret = MakeSecret(auditEnabled: true);
            var writer = new AuditWriter(factory, NullLogger<AuditWriter>.Instance);
            var read = new { contentId = "c", chunkId = "k" };

            await writer.AppendAsync(secret, SecretAuditEventType.SecretCreated, SubjectType.Application, "app", payload: null, NullLogger.Instance);
            await writer.AppendAsync(secret, SecretAuditEventType.ContentRead, SubjectType.Application, "app", read, NullLogger.Instance, AuditOrigin.Create("mcp", "agent/1"));
            await writer.AppendAsync(secret, SecretAuditEventType.ContentRead, SubjectType.Application, "app", read, NullLogger.Instance, AuditOrigin.None);

            var events = await ReadEventsAsync(factory, secret);
            Assert.That(events.Select(e => e.Channel), Is.EqualTo(new[] { string.Empty, "mcp", string.Empty }));
            AssertChainVerifies(events);
        }

        [Test]
        public void SecretAuditEvent_MissingOriginFields_ReadAsEmpty()
        {
            // EF Cosmos materialises a property that is absent from an old document as null.
            var legacy = new SecretAuditEvent { Channel = null!, ClientLabel = null! };

            Assert.That(legacy.Channel, Is.Empty);
            Assert.That(legacy.ClientLabel, Is.Empty);
            Assert.That(new SecretAuditEvent().Channel, Is.Empty);
        }

        internal static void AssertChainVerifies(IReadOnlyList<SecretAuditEvent> events)
        {
            var prevHash = string.Empty;
            foreach (var e in events.OrderBy(e => e.SequenceNumber))
            {
                Assert.That(e.PrevHash, Is.EqualTo(prevHash), $"PrevHash of event {e.SequenceNumber}");
                var recomputed = AuditEventHasher.ComputeHash(
                    e.AuditInstanceId, e.SequenceNumber, e.EventType, e.OccurredAt,
                    e.ActorSubjectType, e.ActorSubjectId, e.ActorDisplayName, e.Payload, e.PrevHash,
                    e.Channel, e.ClientLabel);
                Assert.That(e.Hash, Is.EqualTo(recomputed), $"Hash of event {e.SequenceNumber}");
                prevHash = e.Hash;
            }
        }

        private static async Task<List<SecretAuditEvent>> ReadEventsAsync(IDbContextFactory<SafeExchangeDbContext> factory, ObjectMetadata secret)
        {
            await using var db = await factory.CreateDbContextAsync();
            return await db.SecretAuditEvents.AsNoTracking()
                .Where(e => e.AuditInstanceId == secret.AuditInstanceId)
                .OrderBy(e => e.SequenceNumber)
                .ToListAsync();
        }
    }
}
