/// <summary>
/// SecretAccessGiveUpTests
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
    using SafeExchange.Core.Model.Dto.Output;
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
    public class SecretAccessGiveUpTests
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
        private Features features;
        private OrphanedSecretConfiguration orphanConfig;

        private CaseSensitiveClaimsIdentity firstIdentity;
        private CaseSensitiveClaimsIdentity secondIdentity;
        private CaseSensitiveClaimsIdentity thirdIdentity;

        [OneTimeSetUp]
        public async Task OneTimeSetup()
        {
            var builder = new ConfigurationBuilder().AddUserSecrets<SecretAccessGiveUpTests>();
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
                    databaseName: $"{nameof(SecretAccessGiveUpTests)}Database",
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

            this.secretMeta = new SafeExchangeSecretMeta(
                this.testConfiguration, this.dbContext, this.tokenHelper,
                this.globalFilters, this.purger, this.permissionsManager);

            this.secretAccess = new SafeExchangeAccess(
                this.dbContext, this.groupsManager, this.tokenHelper, this.globalFilters,
                this.purger, this.permissionsManager, this.orphanedSecretManager);

            this.giveUpHandler = new SafeExchangeAccessGiveUp(
                this.dbContext, this.tokenHelper, this.globalFilters,
                this.permissionsManager, this.orphanedSecretManager, featuresOptions);

            this.firstIdentity = new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new Claim("upn", "first@test.test"),
                new Claim("displayname", "First User"),
                new Claim("oid", "00000000-0000-0000-0000-000000000001"),
                new Claim("tid", "00000000-0000-0000-0000-000000000001"),
            }.AsEnumerable());

            this.secondIdentity = new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new Claim("upn", "second@test.test"),
                new Claim("displayname", "Second User"),
                new Claim("oid", "00000000-0000-0000-0000-000000000002"),
                new Claim("tid", "00000000-0000-0000-0000-000000000001"),
            }.AsEnumerable());

            this.thirdIdentity = new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new Claim("upn", "third@test.test"),
                new Claim("displayname", "Third User"),
                new Claim("oid", "00000000-0000-0000-0000-000000000003"),
                new Claim("tid", "00000000-0000-0000-0000-000000000001"),
            }.AsEnumerable());

            var workerOptions = Options.Create(new WorkerOptions() { Serializer = new JsonObjectSerializer() });
            var serviceProviderMock = new Mock<IServiceProvider>();
            serviceProviderMock
                .Setup(x => x.GetService(typeof(IOptions<WorkerOptions>)))
                .Returns(workerOptions);
            TestFactory.FunctionContext.InstanceServices = serviceProviderMock.Object;
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
            this.orphanConfig.Ownership = OrphanOwnershipMode.UserOrApp;
            this.orphanConfig.GracePeriod = TimeSpan.FromDays(7);

            DateTimeProvider.UseSpecifiedDateTime = true;
            DateTimeProvider.SpecifiedDateTime = new DateTime(2026, 5, 6, 9, 0, 0, DateTimeKind.Utc);
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

        // ---- Tests ----

        [Test]
        public async Task GiveUpPreview_FeatureFlagOff_Returns204()
        {
            // [GIVEN] Feature flag is off and a secret exists.
            this.features.UseAccessGiveUp = false;
            await CreateSecret(this.firstIdentity, "secret-1");

            // [WHEN] A GET (preview) request is made.
            var request = TestFactory.CreateHttpRequestData("get");
            var response = await this.giveUpHandler.Run(request, "secret-1",
                new ClaimsPrincipal(this.firstIdentity), this.logger);

            // [THEN] 204 No Content is returned without processing.
            Assert.That((response as TestHttpResponseData)?.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        }

        [Test]
        public async Task GiveUpDelete_FeatureFlagOff_Returns204_NoDbWrite()
        {
            // [GIVEN] Feature flag is off and a secret exists.
            this.features.UseAccessGiveUp = false;
            await CreateSecret(this.firstIdentity, "secret-1");

            // [WHEN] A DELETE (give-up) request is made.
            var request = TestFactory.CreateHttpRequestData("delete");
            var response = await this.giveUpHandler.Run(request, "secret-1",
                new ClaimsPrincipal(this.firstIdentity), this.logger);

            // [THEN] 204 No Content is returned.
            Assert.That((response as TestHttpResponseData)?.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

            // [THEN] The permission row is still present — no write occurred.
            this.dbContext.ChangeTracker.Clear();
            var firstUserRow = await this.dbContext.Permissions
                .FirstOrDefaultAsync(p => p.SecretName == "secret-1" && p.SubjectId == "first@test.test");
            Assert.That(firstUserRow, Is.Not.Null);
        }

        [Test]
        public async Task GiveUpPreview_SecretMissing_Returns404()
        {
            // [GIVEN] No secret exists with the given name.

            // [WHEN] A GET (preview) request is made for a non-existent secret.
            var request = TestFactory.CreateHttpRequestData("get");
            var response = await this.giveUpHandler.Run(request, "no-such-secret",
                new ClaimsPrincipal(this.firstIdentity), this.logger);

            // [THEN] 404 Not Found is returned.
            Assert.That((response as TestHttpResponseData)?.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }

        [Test]
        public async Task GiveUpPreview_NoAccess_Returns403()
        {
            // [GIVEN] A secret exists that only firstIdentity can access.
            await CreateSecret(this.firstIdentity, "secret-1");

            // [WHEN] secondIdentity requests a give-up preview.
            var request = TestFactory.CreateHttpRequestData("get");
            var response = await this.giveUpHandler.Run(request, "secret-1",
                new ClaimsPrincipal(this.secondIdentity), this.logger);

            // [THEN] 403 Forbidden is returned.
            Assert.That((response as TestHttpResponseData)?.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        }

        [Test]
        public async Task GiveUpPreview_DirectRowNotLastCustodian_NotOrphan()
        {
            // [GIVEN] A secret exists with two custodians.
            await CreateSecret(this.firstIdentity, "secret-1");
            await GrantAccess(this.firstIdentity, "secret-1", "second@test.test", true, true, true, false);

            // [WHEN] secondIdentity (non-last custodian) requests a give-up preview.
            var request = TestFactory.CreateHttpRequestData("get");
            var response = await this.giveUpHandler.Run(request, "secret-1",
                new ClaimsPrincipal(this.secondIdentity), this.logger);

            // [THEN] OK, HasDirectRow=true, WouldOrphan=false, ProspectiveExpireAt=null.
            var data = (response as TestHttpResponseData)!;
            Assert.That(data.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = data.ReadBodyAsJson<BaseResponseObject<GiveUpPreviewOutput>>();
            Assert.That(body!.Result!.HasDirectRow, Is.True);
            Assert.That(body.Result.WouldOrphan, Is.False);
            Assert.That(body.Result.ProspectiveExpireAt, Is.Null);
        }

        [Test]
        public async Task GiveUpPreview_DirectRowLastCustodian_WouldOrphan()
        {
            // [GIVEN] A secret exists with only firstIdentity as custodian.
            await CreateSecret(this.firstIdentity, "secret-1");

            // [WHEN] firstIdentity (the sole custodian) requests a give-up preview.
            var request = TestFactory.CreateHttpRequestData("get");
            var response = await this.giveUpHandler.Run(request, "secret-1",
                new ClaimsPrincipal(this.firstIdentity), this.logger);

            // [THEN] OK, HasDirectRow=true, WouldOrphan=true, ProspectiveExpireAt = now + grace period.
            var data = (response as TestHttpResponseData)!;
            Assert.That(data.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = data.ReadBodyAsJson<BaseResponseObject<GiveUpPreviewOutput>>();
            Assert.That(body!.Result!.HasDirectRow, Is.True);
            Assert.That(body.Result.WouldOrphan, Is.True);
            Assert.That(body.Result.ProspectiveExpireAt, Is.EqualTo(DateTimeProvider.UtcNow.AddDays(7)));
        }

        [Test]
        public async Task GiveUpDelete_DirectRowNotLastCustodian_RemovesRowNoSchedule()
        {
            // [GIVEN] A secret exists with two custodians.
            await CreateSecret(this.firstIdentity, "secret-1");
            await GrantAccess(this.firstIdentity, "secret-1", "second@test.test", true, true, true, false);

            // [WHEN] secondIdentity gives up access.
            var request = TestFactory.CreateHttpRequestData("delete");
            var response = await this.giveUpHandler.Run(request, "secret-1",
                new ClaimsPrincipal(this.secondIdentity), this.logger);

            // [THEN] OK, HadDirectRow=true, WasOrphaned=false.
            var data = (response as TestHttpResponseData)!;
            Assert.That(data.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = data.ReadBodyAsJson<BaseResponseObject<GiveUpResultOutput>>();
            Assert.That(body!.Result!.HadDirectRow, Is.True);
            Assert.That(body.Result.WasOrphaned, Is.False);

            // [THEN] secondIdentity's permission row is gone.
            this.dbContext.ChangeTracker.Clear();
            var secondUserRow = await this.dbContext.Permissions
                .FirstOrDefaultAsync(p => p.SecretName == "secret-1" && p.SubjectId == "second@test.test");
            Assert.That(secondUserRow, Is.Null);

            // [THEN] No expiration schedule applied.
            var metadata = await this.dbContext.Objects.FindAsync("secret-1");
            Assert.That(metadata!.ExpirationMetadata.ScheduleExpiration, Is.False);
        }

        [Test]
        public async Task GiveUpDelete_LastCustodian_SchedulesGracePeriod()
        {
            // [GIVEN] A secret exists with only firstIdentity as custodian.
            await CreateSecret(this.firstIdentity, "secret-1");

            // [WHEN] firstIdentity gives up access.
            var request = TestFactory.CreateHttpRequestData("delete");
            var response = await this.giveUpHandler.Run(request, "secret-1",
                new ClaimsPrincipal(this.firstIdentity), this.logger);

            // [THEN] OK, WasOrphaned=true, ExpireAt = now + grace period.
            var data = (response as TestHttpResponseData)!;
            Assert.That(data.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = data.ReadBodyAsJson<BaseResponseObject<GiveUpResultOutput>>();
            Assert.That(body!.Result!.WasOrphaned, Is.True);
            Assert.That(body.Result.ExpireAt, Is.EqualTo(DateTimeProvider.UtcNow.AddDays(7)));

            // [THEN] Expiration schedule is applied to the metadata.
            this.dbContext.ChangeTracker.Clear();
            var metadata = await this.dbContext.Objects.FindAsync("secret-1");
            Assert.That(metadata!.ExpirationMetadata.ScheduleExpiration, Is.True);
            Assert.That(metadata.ExpirationMetadata.ExpireAt, Is.EqualTo(DateTimeProvider.UtcNow.AddDays(7)));
        }

        // ---- Helpers ----

        private async Task CreateSecret(CaseSensitiveClaimsIdentity identity, string secretName)
        {
            var claimsPrincipal = new ClaimsPrincipal(identity);
            var request = TestFactory.CreateHttpRequestData("post");
            var creationInput = new MetadataCreationInput
            {
                ExpirationSettings = new ExpirationSettingsInput
                {
                    ScheduleExpiration = false,
                    ExpireAt = DateTimeProvider.UtcNow + TimeSpan.FromDays(180),
                    ExpireOnIdleTime = false,
                    IdleTimeToExpire = TimeSpan.FromDays(180)
                }
            };

            request.SetBodyAsJson(creationInput);
            var response = await this.secretMeta.Run(request, secretName, claimsPrincipal, this.logger);
            Assert.That((response as TestHttpResponseData)?.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        private async Task GrantAccess(CaseSensitiveClaimsIdentity identity, string secretName, string subjectName, bool read, bool write, bool grantAccess, bool revokeAccess)
        {
            var accessRequest = TestFactory.CreateHttpRequestData("post");
            accessRequest.SetBodyAsJson(new List<SubjectPermissionsInput>
            {
                new SubjectPermissionsInput
                {
                    SubjectType = SubjectTypeInput.User,
                    SubjectName = subjectName,
                    SubjectId = subjectName,
                    CanRead = read, CanWrite = write,
                    CanGrantAccess = grantAccess, CanRevokeAccess = revokeAccess
                }
            });

            var response = await this.secretAccess.Run(accessRequest, secretName,
                new ClaimsPrincipal(identity), this.logger);
            Assert.That((response as TestHttpResponseData)?.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
    }
}
