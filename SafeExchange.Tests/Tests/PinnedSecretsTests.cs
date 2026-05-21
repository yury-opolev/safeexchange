/// <summary>
/// PinnedSecretsTests
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
    using SafeExchange.Core.Middleware;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    [TestFixture]
    public class PinnedSecretsTests
    {
        private ILogger logger;

        private IConfiguration testConfiguration;

        private SafeExchangeDbContext dbContext;

        private ITokenHelper tokenHelper;

        private GlobalFilters globalFilters;

        private IPermissionsManager permissionsManager;

        private CaseSensitiveClaimsIdentity firstIdentity;
        private CaseSensitiveClaimsIdentity secondIdentity;

        private SafeExchangePinnedSecrets pinnedSecrets;
        private SafeExchangePinnedSecretsList pinnedSecretsList;

        private DbContextOptions<SafeExchangeDbContext> dbContextOptions;

        private PinnedSecretsConfiguration pinnedSecretsConfig;

        [OneTimeSetUp]
        public async Task OneTimeSetup()
        {
            var builder = new ConfigurationBuilder().AddUserSecrets<PinnedSecretsTests>();
            var secretConfiguration = builder.Build();

            this.logger = TestFactory.CreateLogger();

            var configurationValues = new Dictionary<string, string>
            {
                {"Features:UseNotifications", "False"},
                {"Features:UseGroupsAuthorization", "False"},
            };

            this.testConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(configurationValues)
                .Build();

            this.dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(secretConfiguration.GetConnectionString("CosmosDb"), databaseName: $"{nameof(PinnedSecretsTests)}Database", CosmosTestOptions.UseGateway)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CosmosEventId.SyncNotSupported))
                .Options;

            this.dbContext = new SafeExchangeDbContext(this.dbContextOptions);
            await this.dbContext.Database.EnsureCreatedAsync();

            this.tokenHelper = new TestTokenHelper();

            GloballyAllowedGroupsConfiguration gagc = new GloballyAllowedGroupsConfiguration();
            var groupsConfiguration = Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(x => x.CurrentValue == gagc);

            AdminConfiguration ac = new AdminConfiguration();
            var adminConfiguration = Mock.Of<IOptionsMonitor<AdminConfiguration>>(x => x.CurrentValue == ac);

            this.globalFilters = new GlobalFilters(groupsConfiguration, adminConfiguration, this.tokenHelper, TestFactory.CreateLogger<GlobalFilters>());

            this.permissionsManager = new PermissionsManager(
                this.testConfiguration,
                this.dbContext,
                TestFactory.CreateLogger<PermissionsManager>());

            this.pinnedSecretsConfig = new PinnedSecretsConfiguration { MaxPinnedSecretsPerUser = 5 };

            this.firstIdentity = new CaseSensitiveClaimsIdentity(new List<Claim>()
                {
                    new Claim("upn", "first@test.test"),
                    new Claim("displayname", "First User"),
                    new Claim("oid", "00000000-0000-0000-0000-000000000001"),
                    new Claim("tid", "00000000-0000-0000-0000-000000000001"),
                }.AsEnumerable());

            this.secondIdentity = new CaseSensitiveClaimsIdentity(new List<Claim>()
                {
                    new Claim("upn", "second@test.test"),
                    new Claim("displayname", "Second User"),
                    new Claim("oid", "00000000-0000-0000-0000-000000000002"),
                    new Claim("tid", "00000000-0000-0000-0000-000000000001"),
                }.AsEnumerable());

            DateTimeProvider.SpecifiedDateTime = DateTime.UtcNow;
            DateTimeProvider.UseSpecifiedDateTime = true;

            var workerOptions = Options.Create(new WorkerOptions() { Serializer = new JsonObjectSerializer() });
            var serviceProviderMock = new Mock<IServiceProvider>();
            serviceProviderMock
                .Setup(x => x.GetService(typeof(IOptions<WorkerOptions>)))
                .Returns(workerOptions);
            TestFactory.FunctionContext.InstanceServices = serviceProviderMock.Object;
        }

        [OneTimeTearDown]
        public void OneTimeCleanup()
        {
            DateTimeProvider.UseSpecifiedDateTime = false;

            this.dbContext.Database.EnsureDeleted();
            this.dbContext.Dispose();
        }

        [SetUp]
        public void Setup()
        {
            DateTimeProvider.SpecifiedDateTime = DateTime.UtcNow;

            this.pinnedSecrets = new SafeExchangePinnedSecrets(
                this.dbContext, this.tokenHelper, this.globalFilters,
                this.permissionsManager,
                Options.Create(this.pinnedSecretsConfig));

            this.pinnedSecretsList = new SafeExchangePinnedSecretsList(
                this.dbContext, this.tokenHelper, this.globalFilters);
        }

        [TearDown]
        public void Cleanup()
        {
            this.dbContext.ChangeTracker.Clear();

            this.dbContext.Users.RemoveRange(this.dbContext.Users.ToList());
            this.dbContext.Objects.RemoveRange(this.dbContext.Objects.ToList());
            this.dbContext.Permissions.RemoveRange(this.dbContext.Permissions.ToList());
            this.dbContext.PinnedSecrets.RemoveRange(this.dbContext.PinnedSecrets.ToList());
            this.dbContext.SaveChanges();
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private async Task SeedSecretAsync(string secretName, string ownerUpn, params string[] tags)
        {
            var metadata = new ObjectMetadata(
                secretName,
                new SafeExchange.Core.Model.Dto.Input.MetadataCreationInput
                {
                    ExpirationSettings = new SafeExchange.Core.Model.Dto.Input.ExpirationSettingsInput
                    {
                        ScheduleExpiration = false,
                        ExpireAt = DateTime.UtcNow.AddYears(1),
                        ExpireOnIdleTime = false,
                        IdleTimeToExpire = TimeSpan.FromDays(30),
                    },
                    Tags = tags?.ToList() ?? new List<string>(),
                },
                $"User {ownerUpn}");

            this.dbContext.Objects.Add(metadata);

            this.dbContext.Permissions.Add(new SubjectPermissions(secretName, SubjectType.User, ownerUpn, ownerUpn)
            {
                CanRead = true,
                CanWrite = true,
                CanGrantAccess = true,
                CanRevokeAccess = true,
            });

            await this.dbContext.SaveChangesAsync();
        }

        private TestHttpRequestData CreatePinRequest(string method, string userId)
        {
            var request = TestFactory.CreateHttpRequestData(method);
            request.FunctionContext.Items[DefaultAuthenticationMiddleware.InvocationContextUserIdKey] = userId;
            return request;
        }

        private async Task<TestHttpResponseData?> PinAsync(string secretName, string userId, CaseSensitiveClaimsIdentity identity)
        {
            var request = this.CreatePinRequest("put", userId);
            var principal = new ClaimsPrincipal(identity);
            var response = await this.pinnedSecrets.Run(request, secretName, principal, this.logger);
            return response as TestHttpResponseData;
        }

        private async Task<TestHttpResponseData?> GetPinAsync(string secretName, string userId, CaseSensitiveClaimsIdentity identity)
        {
            var request = this.CreatePinRequest("get", userId);
            var principal = new ClaimsPrincipal(identity);
            var response = await this.pinnedSecrets.Run(request, secretName, principal, this.logger);
            return response as TestHttpResponseData;
        }

        private async Task<TestHttpResponseData?> UnpinAsync(string secretName, string userId, CaseSensitiveClaimsIdentity identity)
        {
            var request = this.CreatePinRequest("delete", userId);
            var principal = new ClaimsPrincipal(identity);
            var response = await this.pinnedSecrets.Run(request, secretName, principal, this.logger);
            return response as TestHttpResponseData;
        }

        private async Task<TestHttpResponseData?> ListPinsAsync(string userId, CaseSensitiveClaimsIdentity identity)
        {
            var request = this.CreatePinRequest("get", userId);
            var principal = new ClaimsPrincipal(identity);
            var response = await this.pinnedSecretsList.RunList(request, principal, this.logger);
            return response as TestHttpResponseData;
        }

        // -----------------------------------------------------------------------
        // Tests
        // -----------------------------------------------------------------------

        [Test]
        public async Task Pin_HappyPath_PersistsRowAndReturnsDto()
        {
            // [GIVEN] User A owns secret 's-1' and has Read on it.
            var ownerUpn = "first@test.test";
            await this.SeedSecretAsync("s-1", ownerUpn, "prod", "db");

            // [WHEN] User A pins 's-1'.
            var response = await this.PinAsync("s-1", ownerUpn, this.firstIdentity);

            // [THEN] 200 OK with the expected DTO.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = response.ReadBodyAsJson<BaseResponseObject<PinnedSecretOutput>>();
            Assert.That(body!.Status, Is.EqualTo("ok"));
            Assert.That(body.Result.SecretName, Is.EqualTo("s-1"));
            Assert.That(body.Result.Exists, Is.True);
            Assert.That(body.Result.CanRead, Is.True);
            Assert.That(body.Result.Tags, Is.EquivalentTo(new[] { "prod", "db" }));

            // [THEN] Pin row persisted.
            var rows = await this.dbContext.PinnedSecrets.ToListAsync();
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(rows[0].UserId, Is.EqualTo(ownerUpn));
            Assert.That(rows[0].SecretName, Is.EqualTo("s-1"));
        }

        [Test]
        public async Task Pin_ApplicationCaller_Returns403()
        {
            // [GIVEN] A token helper that classifies the caller as an application.
            var appTokenHelper = new Mock<ITokenHelper>();
            appTokenHelper.Setup(h => h.IsUserToken(It.IsAny<ClaimsPrincipal>())).Returns(false);
            appTokenHelper.Setup(h => h.GetTenantId(It.IsAny<ClaimsPrincipal>()))
                .Returns("00000000-0000-0000-0000-000000000001");
            appTokenHelper.Setup(h => h.GetApplicationClientId(It.IsAny<ClaimsPrincipal>()))
                .Returns("00000000-0000-0000-0000-aaaaaaaaaaaa");
            appTokenHelper.Setup(h => h.GetUpn(It.IsAny<ClaimsPrincipal>())).Returns(string.Empty);
            appTokenHelper.Setup(h => h.GetObjectId(It.IsAny<ClaimsPrincipal>()))
                .Returns("00000000-0000-0000-0000-999999999999");
            appTokenHelper.Setup(h => h.GetDisplayName(It.IsAny<ClaimsPrincipal>())).Returns(string.Empty);

            var appPinnedSecrets = new SafeExchangePinnedSecrets(
                this.dbContext, appTokenHelper.Object, this.globalFilters,
                this.permissionsManager, Options.Create(this.pinnedSecretsConfig));

            var appIdentity = new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new Claim("appid", "00000000-0000-0000-0000-aaaaaaaaaaaa"),
                new Claim("oid", "00000000-0000-0000-0000-999999999999"),
                new Claim("tid", "00000000-0000-0000-0000-000000000001"),
            }.AsEnumerable());

            var request = TestFactory.CreateHttpRequestData("put");
            request.FunctionContext.Items[DefaultAuthenticationMiddleware.InvocationContextUserIdKey] =
                "00000000-0000-0000-0000-999999999999";

            // [WHEN] App calls PUT.
            var raw = await appPinnedSecrets.Run(request, "s-1", new ClaimsPrincipal(appIdentity), this.logger);

            // [THEN] 403 Forbidden with the "Applications cannot use this API" message.
            var response = raw as TestHttpResponseData;
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            var body = response.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(body!.Status, Is.EqualTo("forbidden"));
            Assert.That(body.Error, Does.Contain("Applications cannot use this API"));
        }

        [Test]
        public async Task Pin_SecretMissing_Returns404()
        {
            var userUpn = "first@test.test";

            var response = await this.PinAsync("does-not-exist", userUpn, this.firstIdentity);

            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            var body = response.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(body!.Status, Is.EqualTo("not_found"));
            Assert.That(body.Error, Does.Contain("does not exist"));

            // No pin row created.
            Assert.That(await this.dbContext.PinnedSecrets.CountAsync(), Is.EqualTo(0));
        }

        [Test]
        public async Task Pin_NoReadPermission_Returns403()
        {
            // [GIVEN] First user owns s-1 (and has Read). Second user has nothing.
            var ownerUpn = "first@test.test";
            await this.SeedSecretAsync("s-1", ownerUpn);

            var secondUserUpn = "second@test.test";

            // [WHEN] Second user tries to pin s-1.
            var response = await this.PinAsync("s-1", secondUserUpn, this.secondIdentity);

            // [THEN] 403 with InsufficientPermissions shape.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

            // No pin row created.
            var rows = await this.dbContext.PinnedSecrets.Where(p => p.UserId == secondUserUpn).ToListAsync();
            Assert.That(rows.Count, Is.EqualTo(0));
        }

        [Test]
        public async Task Pin_Idempotent_DoesNotDuplicate()
        {
            var userUpn = "first@test.test";
            await this.SeedSecretAsync("s-1", userUpn);

            // [WHEN] User pins twice.
            var first = await this.PinAsync("s-1", userUpn, this.firstIdentity);
            var second = await this.PinAsync("s-1", userUpn, this.firstIdentity);

            // [THEN] Both succeed.
            Assert.That(first!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(second!.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // [THEN] Only one row persisted.
            var rows = await this.dbContext.PinnedSecrets.ToListAsync();
            Assert.That(rows.Count, Is.EqualTo(1));
        }

        [Test]
        public async Task Pin_CapExceeded_Returns400()
        {
            var userUpn = "first@test.test";
            var max = this.pinnedSecretsConfig.MaxPinnedSecretsPerUser; // 5

            // [GIVEN] 5 secrets already pinned (max).
            for (int i = 1; i <= max; i++)
            {
                await this.SeedSecretAsync($"s-{i}", userUpn);
                var ok = await this.PinAsync($"s-{i}", userUpn, this.firstIdentity);
                Assert.That(ok!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            }

            // [WHEN] Tries to pin a 6th.
            await this.SeedSecretAsync("s-6", userUpn);
            var response = await this.PinAsync("s-6", userUpn, this.firstIdentity);

            // [THEN] 400 with cap-exceeded message.
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            var body = response.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(body!.Error, Is.EqualTo(
                "Pinned secret count is 5, which is higher or equal than allowed no. of 5 pinned secrets. Please unpin secrets before adding new ones."));

            // [THEN] Still only 5 rows.
            Assert.That(await this.dbContext.PinnedSecrets.CountAsync(), Is.EqualTo(max));
        }

        [Test]
        public async Task GetPin_NotPinned_ReturnsNoContent()
        {
            var userId = "first@test.test";
            await this.SeedSecretAsync("s-1", userId);

            var response = await this.GetPinAsync("s-1", userId, this.firstIdentity);

            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = response.ReadBodyAsJson<BaseResponseObject<PinnedSecretOutput>>();
            Assert.That(body!.Status, Is.EqualTo("no_content"));
            Assert.That(body.Result, Is.Null);
        }

        [Test]
        public async Task GetPin_LiveSecret_ReturnsDto()
        {
            var userId = "first@test.test";
            await this.SeedSecretAsync("s-1", userId, "prod");
            await this.PinAsync("s-1", userId, this.firstIdentity);

            var response = await this.GetPinAsync("s-1", userId, this.firstIdentity);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var body = response.ReadBodyAsJson<BaseResponseObject<PinnedSecretOutput>>();
            Assert.That(body!.Status, Is.EqualTo("ok"));
            Assert.That(body.Result.SecretName, Is.EqualTo("s-1"));
            Assert.That(body.Result.Exists, Is.True);
            Assert.That(body.Result.CanRead, Is.True);
            Assert.That(body.Result.Tags, Is.EquivalentTo(new[] { "prod" }));
        }

        [Test]
        public async Task GetPin_AccessLost_ReturnsDtoWithCanReadFalse()
        {
            var userId = "first@test.test";
            await this.SeedSecretAsync("s-1", userId, "prod");
            await this.PinAsync("s-1", userId, this.firstIdentity);

            // [GIVEN] User's permission row removed (admin revoked).
            var perm = await this.dbContext.Permissions.FirstAsync(
                p => p.SecretName == "s-1" && p.SubjectId == userId);
            this.dbContext.Permissions.Remove(perm);
            await this.dbContext.SaveChangesAsync();

            var response = await this.GetPinAsync("s-1", userId, this.firstIdentity);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = response.ReadBodyAsJson<BaseResponseObject<PinnedSecretOutput>>();
            Assert.That(body!.Status, Is.EqualTo("ok"));
            Assert.That(body.Result.SecretName, Is.EqualTo("s-1"));
            Assert.That(body.Result.Exists, Is.True);
            Assert.That(body.Result.CanRead, Is.False);
            Assert.That(body.Result.CanWrite, Is.False);
            Assert.That(body.Result.CanGrantAccess, Is.False);
            Assert.That(body.Result.CanRevokeAccess, Is.False);
            Assert.That(body.Result.Tags, Is.Empty);
        }

        [Test]
        public async Task GetPin_SecretDeleted_ReturnsDtoWithExistsFalse()
        {
            var userId = "first@test.test";
            await this.SeedSecretAsync("s-1", userId);
            await this.PinAsync("s-1", userId, this.firstIdentity);

            // [GIVEN] Secret deleted, permission row deleted, pin row remains.
            var perm = await this.dbContext.Permissions.FirstAsync(
                p => p.SecretName == "s-1" && p.SubjectId == userId);
            var metadata = await this.dbContext.Objects.FirstAsync(o => o.ObjectName == "s-1");
            this.dbContext.Permissions.Remove(perm);
            this.dbContext.Objects.Remove(metadata);
            await this.dbContext.SaveChangesAsync();

            var response = await this.GetPinAsync("s-1", userId, this.firstIdentity);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = response.ReadBodyAsJson<BaseResponseObject<PinnedSecretOutput>>();
            Assert.That(body!.Status, Is.EqualTo("ok"));
            Assert.That(body.Result.SecretName, Is.EqualTo("s-1"));
            Assert.That(body.Result.Exists, Is.False);
            Assert.That(body.Result.CanRead, Is.False);
            Assert.That(body.Result.Tags, Is.Empty);
        }

        [Test]
        public async Task Unpin_HappyPath_RemovesRow()
        {
            var userId = "first@test.test";
            await this.SeedSecretAsync("s-1", userId);
            await this.PinAsync("s-1", userId, this.firstIdentity);
            Assert.That(await this.dbContext.PinnedSecrets.CountAsync(), Is.EqualTo(1));

            var response = await this.UnpinAsync("s-1", userId, this.firstIdentity);

            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = response.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.That(body!.Status, Is.EqualTo("ok"));
            Assert.That(body.Result, Is.EqualTo("ok"));

            Assert.That(await this.dbContext.PinnedSecrets.CountAsync(), Is.EqualTo(0));
        }

        [Test]
        public async Task Unpin_NoExistingPin_ReturnsNoContent()
        {
            var userId = "first@test.test";

            var response = await this.UnpinAsync("never-pinned", userId, this.firstIdentity);

            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = response.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.That(body!.Status, Is.EqualTo("no_content"));
            Assert.That(body.Result, Does.Contain("does not exist"));
        }

        [Test]
        public async Task Unpin_AfterAccessLoss_StillSucceeds()
        {
            var userId = "first@test.test";
            await this.SeedSecretAsync("s-1", userId);
            await this.PinAsync("s-1", userId, this.firstIdentity);

            // Revoke permission row (admin-driven scenario).
            var perm = await this.dbContext.Permissions.FirstAsync(
                p => p.SecretName == "s-1" && p.SubjectId == userId);
            this.dbContext.Permissions.Remove(perm);
            await this.dbContext.SaveChangesAsync();

            var response = await this.UnpinAsync("s-1", userId, this.firstIdentity);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = response.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.That(body!.Status, Is.EqualTo("ok"));

            Assert.That(await this.dbContext.PinnedSecrets.CountAsync(), Is.EqualTo(0));
        }

        [Test]
        public async Task List_NoPins_ReturnsNoContentEmptyList()
        {
            var userId = "first@test.test";

            var response = await this.ListPinsAsync(userId, this.firstIdentity);

            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = response.ReadBodyAsJson<BaseResponseObject<List<PinnedSecretOutput>>>();
            Assert.That(body!.Status, Is.EqualTo("no_content"));
            Assert.That(body.Result, Is.Empty);
        }

        [Test]
        public async Task List_MultiplePins_SortedDescByCreatedAt()
        {
            var userId = "first@test.test";
            await this.SeedSecretAsync("s-1", userId);
            await this.SeedSecretAsync("s-2", userId);
            await this.SeedSecretAsync("s-3", userId);

            // [GIVEN] Pin s-1, advance time, pin s-2, advance time, pin s-3.
            DateTimeProvider.SpecifiedDateTime = DateTime.UtcNow;
            await this.PinAsync("s-1", userId, this.firstIdentity);
            DateTimeProvider.SpecifiedDateTime = DateTimeProvider.SpecifiedDateTime.AddSeconds(10);
            await this.PinAsync("s-2", userId, this.firstIdentity);
            DateTimeProvider.SpecifiedDateTime = DateTimeProvider.SpecifiedDateTime.AddSeconds(10);
            await this.PinAsync("s-3", userId, this.firstIdentity);

            var response = await this.ListPinsAsync(userId, this.firstIdentity);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = response.ReadBodyAsJson<BaseResponseObject<List<PinnedSecretOutput>>>();
            Assert.That(body!.Status, Is.EqualTo("ok"));
            Assert.That(body.Result.Count, Is.EqualTo(3));

            // [THEN] Newest first.
            Assert.That(body.Result[0].SecretName, Is.EqualTo("s-3"));
            Assert.That(body.Result[1].SecretName, Is.EqualTo("s-2"));
            Assert.That(body.Result[2].SecretName, Is.EqualTo("s-1"));
        }

        [Test]
        public async Task List_MixedStates_AllReturnedWithFlags()
        {
            var userId = "first@test.test";

            // [GIVEN] Three secrets pinned, then one's permission revoked and one deleted.
            await this.SeedSecretAsync("s-live", userId, "tag1");
            await this.SeedSecretAsync("s-noaccess", userId, "tag2");
            await this.SeedSecretAsync("s-deleted", userId, "tag3");

            DateTimeProvider.SpecifiedDateTime = DateTime.UtcNow;
            await this.PinAsync("s-live", userId, this.firstIdentity);
            DateTimeProvider.SpecifiedDateTime = DateTimeProvider.SpecifiedDateTime.AddSeconds(10);
            await this.PinAsync("s-noaccess", userId, this.firstIdentity);
            DateTimeProvider.SpecifiedDateTime = DateTimeProvider.SpecifiedDateTime.AddSeconds(10);
            await this.PinAsync("s-deleted", userId, this.firstIdentity);

            // Revoke permission on s-noaccess.
            var perm = await this.dbContext.Permissions.FirstAsync(
                p => p.SecretName == "s-noaccess" && p.SubjectId == userId);
            this.dbContext.Permissions.Remove(perm);

            // Delete s-deleted entirely (metadata + permission row).
            var meta = await this.dbContext.Objects.FirstAsync(o => o.ObjectName == "s-deleted");
            var deletedPerm = await this.dbContext.Permissions.FirstAsync(
                p => p.SecretName == "s-deleted" && p.SubjectId == userId);
            this.dbContext.Objects.Remove(meta);
            this.dbContext.Permissions.Remove(deletedPerm);

            await this.dbContext.SaveChangesAsync();

            var response = await this.ListPinsAsync(userId, this.firstIdentity);
            var body = response!.ReadBodyAsJson<BaseResponseObject<List<PinnedSecretOutput>>>();
            Assert.That(body!.Result.Count, Is.EqualTo(3));

            // Order: s-deleted (newest), s-noaccess, s-live.
            var deleted = body.Result[0];
            Assert.That(deleted.SecretName, Is.EqualTo("s-deleted"));
            Assert.That(deleted.Exists, Is.False);
            Assert.That(deleted.CanRead, Is.False);
            Assert.That(deleted.Tags, Is.Empty);

            var noaccess = body.Result[1];
            Assert.That(noaccess.SecretName, Is.EqualTo("s-noaccess"));
            Assert.That(noaccess.Exists, Is.True);
            Assert.That(noaccess.CanRead, Is.False);
            Assert.That(noaccess.Tags, Is.Empty);

            var live = body.Result[2];
            Assert.That(live.SecretName, Is.EqualTo("s-live"));
            Assert.That(live.Exists, Is.True);
            Assert.That(live.CanRead, Is.True);
            Assert.That(live.Tags, Is.EquivalentTo(new[] { "tag1" }));
        }

        [Test]
        public async Task List_TwoUsers_EachSeesOwnPinsOnly()
        {
            var firstUserId = "first@test.test";
            var secondUserId = "second@test.test";

            await this.SeedSecretAsync("s-1", firstUserId);
            await this.SeedSecretAsync("s-2", secondUserId);

            await this.PinAsync("s-1", firstUserId, this.firstIdentity);
            await this.PinAsync("s-2", secondUserId, this.secondIdentity);

            var firstResponse = await this.ListPinsAsync(firstUserId, this.firstIdentity);
            var firstBody = firstResponse!.ReadBodyAsJson<BaseResponseObject<List<PinnedSecretOutput>>>();
            Assert.That(firstBody!.Result.Count, Is.EqualTo(1));
            Assert.That(firstBody.Result[0].SecretName, Is.EqualTo("s-1"));

            var secondResponse = await this.ListPinsAsync(secondUserId, this.secondIdentity);
            var secondBody = secondResponse!.ReadBodyAsJson<BaseResponseObject<List<PinnedSecretOutput>>>();
            Assert.That(secondBody!.Result.Count, Is.EqualTo(1));
            Assert.That(secondBody.Result[0].SecretName, Is.EqualTo("s-2"));
        }

        [Test]
        public async Task List_ApplicationCaller_Returns403()
        {
            var appTokenHelper = new Mock<ITokenHelper>();
            appTokenHelper.Setup(h => h.IsUserToken(It.IsAny<ClaimsPrincipal>())).Returns(false);
            appTokenHelper.Setup(h => h.GetTenantId(It.IsAny<ClaimsPrincipal>()))
                .Returns("00000000-0000-0000-0000-000000000001");
            appTokenHelper.Setup(h => h.GetApplicationClientId(It.IsAny<ClaimsPrincipal>()))
                .Returns("00000000-0000-0000-0000-aaaaaaaaaaaa");
            appTokenHelper.Setup(h => h.GetUpn(It.IsAny<ClaimsPrincipal>())).Returns(string.Empty);
            appTokenHelper.Setup(h => h.GetObjectId(It.IsAny<ClaimsPrincipal>()))
                .Returns("00000000-0000-0000-0000-999999999999");
            appTokenHelper.Setup(h => h.GetDisplayName(It.IsAny<ClaimsPrincipal>())).Returns(string.Empty);

            var appList = new SafeExchangePinnedSecretsList(this.dbContext, appTokenHelper.Object, this.globalFilters);

            var appIdentity = new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new Claim("appid", "00000000-0000-0000-0000-aaaaaaaaaaaa"),
                new Claim("oid", "00000000-0000-0000-0000-999999999999"),
                new Claim("tid", "00000000-0000-0000-0000-000000000001"),
            }.AsEnumerable());

            var request = TestFactory.CreateHttpRequestData("get");
            request.FunctionContext.Items[DefaultAuthenticationMiddleware.InvocationContextUserIdKey] =
                "00000000-0000-0000-0000-999999999999";

            var raw = await appList.RunList(request, new ClaimsPrincipal(appIdentity), this.logger);
            var response = raw as TestHttpResponseData;
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            var body = response.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(body!.Error, Does.Contain("Applications cannot use this API"));
        }
    }
}
