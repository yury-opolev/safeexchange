/// <summary>
/// AdminSecretsTests — integration tests for the admin secrets overview endpoints.
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
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    [TestFixture]
    public class AdminSecretsTests
    {
        private ILogger logger;

        private SafeExchangeDbContext dbContext;

        private DbContextOptions<SafeExchangeDbContext> dbContextOptions;

        private GlobalFilters globalFilters;

        private IOptionsMonitor<Limits> limitsMonitor;

        private SafeExchangeAdminSecrets handler;

        private CaseSensitiveClaimsIdentity adminIdentity;
        private CaseSensitiveClaimsIdentity nonAdminIdentity;

        [OneTimeSetUp]
        public async Task OneTimeSetup()
        {
            var builder = new ConfigurationBuilder().AddUserSecrets<AdminSecretsTests>();
            var secretConfiguration = builder.Build();

            this.logger = TestFactory.CreateLogger();

            this.dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(
                    secretConfiguration.GetConnectionString("CosmosDb"),
                    databaseName: $"{nameof(AdminSecretsTests)}Database",
                    CosmosTestOptions.UseGateway)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CosmosEventId.SyncNotSupported))
                .Options;

            this.dbContext = new SafeExchangeDbContext(this.dbContextOptions);
            await this.dbContext.Database.EnsureCreatedAsync();

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
            this.handler = new SafeExchangeAdminSecrets(
                this.dbContext, this.globalFilters, this.limitsMonitor);

            DateTimeProvider.SpecifiedDateTime = DateTime.UtcNow;
        }

        [TearDown]
        public void Cleanup()
        {
            this.dbContext.ChangeTracker.Clear();
            this.dbContext.Objects.RemoveRange(this.dbContext.Objects.ToList());
            this.dbContext.Permissions.RemoveRange(this.dbContext.Permissions.ToList());
            this.dbContext.SaveChanges();
        }

        // -----------------------------------------------------------------------
        // 1. List: pagination Total is correct and items contain names.
        // -----------------------------------------------------------------------

        [Test]
        public async Task List_PaginationTotal_IsCorrect()
        {
            // [GIVEN] Two secrets.
            var secret1 = this.CreateSecret("list-test-1", "creator@test.test");
            this.dbContext.Objects.Add(secret1);

            var secret2 = this.CreateSecret("list-test-2", "creator@test.test");
            this.dbContext.Objects.Add(secret2);

            await this.dbContext.SaveChangesAsync();
            this.dbContext.ChangeTracker.Clear();

            // [WHEN] Admin lists secrets.
            var request = TestFactory.CreateHttpRequestData("get");
            var principal = new ClaimsPrincipal(this.adminIdentity);
            var response = await this.handler.RunList(request, principal, this.logger) as TestHttpResponseData;

            // [THEN] Total = 2 and both names are present; no AttachmentCount on list items.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var body = response.ReadBodyAsJson<BaseResponseObject<PaginatedResult<SecretAdminOverviewOutput>>>();
            Assert.That(body?.Status, Is.EqualTo("ok"));
            Assert.That(body!.Result.Total, Is.EqualTo(2));

            var names = body.Result.Items.Select(i => i.ObjectName).ToList();
            Assert.That(names, Has.Member("list-test-1"));
            Assert.That(names, Has.Member("list-test-2"));
        }

        // -----------------------------------------------------------------------
        // 2. List: sortBy=lastAccessed&sortDir=asc → oldest LastAccessedAt first.
        // -----------------------------------------------------------------------

        [Test]
        public async Task List_SortByLastAccessedAsc_ReturnsOldestFirst()
        {
            // [GIVEN] Three secrets with different LastAccessedAt values.
            var baseTime = DateTimeProvider.UtcNow;

            DateTimeProvider.SpecifiedDateTime = baseTime.AddDays(-10);
            var oldest = this.CreateSecret("sort-old", "creator@test.test");
            this.dbContext.Objects.Add(oldest);

            DateTimeProvider.SpecifiedDateTime = baseTime.AddDays(-1);
            var newest = this.CreateSecret("sort-new", "creator@test.test");
            this.dbContext.Objects.Add(newest);

            DateTimeProvider.SpecifiedDateTime = baseTime.AddDays(-5);
            var middle = this.CreateSecret("sort-mid", "creator@test.test");
            this.dbContext.Objects.Add(middle);

            await this.dbContext.SaveChangesAsync();
            this.dbContext.ChangeTracker.Clear();

            // [WHEN] Admin lists with sortBy=lastAccessed&sortDir=asc.
            var request = TestFactory.CreateHttpRequestData("get");
            request.SetQueryString("sortBy=lastAccessed&sortDir=asc");
            var principal = new ClaimsPrincipal(this.adminIdentity);
            var response = await this.handler.RunList(request, principal, this.logger) as TestHttpResponseData;

            // [THEN] Items are returned oldest last-accessed first.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var body = response.ReadBodyAsJson<BaseResponseObject<PaginatedResult<SecretAdminOverviewOutput>>>();
            Assert.That(body?.Status, Is.EqualTo("ok"));
            Assert.That(body!.Result.Items.Count, Is.GreaterThanOrEqualTo(3));

            var names = body.Result.Items.Select(i => i.ObjectName).ToList();
            var oldestIdx = names.IndexOf("sort-old");
            var middleIdx = names.IndexOf("sort-mid");
            var newestIdx = names.IndexOf("sort-new");
            Assert.That(oldestIdx, Is.LessThan(middleIdx));
            Assert.That(middleIdx, Is.LessThan(newestIdx));
        }

        // -----------------------------------------------------------------------
        // 3. List: accessedBefore boundary.
        // -----------------------------------------------------------------------

        [Test]
        public async Task List_AccessedBefore_BoundaryIsCorrect()
        {
            // [GIVEN] Three secrets with LastAccessedAt at T-10, T-5, and T-1 days.
            var baseTime = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
            var cutoff = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-3); // T-3

            DateTimeProvider.SpecifiedDateTime = baseTime.AddDays(-10);
            var before1 = this.CreateSecret("before-cutoff-1", "creator@test.test");
            this.dbContext.Objects.Add(before1);

            DateTimeProvider.SpecifiedDateTime = baseTime.AddDays(-5);
            var before2 = this.CreateSecret("before-cutoff-2", "creator@test.test");
            this.dbContext.Objects.Add(before2);

            DateTimeProvider.SpecifiedDateTime = baseTime.AddDays(-1);
            var after = this.CreateSecret("after-cutoff-1", "creator@test.test");
            this.dbContext.Objects.Add(after);

            await this.dbContext.SaveChangesAsync();
            this.dbContext.ChangeTracker.Clear();

            // [WHEN] Admin lists with accessedBefore = cutoff.
            var request = TestFactory.CreateHttpRequestData("get");
            request.SetQueryString($"accessedBefore={Uri.EscapeDataString(cutoff.ToString("O"))}");
            var principal = new ClaimsPrincipal(this.adminIdentity);
            var response = await this.handler.RunList(request, principal, this.logger) as TestHttpResponseData;

            // [THEN] Only secrets accessed before the cutoff are returned.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var body = response.ReadBodyAsJson<BaseResponseObject<PaginatedResult<SecretAdminOverviewOutput>>>();
            Assert.That(body?.Status, Is.EqualTo("ok"));
            Assert.That(body!.Result.Items.Any(i => i.ObjectName == "before-cutoff-1"), Is.True);
            Assert.That(body.Result.Items.Any(i => i.ObjectName == "before-cutoff-2"), Is.True);
            Assert.That(body.Result.Items.Any(i => i.ObjectName == "after-cutoff-1"), Is.False);
        }

        // -----------------------------------------------------------------------
        // 4. List: q matches name.
        // -----------------------------------------------------------------------

        [Test]
        public async Task List_SearchQuery_MatchesName()
        {
            // [GIVEN] Three secrets with distinct names.
            var s1 = this.CreateSecret("alpha-secret-1", "creator@test.test");
            var s2 = this.CreateSecret("beta-secret-1", "creator@test.test");
            var s3 = this.CreateSecret("alpha-secret-2", "creator@test.test");
            this.dbContext.Objects.AddRange(s1, s2, s3);
            await this.dbContext.SaveChangesAsync();
            this.dbContext.ChangeTracker.Clear();

            // [WHEN] Admin searches with q=alpha.
            var request = TestFactory.CreateHttpRequestData("get");
            request.SetQueryString("q=alpha");
            var principal = new ClaimsPrincipal(this.adminIdentity);
            var response = await this.handler.RunList(request, principal, this.logger) as TestHttpResponseData;

            // [THEN] Only secrets whose name contains "alpha" are returned.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var body = response.ReadBodyAsJson<BaseResponseObject<PaginatedResult<SecretAdminOverviewOutput>>>();
            Assert.That(body?.Status, Is.EqualTo("ok"));
            var names = body!.Result.Items.Select(i => i.ObjectName).ToList();
            Assert.That(names, Has.Member("alpha-secret-1"));
            Assert.That(names, Has.Member("alpha-secret-2"));
            Assert.That(names, Has.No.Member("beta-secret-1"));
        }

        // -----------------------------------------------------------------------
        // 5. Detail: returns full metadata including attachments, tags, audit fields.
        // -----------------------------------------------------------------------

        [Test]
        public async Task Detail_ReturnsMetadataForExistingSecret()
        {
            // [GIVEN] A secret exists with tags.
            var secret = this.CreateSecret("detail-test-1", "owner@test.test");
            secret.Tags = new List<string> { "tag1", "tag2" };
            this.dbContext.Objects.Add(secret);
            await this.dbContext.SaveChangesAsync();
            this.dbContext.ChangeTracker.Clear();

            // [WHEN] Admin requests detail.
            var request = TestFactory.CreateHttpRequestData("get");
            var principal = new ClaimsPrincipal(this.adminIdentity);
            var response = await this.handler.RunDetail(request, "detail-test-1", principal, this.logger) as TestHttpResponseData;

            // [THEN] Full metadata is returned without content bytes.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var body = response.ReadBodyAsJson<BaseResponseObject<SecretAdminDetailOutput>>();
            Assert.That(body?.Status, Is.EqualTo("ok"));
            Assert.That(body!.Result.ObjectName, Is.EqualTo("detail-test-1"));
            Assert.That(body.Result.CreatedBy, Is.EqualTo("owner@test.test"));
            Assert.That(body.Result.Tags, Is.EquivalentTo(new[] { "tag1", "tag2" }));
        }

        [Test]
        public async Task Detail_AttachmentCount_ExcludesIsMain()
        {
            // [GIVEN] A secret with one IsMain + two attachment content items.
            var secret = this.CreateSecret("detail-attach-1", "creator@test.test");
            secret.Content.Add(new ContentMetadata { ContentName = ContentMetadata.NewName(), IsMain = false });
            secret.Content.Add(new ContentMetadata { ContentName = ContentMetadata.NewName(), IsMain = false });
            this.dbContext.Objects.Add(secret);
            await this.dbContext.SaveChangesAsync();
            this.dbContext.ChangeTracker.Clear();

            // [WHEN] Admin requests detail.
            var request = TestFactory.CreateHttpRequestData("get");
            var principal = new ClaimsPrincipal(this.adminIdentity);
            var response = await this.handler.RunDetail(request, "detail-attach-1", principal, this.logger) as TestHttpResponseData;

            // [THEN] AttachmentCount = 2 (excludes IsMain item).
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var body = response.ReadBodyAsJson<BaseResponseObject<SecretAdminDetailOutput>>();
            Assert.That(body?.Status, Is.EqualTo("ok"));
            Assert.That(body!.Result.AttachmentCount, Is.EqualTo(2));
        }

        [Test]
        public async Task Detail_Returns404ForMissingSecret()
        {
            // [GIVEN] No secret with the given name exists.

            // [WHEN] Admin requests detail for non-existent secret.
            var request = TestFactory.CreateHttpRequestData("get");
            var principal = new ClaimsPrincipal(this.adminIdentity);
            var response = await this.handler.RunDetail(request, "nonexistent-secret", principal, this.logger) as TestHttpResponseData;

            // [THEN] 404 Not Found is returned.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

            var body = response.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(body?.Status, Is.EqualTo("not_found"));
        }

        // -----------------------------------------------------------------------
        // 6. Detail: audit-enabled secret exposes AuditInstanceId.
        // -----------------------------------------------------------------------

        [Test]
        public async Task Detail_AuditEnabledSecret_ReturnsAuditInstanceId()
        {
            // [GIVEN] A secret with audit enabled and a known AuditInstanceId.
            var secret = this.CreateSecret("audit-detail-1", "owner@test.test");
            secret.AuditEnabled = true;
            var expectedAuditId = "11111111-2222-3333-4444-555555555555";
            secret.AuditInstanceId = expectedAuditId;
            this.dbContext.Objects.Add(secret);
            await this.dbContext.SaveChangesAsync();
            this.dbContext.ChangeTracker.Clear();

            // [WHEN] Admin requests detail.
            var request = TestFactory.CreateHttpRequestData("get");
            var principal = new ClaimsPrincipal(this.adminIdentity);
            var response = await this.handler.RunDetail(request, "audit-detail-1", principal, this.logger) as TestHttpResponseData;

            // [THEN] AuditEnabled is true and AuditInstanceId matches.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var body = response.ReadBodyAsJson<BaseResponseObject<SecretAdminDetailOutput>>();
            Assert.That(body?.Status, Is.EqualTo("ok"));
            Assert.That(body!.Result.AuditEnabled, Is.True);
            Assert.That(body.Result.AuditInstanceId, Is.EqualTo(expectedAuditId));
        }

        // -----------------------------------------------------------------------
        // 7. Access: returns one item per SubjectPermissions row, correct flags + SubjectType.
        // -----------------------------------------------------------------------

        [Test]
        public async Task Access_ReturnsOneItemPerPermissionRow_WithCorrectFlagsAndSubjectType()
        {
            // [GIVEN] A secret with two permission rows: one User, one Group.
            var secret = this.CreateSecret("access-test-1", "creator@test.test");
            this.dbContext.Objects.Add(secret);

            var userPerm = new SubjectPermissions("access-test-1", SubjectType.User, "alice@test.test")
            {
                CanRead = true,
                CanWrite = false,
                CanGrantAccess = true,
                CanRevokeAccess = false,
            };

            var groupPerm = new SubjectPermissions("access-test-1", SubjectType.Group, "MyTeam", "group-id-001")
            {
                CanRead = true,
                CanWrite = true,
                CanGrantAccess = false,
                CanRevokeAccess = true,
            };

            this.dbContext.Permissions.AddRange(userPerm, groupPerm);
            await this.dbContext.SaveChangesAsync();
            this.dbContext.ChangeTracker.Clear();

            // [WHEN] Admin requests access list.
            var request = TestFactory.CreateHttpRequestData("get");
            var principal = new ClaimsPrincipal(this.adminIdentity);
            var response = await this.handler.RunAccess(request, "access-test-1", principal, this.logger) as TestHttpResponseData;

            // [THEN] Two items returned with correct properties.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var body = response.ReadBodyAsJson<BaseResponseObject<List<SecretAccessItemOutput>>>();
            Assert.That(body?.Status, Is.EqualTo("ok"));
            Assert.That(body!.Result.Count, Is.EqualTo(2));

            var alice = body.Result.FirstOrDefault(i => i.SubjectName == "alice@test.test");
            Assert.That(alice, Is.Not.Null);
            Assert.That(alice!.SubjectType, Is.EqualTo("User"));
            Assert.That(alice.CanRead, Is.True);
            Assert.That(alice.CanWrite, Is.False);
            Assert.That(alice.CanGrantAccess, Is.True);
            Assert.That(alice.CanRevokeAccess, Is.False);

            var team = body.Result.FirstOrDefault(i => i.SubjectName == "MyTeam");
            Assert.That(team, Is.Not.Null);
            Assert.That(team!.SubjectType, Is.EqualTo("Group"));
            Assert.That(team.CanRead, Is.True);
            Assert.That(team.CanWrite, Is.True);
            Assert.That(team.CanGrantAccess, Is.False);
            Assert.That(team.CanRevokeAccess, Is.True);
        }

        // -----------------------------------------------------------------------
        // 8. Admin-only: non-admin principal is rejected on each endpoint.
        // -----------------------------------------------------------------------

        [Test]
        public async Task List_NonAdminPrincipal_IsRejected()
        {
            // [GIVEN] A non-admin user.
            var principal = new ClaimsPrincipal(this.nonAdminIdentity);
            var request = TestFactory.CreateHttpRequestData("get");

            // [WHEN] Non-admin calls RunList.
            var response = await this.handler.RunList(request, principal, this.logger) as TestHttpResponseData;

            // [THEN] A non-200 response is returned (admin gate blocks non-admins).
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.Not.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public async Task Detail_NonAdminPrincipal_IsRejected()
        {
            // [GIVEN] A non-admin user.
            var principal = new ClaimsPrincipal(this.nonAdminIdentity);
            var request = TestFactory.CreateHttpRequestData("get");

            // [WHEN] Non-admin calls RunDetail.
            var response = await this.handler.RunDetail(request, "any-secret", principal, this.logger) as TestHttpResponseData;

            // [THEN] A non-200 response is returned.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.Not.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public async Task Access_NonAdminPrincipal_IsRejected()
        {
            // [GIVEN] A non-admin user.
            var principal = new ClaimsPrincipal(this.nonAdminIdentity);
            var request = TestFactory.CreateHttpRequestData("get");

            // [WHEN] Non-admin calls RunAccess.
            var response = await this.handler.RunAccess(request, "any-secret", principal, this.logger) as TestHttpResponseData;

            // [THEN] A non-200 response is returned.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.Not.EqualTo(HttpStatusCode.OK));
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        /// <summary>
        /// Creates an ObjectMetadata with one IsMain content item and LastAccessedAt == CreatedAt
        /// (the "never accessed" baseline). Caller must add it to dbContext.Objects.
        /// </summary>
        private ObjectMetadata CreateSecret(string name, string createdBy)
        {
            var input = new MetadataCreationInput
            {
                ExpirationSettings = new ExpirationSettingsInput
                {
                    ScheduleExpiration = false,
                    ExpireAt = default,
                    ExpireOnIdleTime = false,
                    IdleTimeToExpire = TimeSpan.Zero,
                },
            };

            return new ObjectMetadata(name, input, createdBy);
        }
    }
}
