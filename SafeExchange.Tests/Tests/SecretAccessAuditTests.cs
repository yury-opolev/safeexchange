/// <summary>
/// SecretAccessAuditTests — verifies that POST grant, DELETE revoke, PATCH add,
/// PATCH remove and DELETE /access-giveup each emit one PermissionGranted /
/// PermissionRevoked audit event per input when the secret has AuditEnabled=true,
/// and zero events when AuditEnabled=false.
/// </summary>

namespace SafeExchange.Tests
{
    using Azure.Core.Serialization;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Microsoft.IdentityModel.Tokens;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Groups;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    [TestFixture]
    public class SecretAccessAuditTests
    {
        private ILogger logger;
        private SafeExchangeSecretMeta secretMeta;
        private SafeExchangeAccess secretAccess;
        private SafeExchangeAccessGiveUp giveUpHandler;
        private IConfiguration testConfiguration;
        private SafeExchangeDbContext dbContext;
        private IGroupsManager groupsManager;
        private ITokenHelper tokenHelper;
        private GlobalFilters globalFilters;
        private IBlobHelper blobHelper;
        private IPurger purger;
        private IPermissionsManager permissionsManager;
        private OrphanedSecretManager orphanedSecretManager;
        private RecordingAuditWriter accessAuditWriter;
        private RecordingAuditWriter giveUpAuditWriter;
        private Features features;
        private OrphanedSecretConfiguration orphanConfig;

        private CaseSensitiveClaimsIdentity firstIdentity;
        private CaseSensitiveClaimsIdentity secondIdentity;
        private CaseSensitiveClaimsIdentity thirdIdentity;

        [OneTimeSetUp]
        public async Task OneTimeSetup()
        {
            var builder = new ConfigurationBuilder().AddUserSecrets<SecretAccessAuditTests>();
            var secretConfiguration = builder.Build();

            this.logger = TestFactory.CreateLogger();

            var configurationValues = new Dictionary<string, string>
            {
                {"Features:UseAccessGiveUp", "True"},
                {"Features:UseGroupsAuthorization", "True"}
            };

            this.testConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(configurationValues!)
                .Build();

            var dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(secretConfiguration.GetConnectionString("CosmosDb"),
                    databaseName: $"{nameof(SecretAccessAuditTests)}Database",
                    CosmosTestOptions.UseGateway)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CosmosEventId.SyncNotSupported))
                .Options;

            this.dbContext = new SafeExchangeDbContext(dbContextOptions);
            await this.dbContext.Database.EnsureCreatedAsync();

            this.groupsManager = new GroupsManager(this.dbContext, Mock.Of<ILogger<GroupsManager>>());
            this.tokenHelper = new TestTokenHelper();

            GloballyAllowedGroupsConfiguration gagc = new GloballyAllowedGroupsConfiguration();
            var groupsConfiguration = Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(x => x.CurrentValue == gagc);

            AdminConfiguration ac = new AdminConfiguration();
            var adminConfiguration = Mock.Of<IOptionsMonitor<AdminConfiguration>>(x => x.CurrentValue == ac);

            this.globalFilters = new GlobalFilters(groupsConfiguration, adminConfiguration, this.tokenHelper, TestFactory.CreateLogger<GlobalFilters>());
            this.blobHelper = new TestBlobHelper();
            this.purger = new PurgeManager(this.testConfiguration, this.blobHelper, TestFactory.CreateLogger<PurgeManager>());
            this.permissionsManager = new PermissionsManager(this.testConfiguration, this.dbContext, TestFactory.CreateLogger<PermissionsManager>());

            this.features = new Features { UseAccessGiveUp = true, UseGroupsAuthorization = true };
            this.orphanConfig = new OrphanedSecretConfiguration { Ownership = OrphanOwnershipMode.UserOrApp, GracePeriod = TimeSpan.FromDays(7) };

            var featuresOptions = Mock.Of<IOptionsMonitor<Features>>(x => x.CurrentValue == this.features);
            var configOptions = Mock.Of<IOptionsMonitor<OrphanedSecretConfiguration>>(x => x.CurrentValue == this.orphanConfig);

            this.orphanedSecretManager = new OrphanedSecretManager(featuresOptions, configOptions, TestFactory.CreateLogger<OrphanedSecretManager>());

