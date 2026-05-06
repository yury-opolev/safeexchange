/// <summary>
/// OrphanedSecretManagerTests
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;

    [TestFixture]
    public class OrphanedSecretManagerTests
    {
        private SafeExchangeDbContext dbContext;
        private OrphanedSecretConfiguration orphanConfig;
        private Features features;
        private OrphanedSecretManager manager;

        [OneTimeSetUp]
        public async Task OneTimeSetup()
        {
            var builder = new ConfigurationBuilder().AddUserSecrets<OrphanedSecretManagerTests>();
            var secretConfiguration = builder.Build();

            var dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(secretConfiguration.GetConnectionString("CosmosDb"),
                    databaseName: $"{nameof(OrphanedSecretManagerTests)}Database",
                    CosmosTestOptions.UseGateway)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CosmosEventId.SyncNotSupported))
                .Options;

            this.dbContext = new SafeExchangeDbContext(dbContextOptions);
            await this.dbContext.Database.EnsureCreatedAsync();
        }

        [OneTimeTearDown]
        public async Task OneTimeTearDown()
        {
            await this.dbContext.Database.EnsureDeletedAsync();
            this.dbContext.Dispose();
        }

        [SetUp]
        public void Setup()
        {
            this.features = new Features { UseAccessGiveUp = true };
            this.orphanConfig = new OrphanedSecretConfiguration
            {
                Ownership = OrphanOwnershipMode.UserOrApp,
                GracePeriod = TimeSpan.FromDays(7)
            };

            var featuresOptions = Mock.Of<IOptionsMonitor<Features>>(x => x.CurrentValue == this.features);
            var configOptions = Mock.Of<IOptionsMonitor<OrphanedSecretConfiguration>>(x => x.CurrentValue == this.orphanConfig);

            this.manager = new OrphanedSecretManager(featuresOptions, configOptions, TestFactory.CreateLogger<OrphanedSecretManager>());

            DateTimeProvider.UseSpecifiedDateTime = true;
            DateTimeProvider.SpecifiedDateTime = new DateTime(2026, 5, 6, 9, 0, 0, DateTimeKind.Utc);
        }

        [TearDown]
        public async Task TearDown()
        {
            await ClearDb();
            DateTimeProvider.UseSpecifiedDateTime = false;
        }

        private async Task ClearDb()
        {
            this.dbContext.Permissions.RemoveRange(this.dbContext.Permissions);
            this.dbContext.Objects.RemoveRange(this.dbContext.Objects);
            await this.dbContext.SaveChangesAsync();
        }

        private async Task SeedSecret(string secretId, DateTime? expireAt = null, bool scheduleExpiration = false)
        {
            var creationInput = new MetadataCreationInput
            {
                ExpirationSettings = new ExpirationSettingsInput
                {
                    ScheduleExpiration = scheduleExpiration,
                    ExpireAt = expireAt ?? DateTime.UtcNow.AddDays(30),
                    ExpireOnIdleTime = false,
                    IdleTimeToExpire = TimeSpan.FromDays(30)
                }
            };
            var metadata = new ObjectMetadata(secretId, creationInput, "test-creator");
            this.dbContext.Objects.Add(metadata);
            await this.dbContext.SaveChangesAsync();
        }

        private async Task SeedPermission(string secretId, SubjectType subjectType, string subjectId, bool canGrantAccess)
        {
            var p = new SubjectPermissions(secretId, subjectType, subjectId, subjectId)
            {
                CanRead = true,
                CanWrite = false,
                CanGrantAccess = canGrantAccess,
                CanRevokeAccess = false
            };
            this.dbContext.Permissions.Add(p);
            await this.dbContext.SaveChangesAsync();
        }

        [Test]
        public async Task ApplyOrphanRuleAsync_FeatureFlagOff_NoOps()
        {
            this.features.UseAccessGiveUp = false;
            await SeedSecret("secret-1");

            var result = await this.manager.ApplyOrphanRuleAsync("secret-1", this.dbContext);
            await this.dbContext.SaveChangesAsync();

            Assert.That(result.WasOrphaned, Is.False);
            var metadata = await this.dbContext.Objects.FindAsync("secret-1");
            Assert.That(metadata.ExpirationMetadata.ScheduleExpiration, Is.False);
        }

        [Test]
        public async Task ApplyOrphanRuleAsync_NoCustodian_SchedulesGrace()
        {
            await SeedSecret("secret-1");

            var result = await this.manager.ApplyOrphanRuleAsync("secret-1", this.dbContext);
            await this.dbContext.SaveChangesAsync();

            Assert.That(result.WasOrphaned, Is.True);
            var metadata = await this.dbContext.Objects.FindAsync("secret-1");
            Assert.That(metadata.ExpirationMetadata.ScheduleExpiration, Is.True);
            Assert.That(metadata.ExpirationMetadata.ExpireAt,
                Is.EqualTo(DateTimeProvider.UtcNow.AddDays(7)));
        }

        [Test]
        public async Task ApplyOrphanRuleAsync_UserCustodian_NoSchedule()
        {
            await SeedSecret("secret-1");
            await SeedPermission("secret-1", SubjectType.User, "alice@test", canGrantAccess: true);

            var result = await this.manager.ApplyOrphanRuleAsync("secret-1", this.dbContext);
            await this.dbContext.SaveChangesAsync();

            Assert.That(result.WasOrphaned, Is.False);
            var metadata = await this.dbContext.Objects.FindAsync("secret-1");
            Assert.That(metadata.ExpirationMetadata.ScheduleExpiration, Is.False);
        }

        [Test]
        public async Task ApplyOrphanRuleAsync_AppCustodian_NoSchedule()
        {
            await SeedSecret("secret-1");
            await SeedPermission("secret-1", SubjectType.Application, "app-client-id", canGrantAccess: true);

            var result = await this.manager.ApplyOrphanRuleAsync("secret-1", this.dbContext);
            await this.dbContext.SaveChangesAsync();

            Assert.That(result.WasOrphaned, Is.False);
        }

        [Test]
        public async Task ApplyOrphanRuleAsync_OnlyReadOnlyPermissions_Orphans()
        {
            await SeedSecret("secret-1");
            await SeedPermission("secret-1", SubjectType.User, "alice@test", canGrantAccess: false);
            await SeedPermission("secret-1", SubjectType.User, "bob@test", canGrantAccess: false);

            var result = await this.manager.ApplyOrphanRuleAsync("secret-1", this.dbContext);
            await this.dbContext.SaveChangesAsync();

            Assert.That(result.WasOrphaned, Is.True);
            var metadata = await this.dbContext.Objects.FindAsync("secret-1");
            Assert.That(metadata.ExpirationMetadata.ScheduleExpiration, Is.True);
        }

        [Test]
        public async Task ApplyOrphanRuleAsync_GroupCustodianUserOrApp_Orphans()
        {
            this.orphanConfig.Ownership = OrphanOwnershipMode.UserOrApp;
            await SeedSecret("secret-1");
            await SeedPermission("secret-1", SubjectType.Group, "group-id-1", canGrantAccess: true);

            var result = await this.manager.ApplyOrphanRuleAsync("secret-1", this.dbContext);
            await this.dbContext.SaveChangesAsync();

            Assert.That(result.WasOrphaned, Is.True);
        }

        [Test]
        public async Task ApplyOrphanRuleAsync_GroupCustodianUserOrAppOrGroup_NoSchedule()
        {
            this.orphanConfig.Ownership = OrphanOwnershipMode.UserOrAppOrGroup;
            await SeedSecret("secret-1");
            await SeedPermission("secret-1", SubjectType.Group, "group-id-1", canGrantAccess: true);

            var result = await this.manager.ApplyOrphanRuleAsync("secret-1", this.dbContext);
            await this.dbContext.SaveChangesAsync();

            Assert.That(result.WasOrphaned, Is.False);
        }

        [Test]
        public async Task ApplyOrphanRuleAsync_PendingRemoveOfLastCustodian_Orphans()
        {
            // Reproduces the scenario where a caller stages a Remove on the last custodian row
            // and then invokes ApplyOrphanRuleAsync in the same transaction (before SaveChangesAsync).
            await SeedSecret("secret-1");
            await SeedPermission("secret-1", SubjectType.User, "alice@test", canGrantAccess: true);

            var lastCustodian = await this.dbContext.Permissions
                .FirstAsync(p => p.SecretName == "secret-1" && p.SubjectId == "alice@test");
            this.dbContext.Permissions.Remove(lastCustodian);

            var result = await this.manager.ApplyOrphanRuleAsync("secret-1", this.dbContext);
            await this.dbContext.SaveChangesAsync();

            Assert.That(result.WasOrphaned, Is.True);
            var metadata = await this.dbContext.Objects.FindAsync("secret-1");
            Assert.That(metadata.ExpirationMetadata.ScheduleExpiration, Is.True);
            Assert.That(metadata.ExpirationMetadata.ExpireAt, Is.EqualTo(DateTimeProvider.UtcNow.AddDays(7)));
        }

        [Test]
        public async Task ApplyOrphanRuleAsync_PendingFlagFlipOffOnLastCustodian_Orphans()
        {
            // Reproduces the scenario where UnsetPermissionAsync flips CanGrantAccess to false on
            // the last custodian row but leaves the row in place (Modified, not Deleted).
            await SeedSecret("secret-1");
            await SeedPermission("secret-1", SubjectType.User, "alice@test", canGrantAccess: true);

            var lastCustodian = await this.dbContext.Permissions
                .FirstAsync(p => p.SecretName == "secret-1" && p.SubjectId == "alice@test");
            lastCustodian.CanGrantAccess = false;

            var result = await this.manager.ApplyOrphanRuleAsync("secret-1", this.dbContext);
            await this.dbContext.SaveChangesAsync();

            Assert.That(result.WasOrphaned, Is.True);
        }

        [Test]
        public async Task ApplyOrphanRuleAsync_PendingAddOfNewCustodian_NoOrphan()
        {
            // Reproduces the swap-custodian scenario in PATCH: the caller stages both a
            // Remove of the existing custodian and an Add of a new custodian in the same transaction.
            // Post-state has a custodian, so no orphan should fire.
            await SeedSecret("secret-1");
            await SeedPermission("secret-1", SubjectType.User, "alice@test", canGrantAccess: true);

            var existing = await this.dbContext.Permissions
                .FirstAsync(p => p.SecretName == "secret-1" && p.SubjectId == "alice@test");
            this.dbContext.Permissions.Remove(existing);

            var newCustodian = new SubjectPermissions("secret-1", SubjectType.User, "bob@test", "bob@test")
            {
                CanRead = true,
                CanWrite = true,
                CanGrantAccess = true,
                CanRevokeAccess = true
            };
            this.dbContext.Permissions.Add(newCustodian);

            var result = await this.manager.ApplyOrphanRuleAsync("secret-1", this.dbContext);
            await this.dbContext.SaveChangesAsync();

            Assert.That(result.WasOrphaned, Is.False);
        }

        [Test]
        public async Task ApplyOrphanRuleAsync_PreExistingEarlierExpireAt_NeverExtends()
        {
            var earlierExpire = DateTimeProvider.UtcNow.AddDays(2);
            await SeedSecret("secret-1", expireAt: earlierExpire, scheduleExpiration: true);

            var result = await this.manager.ApplyOrphanRuleAsync("secret-1", this.dbContext);
            await this.dbContext.SaveChangesAsync();

            Assert.That(result.WasOrphaned, Is.True);
            var metadata = await this.dbContext.Objects.FindAsync("secret-1");
            Assert.That(metadata.ExpirationMetadata.ExpireAt, Is.EqualTo(earlierExpire));
        }

        [Test]
        public async Task ApplyOrphanRuleAsync_PreExistingLaterExpireAt_LowersToGracePeriod()
        {
            var laterExpire = DateTimeProvider.UtcNow.AddDays(30);
            await SeedSecret("secret-1", expireAt: laterExpire, scheduleExpiration: true);

            var result = await this.manager.ApplyOrphanRuleAsync("secret-1", this.dbContext);
            await this.dbContext.SaveChangesAsync();

            Assert.That(result.WasOrphaned, Is.True);
            var metadata = await this.dbContext.Objects.FindAsync("secret-1");
            Assert.That(metadata.ExpirationMetadata.ExpireAt,
                Is.EqualTo(DateTimeProvider.UtcNow.AddDays(7)));
        }

        [Test]
        public async Task ApplyOrphanRuleAsync_Idempotent_SameFinalState()
        {
            await SeedSecret("secret-1");

            var first = await this.manager.ApplyOrphanRuleAsync("secret-1", this.dbContext);
            await this.dbContext.SaveChangesAsync();

            var second = await this.manager.ApplyOrphanRuleAsync("secret-1", this.dbContext);
            await this.dbContext.SaveChangesAsync();

            Assert.That(first.ExpireAt, Is.EqualTo(second.ExpireAt));
        }

        [Test]
        public async Task PreviewAsync_NoCustodian_PredictsOrphan()
        {
            await SeedSecret("secret-1");

            var preview = await this.manager.PreviewAsync("secret-1", this.dbContext);

            Assert.That(preview.WouldOrphan, Is.True);
            Assert.That(preview.ProspectiveExpireAt,
                Is.EqualTo(DateTimeProvider.UtcNow.AddDays(7)));
        }

        [Test]
        public async Task PreviewAsync_HasCustodian_NoOrphan()
        {
            await SeedSecret("secret-1");
            await SeedPermission("secret-1", SubjectType.User, "alice@test", canGrantAccess: true);

            var preview = await this.manager.PreviewAsync("secret-1", this.dbContext);

            Assert.That(preview.WouldOrphan, Is.False);
            Assert.That(preview.ProspectiveExpireAt, Is.Null);
        }
    }
}
