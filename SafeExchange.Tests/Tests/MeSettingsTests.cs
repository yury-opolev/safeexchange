/// <summary>
/// MeSettingsTests — GET and PATCH /me/settings against the Cosmos DB emulator (the etag
/// concurrency of the user document is only real there).
/// </summary>

namespace SafeExchange.Tests
{
    using Azure.Core.Serialization;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
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
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Model;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;

    [TestFixture]
    public class MeSettingsTests
    {
        private const string TenantId = "00000000-0000-0000-0000-000000000001";

        private const string UserObjectId = "00000000-0000-0000-0000-0000000000d1";

        private const string UserUpn = "me@test.test";

        private DbContextOptions<SafeExchangeDbContext> options = null!;

        private SafeExchangeDbContext dbContext = null!;

        private ITokenHelper tokenHelper = null!;

        private GlobalFilters globalFilters = null!;

        private IServiceProvider previousInstanceServices = null!;

        [OneTimeSetUp]
        public async Task OneTimeSetup()
        {
            var connectionString = new ConfigurationBuilder().AddUserSecrets<MeSettingsTests>().Build().GetConnectionString("CosmosDb");
            this.options = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(connectionString!, $"{nameof(MeSettingsTests)}Database", CosmosTestOptions.UseGateway)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CosmosEventId.SyncNotSupported))
                .Options;
            this.dbContext = new SafeExchangeDbContext(this.options);
            await this.dbContext.Database.EnsureCreatedAsync();