            this.firstIdentity = TestIdentity("first@test.test", "First User", "00000000-0000-0000-0000-000000000001");
            this.secondIdentity = TestIdentity("second@test.test", "Second User", "00000000-0000-0000-0000-000000000002");
            this.thirdIdentity = TestIdentity("third@test.test", "Third User", "00000000-0000-0000-0000-000000000003");

            var workerOptions = Options.Create(new WorkerOptions() { Serializer = new JsonObjectSerializer() });
            var serviceProviderMock = new Mock<IServiceProvider>();
            serviceProviderMock
                .Setup(x => x.GetService(typeof(IOptions<WorkerOptions>)))
                .Returns(workerOptions);
            TestFactory.FunctionContext.InstanceServices = serviceProviderMock.Object;

            var (sa, gu) = this.RebuildHandlers(featuresOptions);
            this.secretAccess = sa;
            this.giveUpHandler = gu;
        }

        [OneTimeTearDown]
        public async Task OneTimeTearDown()
        {
            await this.dbContext.Database.EnsureDeletedAsync();
            this.dbContext.Dispose();
        }

        [SetUp]
        public void SetupBeforeTest()
        {
            this.features.UseAccessGiveUp = true;
            DateTimeProvider.UseSpecifiedDateTime = true;
            DateTimeProvider.SpecifiedDateTime = new DateTime(2026, 5, 6, 9, 0, 0, DateTimeKind.Utc);

            this.accessAuditWriter = new RecordingAuditWriter();
            this.giveUpAuditWriter = new RecordingAuditWriter();

            var featuresOptions = Mock.Of<IOptionsMonitor<Features>>(x => x.CurrentValue == this.features);

            this.secretMeta = new SafeExchangeSecretMeta(
                this.testConfiguration, this.dbContext, this.tokenHelper,
                this.globalFilters, this.purger, this.permissionsManager, new NoOpAuditWriter());

            this.secretAccess = new SafeExchangeAccess(
                this.dbContext, this.groupsManager, this.tokenHelper, this.globalFilters,
                this.purger, this.permissionsManager, this.accessAuditWriter, this.orphanedSecretManager);

            this.giveUpHandler = new SafeExchangeAccessGiveUp(
                this.dbContext, this.tokenHelper, this.globalFilters,
                this.permissionsManager, this.orphanedSecretManager, this.giveUpAuditWriter, featuresOptions);
        }

        [TearDown]
        public void Cleanup()
        {
            DateTimeProvider.UseSpecifiedDateTime = false;

            this.dbContext.ChangeTracker.Clear();
            this.dbContext.Permissions.RemoveRange(this.dbContext.Permissions.ToList());
            this.dbContext.Objects.RemoveRange(this.dbContext.Objects.ToList());
            this.dbContext.SaveChanges();
        }

        // ---- Grant (POST) ----

        [Test]
        public async Task Post_GrantToUser_AuditEnabled_EmitsPermissionGranted()
        {
            await this.CreateAuditedSecret(this.firstIdentity, "auditA");

            await this.PostGrant(this.firstIdentity, "auditA", "second@test.test", read: true, write: true, grant: false, revoke: false);

            var entries = this.accessAuditWriter.Entries.Where(e => e.SecretId == "auditA").ToList();
            Assert.That(entries.Count, Is.EqualTo(1));
            Assert.That(entries[0].EventType, Is.EqualTo(SecretAuditEventType.PermissionGranted));
            Assert.That(entries[0].ActorType, Is.EqualTo(SubjectType.User));
            Assert.That(entries[0].ActorId, Is.EqualTo("first@test.test"));
        }

        [Test]
        public async Task Post_GrantToUser_AuditDisabled_EmitsNothing()
        {
            await this.CreateSecret(this.firstIdentity, "noaudit", auditEnabled: false);

            await this.PostGrant(this.firstIdentity, "noaudit", "second@test.test", read: true, write: false, grant: false, revoke: false);

            Assert.That(this.accessAuditWriter.Entries.Where(e => e.SecretId == "noaudit"), Is.Empty);
        }

        [Test]
        public async Task Post_GrantMultipleInputs_EmitsOneEventPerInput()
        {
            await this.CreateAuditedSecret(this.firstIdentity, "auditB");

            var accessRequest = TestFactory.CreateHttpRequestData("post");
            accessRequest.SetBodyAsJson(new List<SubjectPermissionsInput>
            {
                MakeInput(SubjectTypeInput.User, null, "second@test.test", read: true),
                MakeInput(SubjectTypeInput.User, null, "third@test.test", read: true, write: true),
            });
            var resp = await this.secretAccess.Run(accessRequest, "auditB", new ClaimsPrincipal(this.firstIdentity), this.logger);
            Assert.That(((TestHttpResponseData)resp).StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var entries = this.accessAuditWriter.Entries.Where(e => e.SecretId == "auditB").ToList();
            Assert.That(entries.Count, Is.EqualTo(2));
            Assert.That(entries.All(e => e.EventType == SecretAuditEventType.PermissionGranted), Is.True);
        }

        // ---- Revoke (DELETE) ----

        [Test]
        public async Task Delete_RevokeUser_AuditEnabled_EmitsPermissionRevoked()
        {
            await this.CreateAuditedSecret(this.firstIdentity, "auditC");
            await this.PostGrant(this.firstIdentity, "auditC", "second@test.test", read: true, write: true, grant: false, revoke: false);
            this.accessAuditWriter.Entries.Clear();

            await this.DeleteRevoke(this.firstIdentity, "auditC", "second@test.test", read: true, write: true, grant: false, revoke: false);

            var entries = this.accessAuditWriter.Entries.Where(e => e.SecretId == "auditC").ToList();
            Assert.That(entries.Count, Is.EqualTo(1));
            Assert.That(entries[0].EventType, Is.EqualTo(SecretAuditEventType.PermissionRevoked));
            Assert.That(entries[0].ActorType, Is.EqualTo(SubjectType.User));
            Assert.That(entries[0].ActorId, Is.EqualTo("first@test.test"));
        }

        [Test]
        public async Task Delete_RevokeUser_AuditDisabled_EmitsNothing()
        {
            await this.CreateSecret(this.firstIdentity, "noaudit2", auditEnabled: false);
            await this.PostGrant(this.firstIdentity, "noaudit2", "second@test.test", read: true, write: false, grant: false, revoke: false);
            this.accessAuditWriter.Entries.Clear();

            await this.DeleteRevoke(this.firstIdentity, "noaudit2", "second@test.test", read: true, write: false, grant: false, revoke: false);

            Assert.That(this.accessAuditWriter.Entries.Where(e => e.SecretId == "noaudit2"), Is.Empty);
        }

        // ---- PATCH add ----

        [Test]
        public async Task Patch_AddUser_AuditEnabled_EmitsPermissionGranted()
        {
            await this.CreateAuditedSecret(this.firstIdentity, "auditD");

            await this.PatchRequest(this.firstIdentity, "auditD",
                add: new() { MakeInput(SubjectTypeInput.User, null, "second@test.test", read: true) },
                remove: null);

            var entries = this.accessAuditWriter.Entries.Where(e => e.SecretId == "auditD").ToList();
            Assert.That(entries.Count, Is.EqualTo(1));
            Assert.That(entries[0].EventType, Is.EqualTo(SecretAuditEventType.PermissionGranted));
        }

        // ---- PATCH remove — regression for the audit gap fixed in SafeExchangeAccess.cs ----

        [Test]
        public async Task Patch_RemoveUser_AuditEnabled_EmitsPermissionRevoked()
        {
            await this.CreateAuditedSecret(this.firstIdentity, "auditE");
            await this.PostGrant(this.firstIdentity, "auditE", "second@test.test", read: true, write: true, grant: false, revoke: false);
            this.accessAuditWriter.Entries.Clear();

            await this.PatchRequest(this.firstIdentity, "auditE",
                add: null,
                remove: new() { MakeInput(SubjectTypeInput.User, null, "second@test.test", read: true, write: true) });

            var entries = this.accessAuditWriter.Entries.Where(e => e.SecretId == "auditE").ToList();
            Assert.That(entries.Count, Is.EqualTo(1));
            Assert.That(entries[0].EventType, Is.EqualTo(SecretAuditEventType.PermissionRevoked));
            Assert.That(entries[0].ActorId, Is.EqualTo("first@test.test"));
        }

        [Test]
        public async Task Patch_AddAndRemove_EmitsOneEventEach()
        {
            await this.CreateAuditedSecret(this.firstIdentity, "auditF");
            await this.PostGrant(this.firstIdentity, "auditF", "second@test.test", read: true, write: true, grant: false, revoke: false);
            this.accessAuditWriter.Entries.Clear();

            await this.PatchRequest(this.firstIdentity, "auditF",
                add: new() { MakeInput(SubjectTypeInput.User, null, "third@test.test", read: true) },
                remove: new() { MakeInput(SubjectTypeInput.User, null, "second@test.test", read: true, write: true) });

            var entries = this.accessAuditWriter.Entries.Where(e => e.SecretId == "auditF").ToList();
            Assert.That(entries.Count, Is.EqualTo(2));
            Assert.That(entries.Count(e => e.EventType == SecretAuditEventType.PermissionGranted), Is.EqualTo(1));
            Assert.That(entries.Count(e => e.EventType == SecretAuditEventType.PermissionRevoked), Is.EqualTo(1));
        }

        [Test]
        public async Task Patch_RemoveAndReAddSameSubject_EmitsSingleNetGrantedEvent()
        {
            // The "delete then re-add to widen permissions" flow: second@ has Read, and one PATCH both
            // removes Read and re-adds Read+Write. The net effect is a single broadening, so exactly one
            // PermissionGranted event must fire (not a Revoked+Granted pair).
            await this.CreateAuditedSecret(this.firstIdentity, "auditNet");
            await this.PostGrant(this.firstIdentity, "auditNet", "second@test.test", read: true, write: false, grant: false, revoke: false);
            this.accessAuditWriter.Entries.Clear();

            await this.PatchRequest(this.firstIdentity, "auditNet",
                add: new() { MakeInput(SubjectTypeInput.User, null, "second@test.test", read: true, write: true) },
                remove: new() { MakeInput(SubjectTypeInput.User, null, "second@test.test", read: true) });

            var entries = this.accessAuditWriter.Entries.Where(e => e.SecretId == "auditNet").ToList();
            Assert.That(entries.Count, Is.EqualTo(1));
            Assert.That(entries[0].EventType, Is.EqualTo(SecretAuditEventType.PermissionGranted));
            Assert.That(entries[0].ActorId, Is.EqualTo("first@test.test"));
        }

        // ---- Give-up — regression for the audit gap fixed in SafeExchangeAccessGiveUp.cs ----

        [Test]
        public async Task GiveUp_AuditEnabled_EmitsPermissionRevoked()
        {
            await this.CreateAuditedSecret(this.firstIdentity, "auditG");
            await this.PostGrant(this.firstIdentity, "auditG", "second@test.test", read: true, write: true, grant: true, revoke: false);

            var request = TestFactory.CreateHttpRequestData("delete");
            var resp = await this.giveUpHandler.Run(request, "auditG", new ClaimsPrincipal(this.secondIdentity), this.logger);
            Assert.That(((TestHttpResponseData)resp).StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var entries = this.giveUpAuditWriter.Entries.Where(e => e.SecretId == "auditG").ToList();
            Assert.That(entries.Count, Is.EqualTo(1));
            Assert.That(entries[0].EventType, Is.EqualTo(SecretAuditEventType.PermissionRevoked));
            Assert.That(entries[0].ActorType, Is.EqualTo(SubjectType.User));
            Assert.That(entries[0].ActorId, Is.EqualTo("second@test.test"));
        }

        [Test]
        public async Task GiveUp_AuditDisabled_EmitsNothing()
        {
            await this.CreateSecret(this.firstIdentity, "noaudit3", auditEnabled: false);
            await this.PostGrant(this.firstIdentity, "noaudit3", "second@test.test", read: true, write: true, grant: true, revoke: false);

            var request = TestFactory.CreateHttpRequestData("delete");
            var resp = await this.giveUpHandler.Run(request, "noaudit3", new ClaimsPrincipal(this.secondIdentity), this.logger);
            Assert.That(((TestHttpResponseData)resp).StatusCode, Is.EqualTo(HttpStatusCode.OK));

            Assert.That(this.giveUpAuditWriter.Entries.Where(e => e.SecretId == "noaudit3"), Is.Empty);
        }

        [Test]
        public async Task GiveUp_NoDirectRow_EmitsNothing()
        {
            await this.CreateAuditedSecret(this.firstIdentity, "auditH");

            var request = TestFactory.CreateHttpRequestData("delete");
            var resp = await this.giveUpHandler.Run(request, "auditH", new ClaimsPrincipal(this.secondIdentity), this.logger);
            // No row → 403 (HasAnyAccess gate fires before give-up).
            Assert.That(((TestHttpResponseData)resp).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

            Assert.That(this.giveUpAuditWriter.Entries.Where(e => e.SecretId == "auditH"), Is.Empty);
        }

        // ---- Helpers ----

        private (SafeExchangeAccess, SafeExchangeAccessGiveUp) RebuildHandlers(IOptionsMonitor<Features> featuresOptions)
        {
            this.accessAuditWriter = new RecordingAuditWriter();
            this.giveUpAuditWriter = new RecordingAuditWriter();
            var access = new SafeExchangeAccess(
                this.dbContext, this.groupsManager, this.tokenHelper, this.globalFilters,
                this.purger, this.permissionsManager, this.accessAuditWriter, this.orphanedSecretManager);
            var giveUp = new SafeExchangeAccessGiveUp(
                this.dbContext, this.tokenHelper, this.globalFilters,
                this.permissionsManager, this.orphanedSecretManager, this.giveUpAuditWriter, featuresOptions);
            return (access, giveUp);
        }

        private static CaseSensitiveClaimsIdentity TestIdentity(string upn, string displayName, string oid) =>
            new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new Claim("upn", upn),
                new Claim("displayname", displayName),
                new Claim("oid", oid),
                new Claim("tid", "00000000-0000-0000-0000-000000000001"),
            }.AsEnumerable());

        private static SubjectPermissionsInput MakeInput(
            SubjectTypeInput subjectType, string? subjectId, string subjectName,
            bool read = false, bool write = false, bool grant = false, bool revoke = false) =>
            new SubjectPermissionsInput
            {
                SubjectType = subjectType,
                SubjectId = subjectId!,
                SubjectName = subjectName,
                CanRead = read,
                CanWrite = write,
                CanGrantAccess = grant,
                CanRevokeAccess = revoke,
            };

        private async Task CreateAuditedSecret(CaseSensitiveClaimsIdentity identity, string secretName)
            => await this.CreateSecret(identity, secretName, auditEnabled: true);

        private async Task CreateSecret(CaseSensitiveClaimsIdentity identity, string secretName, bool auditEnabled)
        {
            var request = TestFactory.CreateHttpRequestData("post");
            request.SetBodyAsJson(new MetadataCreationInput
            {
                ExpirationSettings = new ExpirationSettingsInput
                {
                    ScheduleExpiration = false,
                    ExpireAt = DateTimeProvider.UtcNow + TimeSpan.FromDays(180),
                    ExpireOnIdleTime = false,
                    IdleTimeToExpire = TimeSpan.FromDays(180),
                },
                AuditEnabled = auditEnabled,
            });
            var resp = await this.secretMeta.Run(request, secretName, new ClaimsPrincipal(identity), this.logger);
            Assert.That(((TestHttpResponseData)resp).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        private async Task PostGrant(CaseSensitiveClaimsIdentity identity, string secretName, string targetUpn,
            bool read, bool write, bool grant, bool revoke)
        {
            var accessRequest = TestFactory.CreateHttpRequestData("post");
            accessRequest.SetBodyAsJson(new List<SubjectPermissionsInput>
            {
                MakeInput(SubjectTypeInput.User, null, targetUpn, read, write, grant, revoke),
            });
            var resp = await this.secretAccess.Run(accessRequest, secretName, new ClaimsPrincipal(identity), this.logger);
            Assert.That(((TestHttpResponseData)resp).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        private async Task DeleteRevoke(CaseSensitiveClaimsIdentity identity, string secretName, string targetUpn,
            bool read, bool write, bool grant, bool revoke)
        {
            var accessRequest = TestFactory.CreateHttpRequestData("delete");
            accessRequest.SetBodyAsJson(new List<SubjectPermissionsInput>
            {
                MakeInput(SubjectTypeInput.User, null, targetUpn, read, write, grant, revoke),
            });
            var resp = await this.secretAccess.Run(accessRequest, secretName, new ClaimsPrincipal(identity), this.logger);
            Assert.That(((TestHttpResponseData)resp).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        private async Task PatchRequest(CaseSensitiveClaimsIdentity identity, string secretName,
            List<SubjectPermissionsInput>? add, List<SubjectPermissionsInput>? remove)
        {
            var accessRequest = TestFactory.CreateHttpRequestData("patch");
            accessRequest.SetBodyAsJson(new AccessUpdateInput { Add = add, Remove = remove });
            var resp = await this.secretAccess.Run(accessRequest, secretName, new ClaimsPrincipal(identity), this.logger);
            Assert.That(((TestHttpResponseData)resp).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
    }
}
