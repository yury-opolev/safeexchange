/// <summary>
/// AuditWriterTests — pure-unit coverage of the no-op fast-path. Cosmos-backed
/// retry/hash-chain behaviour lives in the integration test suite (not in scope
/// here without a Cosmos emulator).
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
    using System.Threading.Tasks;

    [TestFixture]
    public class AuditWriterTests
    {
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
    }
}
