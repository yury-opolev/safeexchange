/// <summary>
/// ServiceTestBase
/// </summary>

namespace SafeExchange.Tests.Services
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging.Abstractions;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Services;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// Shared plumbing for service tests: a fresh EF InMemory database per test, a fixed clock, the real
    /// purger, permissions manager and audit writer, an in-memory blob store and three user callers.
    /// </summary>
    public abstract class ServiceTestBase
    {
        protected const string OwnerUpn = "owner@services.test";

        protected const string ReaderUpn = "reader@services.test";

        protected const string StrangerUpn = "stranger@services.test";

        protected static readonly DateTime FixedNow = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);

        private DbContextOptions<SafeExchangeDbContext> dbContextOptions = null!;

        private bool previousUseSpecifiedDateTime;

        private DateTime previousSpecifiedDateTime;

        protected DbContextOptions<SafeExchangeDbContext> DbContextOptions => this.dbContextOptions;

        protected IConfiguration Configuration { get; private set; } = null!;

        protected SafeExchangeDbContext DbContext { get; private set; } = null!;

        protected TestBlobHelper BlobHelper { get; private set; } = null!;

        protected IPurger Purger { get; private set; } = null!;

        protected IPermissionsManager PermissionsManager { get; private set; } = null!;

        protected IAuditWriter AuditWriter { get; private set; } = null!;

        protected Caller Owner { get; } = UserCaller(OwnerUpn);

        protected Caller Reader { get; } = UserCaller(ReaderUpn);

        protected Caller Stranger { get; } = UserCaller(StrangerUpn);

        [SetUp]
        public void ServiceSetUp()
        {
            this.previousUseSpecifiedDateTime = DateTimeProvider.UseSpecifiedDateTime;
            this.previousSpecifiedDateTime = DateTimeProvider.SpecifiedDateTime;
            DateTimeProvider.SpecifiedDateTime = FixedNow;
            DateTimeProvider.UseSpecifiedDateTime = true;

            this.Configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "Features:UseGroupsAuthorization", "False" },
                    { "AccessTickets:AccessTicketTimeout", "00:10:00" },
                })
                .Build();

            this.dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseInMemoryDatabase($"{this.GetType().Name}-{Guid.NewGuid()}")
                .Options;
            this.DbContext = new SafeExchangeDbContext(this.dbContextOptions);
            this.BlobHelper = new TestBlobHelper();
            this.Purger = new PurgeManager(this.Configuration, this.BlobHelper, NullLogger<PurgeManager>.Instance);
            this.PermissionsManager = new PermissionsManager(this.Configuration, this.DbContext, NullLogger<PermissionsManager>.Instance);
            this.AuditWriter = new AuditWriter(new ServiceDbContextFactory(this.dbContextOptions), NullLogger<AuditWriter>.Instance);
        }

        [TearDown]
        public void ServiceTearDown()
        {
            TestDatabase.EnsureDeletedTolerant(this.DbContext.Database);
            this.DbContext.Dispose();
            DateTimeProvider.SpecifiedDateTime = this.previousSpecifiedDateTime;
            DateTimeProvider.UseSpecifiedDateTime = this.previousUseSpecifiedDateTime;
        }

        protected static Caller UserCaller(string upn)
            => new(SubjectType.User, upn, upn, Array.Empty<string>(), CallerChannels.Rest, null);

        protected static MetadataCreationInput CreationInput(bool? auditEnabled = null)
            => new()
            {
                ExpirationSettings = new ExpirationSettingsInput
                {
                    ScheduleExpiration = false,
                    ExpireAt = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    ExpireOnIdleTime = false,
                    IdleTimeToExpire = TimeSpan.FromDays(180),
                },
                AuditEnabled = auditEnabled,
            };

        protected SecretService CreateSecretService()
            => new(this.Configuration, this.DbContext, this.Purger, this.PermissionsManager, this.AuditWriter, NullLogger<SecretService>.Instance);

        protected async Task CreateSecretAsync(string secretId, bool? auditEnabled = null)
        {
            var result = await this.CreateSecretService().CreateAsync(this.Owner, secretId, CreationInput(auditEnabled));
            Assert.That(result.IsOk, Is.True, $"Arrange: secret creation failed: {result.Error}");
        }

        protected async Task GrantAsync(string secretId, string upn, PermissionType permission)
        {
            await this.PermissionsManager.SetPermissionAsync(SubjectType.User, upn, upn, secretId, permission);
            await this.DbContext.SaveChangesAsync();
        }

        protected static void AssertInsufficientPermissions(ServiceResult outcome, PermissionType permission, string secretId, string subStatus = "")
        {
            Assert.That(outcome.Status, Is.EqualTo(ServiceStatus.Forbidden));
            Assert.That(outcome.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(outcome.Error, Is.EqualTo($"Insufficient permissions to do '{permission}' action on '{secretId}'"));
            Assert.That(outcome.SubStatus, Is.EqualTo(subStatus));
            Assert.That(outcome.Detail, Is.Null);
        }

        private sealed class ServiceDbContextFactory : IDbContextFactory<SafeExchangeDbContext>
        {
            private readonly DbContextOptions<SafeExchangeDbContext> options;

            public ServiceDbContextFactory(DbContextOptions<SafeExchangeDbContext> options)
            {
                this.options = options ?? throw new ArgumentNullException(nameof(options));
            }

            public SafeExchangeDbContext CreateDbContext() => new(this.options);
        }
    }
}
