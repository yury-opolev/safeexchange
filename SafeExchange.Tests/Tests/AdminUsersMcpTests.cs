/// <summary>
/// AdminUsersMcpTests — the MCP fields of the admin user list and PATCH admin/users/{upn}/mcp-blocked,
/// against the Cosmos DB emulator (the etag concurrency of the user document is only real there).
/// </summary>

namespace SafeExchange.Tests
{
    using Azure.Core.Serialization;
    using Microsoft.Azure.Cosmos;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging.Abstractions;
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
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Text;
    using System.Text.Json.Nodes;
    using System.Threading.Tasks;
    using User = SafeExchange.Core.Model.User;

    [TestFixture]
    public class AdminUsersMcpTests
    {
        private const string AdminObjectId = "00000321-0000-0000-0000-000000000321";

        private DbContextOptions<SafeExchangeDbContext> options = null!;

        private SafeExchangeDbContext dbContext = null!;

        private CosmosClient cosmosClient = null!;

        private Container users = null!;
        private GlobalFilters globalFilters = null!;

        private IOptionsMonitor<Limits> limits = null!;

        private IServiceProvider previousInstanceServices = null!;

        [OneTimeSetUp]
        public async Task OneTimeSetup()
        {
            var connectionString = new ConfigurationBuilder().AddUserSecrets<AdminUsersMcpTests>().Build().GetConnectionString("CosmosDb");
            this.options = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(connectionString!, $"{nameof(AdminUsersMcpTests)}Database", CosmosTestOptions.UseGateway)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CosmosEventId.SyncNotSupported))
                .Options;
            this.dbContext = new SafeExchangeDbContext(this.options);
            await this.dbContext.Database.EnsureCreatedAsync();

            this.cosmosClient = CosmosTestOptions.CreateClient(connectionString!);
            this.users = this.cosmosClient.GetDatabase($"{nameof(AdminUsersMcpTests)}Database").GetContainer("Users");