            this.tokenHelper = new TokenHelper(new ConfigurationBuilder().Build(), NullLogger<TokenHelper>.Instance);
            this.globalFilters = new GlobalFilters(
                Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(x => x.CurrentValue == new GloballyAllowedGroupsConfiguration()),
                Mock.Of<IOptionsMonitor<AdminConfiguration>>(x => x.CurrentValue == new AdminConfiguration()),
                this.tokenHelper,
                NullLogger<GlobalFilters>.Instance);
        }

        [OneTimeTearDown]
        public void OneTimeCleanup()
        {
            TestDatabase.EnsureDeletedTolerant(this.dbContext.Database);
            this.dbContext.Dispose();
        }

        [SetUp]
        public void Setup()
        {
            // Production response serializer settings (camelCase, nulls omitted).
            this.previousInstanceServices = TestFactory.FunctionContext.InstanceServices;
            var serializer = new JsonObjectSerializer(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNameCaseInsensitive = true,
            });
            var workerOptions = Options.Create(new WorkerOptions { Serializer = serializer });
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
        public async Task Get_ReturnsTheDefaults_OfANewUser()
        {
            await this.SeedUserAsync();

            var response = await this.Handler().RunGet(Request("get"), UserPrincipal(), NullLogger.Instance);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(BodyOf(response), Is.EqualTo("""{"status":"ok","subStatus":"","result":{"mcpAllowed":false,"mcpBlockedByAdmin":false,"mcpEffective":false}}"""));
        }

        [Test]
        public async Task Get_OfAUserWithoutAUsersRow_IsNotFound()
        {
            var response = await this.Handler().RunGet(Request("get"), UserPrincipal(), NullLogger.Instance);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }

        [Test]
        public async Task Patch_OptsInAndOut()
        {
            var user = await this.SeedUserAsync();

            var optIn = await this.Handler().RunUpdate(Request("patch", """{"mcpAllowed":true}"""), UserPrincipal(), NullLogger.Instance);
            var storedAfterOptIn = await this.StoredAsync(user.Id);
            var optOut = await this.Handler().RunUpdate(Request("patch", """{"mcpAllowed":false}"""), UserPrincipal(), NullLogger.Instance);
            var storedAfterOptOut = await this.StoredAsync(user.Id);

            Assert.That(optIn.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(BodyOf(optIn), Is.EqualTo("""{"status":"ok","subStatus":"","result":{"mcpAllowed":true,"mcpBlockedByAdmin":false,"mcpEffective":true}}"""));
            Assert.That(storedAfterOptIn.McpAllowed, Is.True);
            Assert.That(optOut.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(BodyOf(optOut), Is.EqualTo("""{"status":"ok","subStatus":"","result":{"mcpAllowed":false,"mcpBlockedByAdmin":false,"mcpEffective":false}}"""));
            Assert.That(storedAfterOptOut.McpAllowed, Is.False);
        }

        [Test]
        public async Task Patch_OptInWhileBlocked_IsForbidden_AndStoresNothing()
        {
            var user = await this.SeedUserAsync(mcpBlockedByAdmin: true);

            var response = await this.Handler().RunUpdate(Request("patch", """{"mcpAllowed":true}"""), UserPrincipal(), NullLogger.Instance);

            AssertBlocked(response);
            Assert.That((await this.StoredAsync(user.Id)).McpAllowed, Is.False);
        }

        [Test]
        public async Task Patch_OptOutWhileBlocked_IsAccepted()
        {
            var user = await this.SeedUserAsync(mcpAllowed: true, mcpBlockedByAdmin: true);

            var response = await this.Handler().RunUpdate(Request("patch", """{"mcpAllowed":false}"""), UserPrincipal(), NullLogger.Instance);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(BodyOf(response), Is.EqualTo("""{"status":"ok","subStatus":"","result":{"mcpAllowed":false,"mcpBlockedByAdmin":true,"mcpEffective":false}}"""));
            Assert.That((await this.StoredAsync(user.Id)).McpAllowed, Is.False);
        }

        [TestCase("")]
        [TestCase("not json")]
        [TestCase("[]")]
        [TestCase("{}")]
        [TestCase("""{"mcpAllowed":null}""")]
        [TestCase("""{"mcpAllowed":"true"}""")]
        [TestCase("""{"mcpAllowed":1}""")]
        public async Task Patch_WithoutABooleanMcpAllowed_IsBadRequest(string body)
        {
            var user = await this.SeedUserAsync();

            var response = await this.Handler().RunUpdate(Request("patch", body), UserPrincipal(), NullLogger.Instance);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            var json = JsonNode.Parse(BodyOf(response))!;
            Assert.That(json["status"]!.GetValue<string>(), Is.EqualTo("error"));
            Assert.That(json["error"]!.GetValue<string>(), Is.EqualTo(SafeExchangeMeSettings.InvalidBodyError));
            Assert.That((await this.StoredAsync(user.Id)).McpAllowed, Is.False);
        }

        [TestCase("get")]
        [TestCase("patch")]
        public async Task Application_IsForbidden(string method)
        {
            var handler = this.Handler();
            var request = Request(method, """{"mcpAllowed":true}""");

            var response = method == "get"
                ? await handler.RunGet(request, AppPrincipal(), NullLogger.Instance)
                : await handler.RunUpdate(request, AppPrincipal(), NullLogger.Instance);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            var json = JsonNode.Parse(BodyOf(response))!;
            Assert.That(json["status"]!.GetValue<string>(), Is.EqualTo("forbidden"));
            Assert.That(json["error"]!.GetValue<string>(), Is.EqualTo(SafeExchangeMeSettings.UsersOnlyError));
        }

        [Test]
        public async Task Patch_ConcurrentUpdate_IsRetried_AndKeepsBothChanges()
        {
            var user = await this.SeedUserAsync();
            var concurrentWriter = new ConcurrentUserWriteInterceptor(this.options, user.Id, u => u.DisplayName = "Renamed concurrently");

            var response = await this.Handler(concurrentWriter).RunUpdate(Request("patch", """{"mcpAllowed":true}"""), UserPrincipal(), NullLogger.Instance);

            var stored = await this.StoredAsync(user.Id);
            Assert.That(concurrentWriter.Writes, Is.EqualTo(1));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(stored.McpAllowed, Is.True);
            Assert.That(stored.DisplayName, Is.EqualTo("Renamed concurrently"));
        }

        [Test]
        public async Task Patch_OptIn_LosesToAConcurrentBlock()
        {
            var user = await this.SeedUserAsync();
            var concurrentWriter = new ConcurrentUserWriteInterceptor(this.options, user.Id, u => u.McpBlockedByAdmin = true);

            var response = await this.Handler(concurrentWriter).RunUpdate(Request("patch", """{"mcpAllowed":true}"""), UserPrincipal(), NullLogger.Instance);

            var stored = await this.StoredAsync(user.Id);
            AssertBlocked(response);
            Assert.That(stored.McpAllowed, Is.False);
            Assert.That(stored.McpBlockedByAdmin, Is.True);
        }

        private static void AssertBlocked(HttpResponseData response)
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            var json = JsonNode.Parse(BodyOf(response))!;
            Assert.That(json["status"]!.GetValue<string>(), Is.EqualTo("forbidden"));
            Assert.That(json["subStatus"]!.GetValue<string>(), Is.EqualTo(McpUserAccessPolicy.BlockedByAdminSubStatus));
            Assert.That(json["error"]!.GetValue<string>(), Is.EqualTo(SafeExchangeMeSettings.EnableWhileBlockedError));
        }

        private SafeExchangeMeSettings Handler(ConcurrentUserWriteInterceptor? concurrentWriter = null)
        {
            var handlerOptions = concurrentWriter is null
                ? this.options
                : new DbContextOptionsBuilder<SafeExchangeDbContext>(this.options).AddInterceptors(concurrentWriter).Options;
            return new SafeExchangeMeSettings(new SafeExchangeDbContext(handlerOptions), this.tokenHelper, this.globalFilters);
        }

        private async Task<User> SeedUserAsync(bool mcpAllowed = false, bool mcpBlockedByAdmin = false)
        {
            await using var seed = new SafeExchangeDbContext(this.options);
            var user = new User("Me", UserObjectId, TenantId, UserUpn, UserUpn)
            {
                McpAllowed = mcpAllowed,
                McpBlockedByAdmin = mcpBlockedByAdmin,
            };
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
            return user;
        }

        private async Task<User> StoredAsync(string id)
        {
            await using var read = new SafeExchangeDbContext(this.options);
            return await read.Users.AsNoTracking().SingleAsync(u => u.Id == id);
        }

        private static ClaimsPrincipal UserPrincipal()
            => new(new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new("upn", UserUpn),
                new("oid", UserObjectId),
                new("tid", TenantId),
            }));

        // App-only (client-credentials) v2 token shape: azp + azpacr and no scope claim.
        private static ClaimsPrincipal AppPrincipal()
            => new(new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new("azp", "00000000-0000-0000-0000-0000000000c1"),
                new("azpacr", "1"),
                new("oid", "00000000-0000-0000-0000-0000000000b1"),
                new("tid", TenantId),
            }));

        private static HttpRequestData Request(string method, string body = "")
        {
            var request = TestFactory.CreateHttpRequestData(method);
            request.SetBodyAsStream(new MemoryStream(Encoding.UTF8.GetBytes(body)));
            return request;
        }

        private static string BodyOf(HttpResponseData response) => ((TestHttpResponseData)response).ReadBodyAsString();
    }
}
