/// <summary>
/// AdminUserDetailTests — integration tests for GET v2/admin/users/{upn}.
/// All tests run against the Cosmos DB emulator via the shared CosmosTestOptions helper.
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
    using SafeExchange.Core.Functions.Admin;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    [TestFixture]
    public class AdminUserDetailTests
    {
        private ILogger logger;

        private SafeExchangeDbContext dbContext;

        private DbContextOptions<SafeExchangeDbContext> dbContextOptions;

        private GlobalFilters globalFilters;

        private IOptionsMonitor<Limits> limitsMonitor;

        private SafeExchangeAdminUsers handler;

        private CaseSensitiveClaimsIdentity adminIdentity;
        private CaseSensitiveClaimsIdentity nonAdminIdentity;

        [OneTimeSetUp]
        public async Task OneTimeSetup()
        {
            var builder = new ConfigurationBuilder().AddUserSecrets<AdminUserDetailTests>();
            var secretConfiguration = builder.Build();

            this.logger = TestFactory.CreateLogger();

            this.dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(
                    secretConfiguration.GetConnectionString("CosmosDb"),
                    databaseName: $"{nameof(AdminUserDetailTests)}Database",
                    CosmosTestOptions.UseGateway)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CosmosEventId.SyncNotSupported))
                .Options;

            this.dbContext = new SafeExchangeDbContext(this.dbContextOptions);
            await this.dbContext.Database.EnsureCreatedAsync();

            var cosmosClient = CosmosTestOptions.CreateClient(secretConfiguration.GetConnectionString("CosmosDb"));
            await cosmosClient.GetDatabase($"{nameof(AdminUserDetailTests)}Database").CreateContainerIfNotExistsAsync(
                new Microsoft.Azure.Cosmos.ContainerProperties("TelemetryIdMap", "/UserId")
                {
                    DefaultTimeToLive = 2419200,
                });

            // Admin identity: oid must match AdminConfiguration.AdminUsers.
            this.adminIdentity = new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new Claim("upn", "admin@test.test"),
                new Claim("displayname", "Admin User"),
                new Claim("oid", "00000321-0000-0000-0000-000000000321"),
                new Claim("tid", "00000000-0000-0000-0000-000000000001"),
            });

            this.nonAdminIdentity = new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new Claim("upn", "plain@test.test"),
                new Claim("displayname", "Plain User"),
                new Claim("oid", "00000000-0000-0000-0000-000000000099"),
                new Claim("tid", "00000000-0000-0000-0000-000000000001"),
            });

            var gagc = new GloballyAllowedGroupsConfiguration();
            var groupsConfiguration = Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(
                x => x.CurrentValue == gagc);

            var ac = new AdminConfiguration
            {
                AdminUsers = "00000321-0000-0000-0000-000000000321",
            };
            var adminConfiguration = Mock.Of<IOptionsMonitor<AdminConfiguration>>(
                x => x.CurrentValue == ac);

            var tokenHelper = new TestTokenHelper();
            this.globalFilters = new GlobalFilters(
                groupsConfiguration, adminConfiguration, tokenHelper,
                TestFactory.CreateLogger<GlobalFilters>());

            var limits = new Limits { AdminListDefaultPageSize = 25, AdminListMaxPageSize = 100 };
            this.limitsMonitor = Mock.Of<IOptionsMonitor<Limits>>(x => x.CurrentValue == limits);

            DateTimeProvider.SpecifiedDateTime = DateTime.UtcNow;
            DateTimeProvider.UseSpecifiedDateTime = true;

            var workerOptions = Options.Create(new WorkerOptions { Serializer = new JsonObjectSerializer() });
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
            this.handler = new SafeExchangeAdminUsers(
                this.dbContext, new TestTokenHelper(), this.globalFilters, this.limitsMonitor);

            DateTimeProvider.SpecifiedDateTime = DateTime.UtcNow;
        }

        [TearDown]
        public void Cleanup()
        {
            this.dbContext.ChangeTracker.Clear();
            this.dbContext.Users.RemoveRange(this.dbContext.Users.ToList());
            this.dbContext.Set<TelemetryIdMapEntry>().RemoveRange(this.dbContext.Set<TelemetryIdMapEntry>().ToList());
            this.dbContext.SaveChanges();
        }

        private async Task<User> SeedUserAsync(string upn)
        {
            var user = new User(
                displayName: upn,
                aadObjectId: Guid.NewGuid().ToString(),
                aadTenantId: "bbbbbbbb-0000-0000-0000-000000000001",
                aadUpn: upn,
                contactEmail: upn);
            this.dbContext.Users.Add(user);
            await this.dbContext.SaveChangesAsync();
            return user;
        }

        private async Task<UserDetailOutput> GetDetailAsync(string upn)
        {
            var request = TestFactory.CreateHttpRequestData("get");
            var principal = new ClaimsPrincipal(this.adminIdentity);
            var response = await this.handler.RunDetail(request, upn, principal, this.logger) as TestHttpResponseData;
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = response.ReadBodyAsJson<BaseResponseObject<UserDetailOutput>>();
            Assert.That(body?.Status, Is.EqualTo("ok"));
            return body!.Result!;
        }

        // -----------------------------------------------------------------------
        // 1. RunDetail returns all required fields for an existing user;
        //    Groups are NOT exposed.
        // -----------------------------------------------------------------------

        [Test]
        public async Task RunDetail_ReturnsAllFieldsForExistingUser_AndDoesNotExposeGroups()
        {
            // [GIVEN] A user exists in the database.
            var createdAt = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
            var modifiedAt = new DateTime(2025, 3, 20, 14, 30, 0, DateTimeKind.Utc);

            var user = new User(
                displayName: "Alice Smith",
                aadObjectId: "aaaaaaaa-0000-0000-0000-000000000001",
                aadTenantId: "bbbbbbbb-0000-0000-0000-000000000001",
                aadUpn: "alice@contoso.com",
                contactEmail: "alice.smith@email.com");

            user.CreatedAt = createdAt;
            user.ModifiedAt = modifiedAt;
            user.Enabled = true;
            user.ReceiveExternalNotifications = true;
            user.ConsentRequired = false;
            // Add a group to verify it does NOT appear in the response.
            user.Groups.Add(new UserGroup { AadGroupId = "group-001" });

            this.dbContext.Users.Add(user);
            await this.dbContext.SaveChangesAsync();
            this.dbContext.ChangeTracker.Clear();

            // [WHEN] Admin requests detail for the user.
            var request = TestFactory.CreateHttpRequestData("get");
            var principal = new ClaimsPrincipal(this.adminIdentity);
            var response = await this.handler.RunDetail(request, "alice@contoso.com", principal, this.logger) as TestHttpResponseData;

            // [THEN] 200 OK with all expected fields; no groups property.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var body = response.ReadBodyAsJson<BaseResponseObject<UserDetailOutput>>();
            Assert.That(body?.Status, Is.EqualTo("ok"));

            var detail = body!.Result;
            Assert.That(detail, Is.Not.Null);
            Assert.That(detail.AadUpn, Is.EqualTo("alice@contoso.com"));
            Assert.That(detail.DisplayName, Is.EqualTo("Alice Smith"));
            Assert.That(detail.ContactEmail, Is.EqualTo("alice.smith@email.com"));
            Assert.That(detail.Enabled, Is.True);
            Assert.That(detail.Id, Is.Not.Null.And.Not.Empty);
            Assert.That(detail.AadObjectId, Is.EqualTo("aaaaaaaa-0000-0000-0000-000000000001"));
            Assert.That(detail.AadTenantId, Is.EqualTo("bbbbbbbb-0000-0000-0000-000000000001"));
            Assert.That(detail.CreatedAt, Is.EqualTo(createdAt));
            Assert.That(detail.ModifiedAt, Is.EqualTo(modifiedAt));
            Assert.That(detail.ReceiveExternalNotifications, Is.True);
            Assert.That(detail.ConsentRequired, Is.False);

            // Groups must NOT be present in the serialized output.
            var rawJson = response.ReadBodyAsString();
            Assert.That(rawJson, Does.Not.Contain("\"groups\"").IgnoreCase);
        }

        // -----------------------------------------------------------------------
        // 2. RunDetail returns 404 for a missing UPN.
        // -----------------------------------------------------------------------

        [Test]
        public async Task RunDetail_Returns404ForMissingUpn()
        {
            // [GIVEN] No user with the requested UPN exists.

            // [WHEN] Admin requests detail for a non-existent user.
            var request = TestFactory.CreateHttpRequestData("get");
            var principal = new ClaimsPrincipal(this.adminIdentity);
            var response = await this.handler.RunDetail(request, "nonexistent@nowhere.com", principal, this.logger) as TestHttpResponseData;

            // [THEN] 404 Not Found with status = "not_found".
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

            var body = response.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(body?.Status, Is.EqualTo("not_found"));
            Assert.That(body?.Error, Does.Contain("nonexistent@nowhere.com"));
        }

        // -----------------------------------------------------------------------
        // 3. Admin-only: non-admin principal is rejected.
        // -----------------------------------------------------------------------

        [Test]
        public async Task RunDetail_NonAdminPrincipal_IsRejected()
        {
            // [GIVEN] A non-admin principal.
            var principal = new ClaimsPrincipal(this.nonAdminIdentity);
            var request = TestFactory.CreateHttpRequestData("get");

            // [WHEN] Non-admin calls RunDetail.
            var response = await this.handler.RunDetail(request, "any@test.test", principal, this.logger) as TestHttpResponseData;

            // [THEN] A non-200 response is returned (admin gate rejects the caller).
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.Not.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public async Task RunDetail_ReturnsCurrentTelemetryIdAndWindow()
        {
            // [GIVEN] a user with a current telemetry id
            var user = await this.SeedUserAsync("detail-tid@test.test");
            user.TelemetryId = "0123456789abcdef0123456789abcdef";
            user.TelemetryIdIssuedAt = new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc);
            user.TelemetryIdExpiresAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            await this.dbContext.SaveChangesAsync();

            // [WHEN] admin fetches detail
            var detail = await this.GetDetailAsync(user.AadUpn);

            // [THEN] current id + window are returned
            Assert.That(detail.CurrentTelemetryId, Is.EqualTo("0123456789abcdef0123456789abcdef"));
            Assert.That(detail.TelemetryIdActiveSinceUtc, Is.EqualTo(user.TelemetryIdIssuedAt));
            Assert.That(detail.TelemetryIdRotatesAtUtc, Is.EqualTo(user.TelemetryIdExpiresAt));
        }

        [Test]
        public async Task RunDetail_ListsRetiredTelemetryIds_NewestFirst()
        {
            var user = await this.SeedUserAsync("detail-hist@test.test");
            await this.dbContext.Set<TelemetryIdMapEntry>().AddRangeAsync(
                new TelemetryIdMapEntry { id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", UserId = user.Id, ValidFromUtc = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc), ValidToUtc = new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc) },
                new TelemetryIdMapEntry { id = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", UserId = user.Id, ValidFromUtc = new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc), ValidToUtc = new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc) });
            await this.dbContext.SaveChangesAsync();

            var detail = await this.GetDetailAsync(user.AadUpn);

            Assert.That(detail.RecentTelemetryIds.Count, Is.EqualTo(2));
            Assert.That(detail.RecentTelemetryIds[0].Id, Is.EqualTo("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")); // newest ValidToUtc first
            Assert.That(detail.RecentTelemetryIds[1].Id, Is.EqualTo("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        }
    }
}