            this.globalFilters = new GlobalFilters(
                Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(x => x.CurrentValue == new GloballyAllowedGroupsConfiguration()),
                Mock.Of<IOptionsMonitor<AdminConfiguration>>(x => x.CurrentValue == new AdminConfiguration { AdminUsers = AdminObjectId }),
                new TestTokenHelper(),
                NullLogger<GlobalFilters>.Instance);
            var limitValues = new Limits { AdminListDefaultPageSize = 25, AdminListMaxPageSize = 100 };
            this.limits = Mock.Of<IOptionsMonitor<Limits>>(x => x.CurrentValue == limitValues);
        }

        [OneTimeTearDown]
        public void OneTimeCleanup()
        {
            TestDatabase.EnsureDeletedTolerant(this.dbContext.Database);
            this.dbContext.Dispose();
            this.cosmosClient.Dispose();
        }

        [SetUp]
        public void Setup()
        {
            this.previousInstanceServices = TestFactory.FunctionContext.InstanceServices;
            var workerOptions = Options.Create(new WorkerOptions { Serializer = new JsonObjectSerializer() });
            TestFactory.FunctionContext.InstanceServices = Mock.Of<IServiceProvider>(x => x.GetService(typeof(IOptions<WorkerOptions>)) == workerOptions);
        }

        [TearDown]
        public void Cleanup()
        {
            TestFactory.FunctionContext.InstanceServices = this.previousInstanceServices;
            using var cleanup = new SafeExchangeDbContext(this.options);
            cleanup.Users.RemoveRange(cleanup.Users.ToList());
            cleanup.SaveChanges();
        }

        [Test]
        public async Task RunList_ReturnsTheMcpFlags()
        {
            await this.SeedUserAsync("allowed@test.test", mcpAllowed: true);
            await this.SeedUserAsync("blocked@test.test", mcpAllowed: true, mcpBlockedByAdmin: true);

            var response = (TestHttpResponseData)await this.Handler().RunList(Request("get"), Admin(), NullLogger.Instance);

            var items = response.ReadBodyAsJson<BaseResponseObject<PaginatedResult<UserOverviewOutput>>>()!.Result!.Items;
            Assert.That(items.Select(i => (i.AadUpn, i.McpAllowed, i.McpBlockedByAdmin)), Is.EqualTo(new[]
            {
                ("allowed@test.test", true, false),
                ("blocked@test.test", true, true),
            }));
        }

        [Test]
        public async Task RunList_ReadsLegacyUsersWithoutTheFlags_AsFalse()
        {
            await this.SeedLegacyUserAsync("legacy@test.test");
            await this.SeedUserAsync("current@test.test", mcpAllowed: true);

            var response = (TestHttpResponseData)await this.Handler().RunList(Request("get"), Admin(), NullLogger.Instance);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var items = response.ReadBodyAsJson<BaseResponseObject<PaginatedResult<UserOverviewOutput>>>()!.Result!.Items;
            Assert.That(items.Select(i => (i.AadUpn, i.McpAllowed, i.McpBlockedByAdmin)), Is.EqualTo(new[]
            {
                ("current@test.test", true, false),
                ("legacy@test.test", false, false),
            }));
        }

        [Test]
        public async Task RunDetail_ReadsALegacyUserWithoutTheFlags_AsFalse()
        {
            var user = await this.SeedLegacyUserAsync("legacy@test.test");

            var response = (TestHttpResponseData)await this.Handler().RunDetail(Request("get"), user.AadUpn, Admin(), NullLogger.Instance);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var detail = response.ReadBodyAsJson<BaseResponseObject<UserDetailOutput>>()!.Result!;
            Assert.That((detail.McpAllowed, detail.McpBlockedByAdmin), Is.EqualTo((false, false)));
        }

        [Test]
        public async Task RunByTelemetryId_ReadsALegacyUserWithoutTheFlags_AsFalse()
        {
            var user = await this.SeedLegacyUserAsync("legacy@test.test", telemetryId: "44444444444444444444444444444444");

            var response = (TestHttpResponseData)await this.Handler().RunByTelemetryId(Request("get"), user.TelemetryId, Admin(), NullLogger.Instance);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var detail = response.ReadBodyAsJson<BaseResponseObject<UserDetailOutput>>()!.Result!;
            Assert.That((detail.McpAllowed, detail.McpBlockedByAdmin), Is.EqualTo((false, false)));
        }

        [Test]
        public async Task RunToggleMcpBlocked_OnALegacyUserWithoutTheFlags_Blocks()
        {
            var user = await this.SeedLegacyUserAsync("legacy@test.test");

            var response = (TestHttpResponseData)await this.Handler().RunToggleMcpBlocked(Request("patch", """{"blocked":true}"""), user.AadUpn, Admin(), NullLogger.Instance);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var stored = await this.StoredAsync(user.Id);
            Assert.That((stored.McpAllowed, stored.McpBlockedByAdmin), Is.EqualTo((false, true)));
        }

        [Test]
        public async Task RunToggleMcpBlocked_BlocksAndUnblocks_KeepingTheUsersChoice()
        {
            var user = await this.SeedUserAsync("target@test.test", mcpAllowed: true);

            var blocked = (TestHttpResponseData)await this.Handler().RunToggleMcpBlocked(Request("patch", """{"blocked":true}"""), user.AadUpn, Admin(), NullLogger.Instance);
            var storedBlocked = await this.StoredAsync(user.Id);
            var unblocked = (TestHttpResponseData)await this.Handler().RunToggleMcpBlocked(Request("patch", """{"blocked":false}"""), user.AadUpn, Admin(), NullLogger.Instance);
            var storedUnblocked = await this.StoredAsync(user.Id);

            Assert.That(blocked.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var blockedResult = blocked.ReadBodyAsJson<BaseResponseObject<UserOverviewOutput>>()!.Result!;
            Assert.That((blockedResult.AadUpn, blockedResult.McpAllowed, blockedResult.McpBlockedByAdmin), Is.EqualTo(("target@test.test", true, true)));
            Assert.That((storedBlocked.McpAllowed, storedBlocked.McpBlockedByAdmin), Is.EqualTo((true, true)));
            Assert.That(unblocked.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(unblocked.ReadBodyAsJson<BaseResponseObject<UserOverviewOutput>>()!.Result!.McpBlockedByAdmin, Is.False);
            Assert.That((storedUnblocked.McpAllowed, storedUnblocked.McpBlockedByAdmin), Is.EqualTo((true, false)));
        }

        [Test]
        public async Task RunToggleMcpBlocked_UnknownUser_IsNotFound()
        {
            var response = (TestHttpResponseData)await this.Handler().RunToggleMcpBlocked(Request("patch", """{"blocked":true}"""), "nobody@test.test", Admin(), NullLogger.Instance);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(response.ReadBodyAsJson<BaseResponseObject<object>>()!.Status, Is.EqualTo("not_found"));
        }

        [TestCase("")]
        [TestCase("not json")]
        [TestCase("{}")]
        [TestCase("""{"blocked":null}""")]
        [TestCase("""{"blocked":"true"}""")]
        public async Task RunToggleMcpBlocked_WithoutABooleanBlocked_IsBadRequest(string body)
        {
            var user = await this.SeedUserAsync("target@test.test");

            var response = (TestHttpResponseData)await this.Handler().RunToggleMcpBlocked(Request("patch", body), user.AadUpn, Admin(), NullLogger.Instance);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            var result = response.ReadBodyAsJson<BaseResponseObject<object>>()!;
            Assert.That(result.Status, Is.EqualTo("error"));
            Assert.That(result.Error, Is.EqualTo("Body must be { \"blocked\": bool }."));
            Assert.That((await this.StoredAsync(user.Id)).McpBlockedByAdmin, Is.False);
        }

        [Test]
        public async Task RunToggleMcpBlocked_NonAdmin_IsRejected()
        {
            var user = await this.SeedUserAsync("target@test.test");

            var response = await this.Handler().RunToggleMcpBlocked(Request("patch", """{"blocked":true}"""), user.AadUpn, NonAdmin(), NullLogger.Instance);

            Assert.That(response.StatusCode, Is.Not.EqualTo(HttpStatusCode.OK));
            Assert.That((await this.StoredAsync(user.Id)).McpBlockedByAdmin, Is.False);
        }

        [Test]
        public async Task RunToggleMcpBlocked_ConcurrentUpdate_IsRetried_AndKeepsBothChanges()
        {
            var user = await this.SeedUserAsync("target@test.test", mcpAllowed: true);
            var concurrentWriter = new ConcurrentUserWriteInterceptor(this.options, user.Id, u => u.DisplayName = "Renamed concurrently");

            var response = await this.Handler(concurrentWriter).RunToggleMcpBlocked(Request("patch", """{"blocked":true}"""), user.AadUpn, Admin(), NullLogger.Instance);

            var stored = await this.StoredAsync(user.Id);
            Assert.That(concurrentWriter.Writes, Is.EqualTo(1));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(stored.McpBlockedByAdmin, Is.True);
            Assert.That(stored.DisplayName, Is.EqualTo("Renamed concurrently"));
        }

        [Test]
        public async Task RunToggleEnabled_ConcurrentUpdate_IsRetried_AndKeepsBothChanges()
        {
            var user = await this.SeedUserAsync("target@test.test");
            var concurrentWriter = new ConcurrentUserWriteInterceptor(this.options, user.Id, u => u.DisplayName = "Renamed concurrently");

            var response = await this.Handler(concurrentWriter).RunToggleEnabled(Request("patch", """{"enabled":false}"""), user.AadUpn, Admin(), NullLogger.Instance);

            var stored = await this.StoredAsync(user.Id);
            Assert.That(concurrentWriter.Writes, Is.EqualTo(1));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(stored.Enabled, Is.False);
            Assert.That(stored.DisplayName, Is.EqualTo("Renamed concurrently"));
        }

        private SafeExchangeAdminUsers Handler(ConcurrentUserWriteInterceptor? concurrentWriter = null)
        {
            var handlerOptions = concurrentWriter is null
                ? this.options
                : new DbContextOptionsBuilder<SafeExchangeDbContext>(this.options).AddInterceptors(concurrentWriter).Options;
            return new SafeExchangeAdminUsers(new SafeExchangeDbContext(handlerOptions), new TestTokenHelper(), this.globalFilters, this.limits);
        }

        private async Task<User> SeedUserAsync(string upn, bool mcpAllowed = false, bool mcpBlockedByAdmin = false)
        {
            await using var seed = new SafeExchangeDbContext(this.options);
            var user = new User(upn, Guid.NewGuid().ToString(), "bbbbbbbb-0000-0000-0000-000000000001", upn, upn)
            {
                McpAllowed = mcpAllowed,
                McpBlockedByAdmin = mcpBlockedByAdmin,
            };
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
            return user;
        }

        // A user written before the flags existed: saved through EF, then the two properties removed from the stored document.
        private async Task<User> SeedLegacyUserAsync(string upn, string telemetryId = "")
        {
            var user = await this.SeedUserAsync(upn);
            if (telemetryId.Length > 0)
            {
                await using var update = new SafeExchangeDbContext(this.options);
                var tracked = await update.Users.SingleAsync(u => u.Id == user.Id);
                tracked.TelemetryId = telemetryId;
                await update.SaveChangesAsync();
                user.TelemetryId = telemetryId;
            }

            using var read = await this.users.ReadItemStreamAsync(user.Id, new PartitionKey(User.DefaultPartitionKey));
            read.EnsureSuccessStatusCode();
            using var reader = new StreamReader(read.Content);
            var document = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
            document.Remove("McpAllowed");
            document.Remove("McpBlockedByAdmin");
            using var content = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString()));
            using var replaced = await this.users.ReplaceItemStreamAsync(content, user.Id, new PartitionKey(User.DefaultPartitionKey));
            replaced.EnsureSuccessStatusCode();
            return user;
        }

        private async Task<User> StoredAsync(string id)
        {
            await using var read = new SafeExchangeDbContext(this.options);
            return await read.Users.AsNoTracking().SingleAsync(u => u.Id == id);
        }

        private static ClaimsPrincipal Admin()
            => new(new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new("upn", "admin@test.test"),
                new("oid", AdminObjectId),
                new("tid", "00000000-0000-0000-0000-000000000001"),
            }));

        private static ClaimsPrincipal NonAdmin()
            => new(new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new("upn", "plain@test.test"),
                new("oid", "00000000-0000-0000-0000-000000000099"),
                new("tid", "00000000-0000-0000-0000-000000000001"),
            }));

        private static TestHttpRequestData Request(string method, string body = "")
        {
            var request = TestFactory.CreateHttpRequestData(method);
            request.SetBodyAsStream(new MemoryStream(Encoding.UTF8.GetBytes(body)));
            return request;
        }
    }
}
