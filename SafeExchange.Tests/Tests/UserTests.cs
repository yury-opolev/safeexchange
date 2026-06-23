/// <summary>
/// UserTests
/// </summary>

namespace SafeExchange.Tests
{
    using Azure.Core.Serialization;
    using SafeExchange.Tests.Utilities;
    using Microsoft.Azure.Cosmos;
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
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Telemetry;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Claims;
    using System.Threading.Tasks;

    [TestFixture]
    public class UserTests
    {
        private DbContextOptions<SafeExchangeDbContext> dbContextOptions;

        private ILogger<TokenMiddlewareCore> middlewareLogger;
        private ILogger logger;

        private TokenMiddlewareCore tokenMiddleware;

        private SafeExchangeSecretMeta secretMeta;

        private IConfiguration testConfiguration;

        private SafeExchangeDbContext dbContext;

        private ITokenHelper tokenHelper;

        private TestGraphDataProvider graphDataProvider;

        private GlobalFilters globalFilters;

        private IBlobHelper blobHelper;

        private IPurger purger;

        private IPermissionsManager permissionsManager;

        private CaseSensitiveClaimsIdentity firstIdentity;
        private CaseSensitiveClaimsIdentity secondIdentity;

        [OneTimeSetUp]
        public async Task OneTimeSetup()
        {
            var builder = new ConfigurationBuilder().AddUserSecrets<UserTests>();
            var secretConfiguration = builder.Build();

            var databaseName = $"{nameof(UserTests)}Database";
            var cosmosClient = CosmosTestOptions.CreateClient(secretConfiguration.GetConnectionString("CosmosDb"));
            cosmosClient.CreateDatabaseIfNotExistsAsync(databaseName).GetAwaiter().GetResult();
            cosmosClient.GetDatabase(databaseName).DefineContainer(name: "Users", partitionKeyPath: "/PartitionKey")
                .WithUniqueKey()
                    .Path("/AadTenantId")
                    .Path("/AadObjectId")
                .Attach()
                .WithUniqueKey()
                    .Path("/AadUpn")
                .Attach()
                .CreateIfNotExistsAsync().GetAwaiter().GetResult();

            await cosmosClient.GetDatabase(databaseName).CreateContainerIfNotExistsAsync(
                new Microsoft.Azure.Cosmos.ContainerProperties("TelemetryIdMap", "/UserId")
                {
                    DefaultTimeToLive = 2419200,
                });

            this.logger = TestFactory.CreateLogger();

            this.dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(secretConfiguration.GetConnectionString("CosmosDb"), databaseName, CosmosTestOptions.UseGateway)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CosmosEventId.SyncNotSupported))
                .Options;

            this.dbContext = new SafeExchangeDbContext(this.dbContextOptions);
            await this.dbContext.Database.EnsureCreatedAsync();

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
            this.CreateDefaultTestConfiguration();
            this.CreateTestClasses();

            DateTimeProvider.SpecifiedDateTime = DateTime.UtcNow;

            this.middlewareLogger = TestFactory.CreateLogger<TokenMiddlewareCore>();
            this.tokenMiddleware = new TokenMiddlewareCore(
                this.testConfiguration, this.dbContext, this.tokenHelper,
                this.graphDataProvider, new TelemetryIdRotator(), this.middlewareLogger);

            this.secretMeta = new SafeExchangeSecretMeta(
                this.testConfiguration, this.dbContext, this.tokenHelper,
                this.globalFilters, this.purger, this.permissionsManager, new NoOpAuditWriter());
        }

        [TearDown]
        public void Cleanup()
        {
            this.graphDataProvider.GroupMemberships.Clear();

            this.dbContext.Users.RemoveRange(this.dbContext.Users.ToList());
            this.dbContext.Objects.RemoveRange(this.dbContext.Objects.ToList());
            this.dbContext.Permissions.RemoveRange(this.dbContext.Permissions.ToList());
            this.dbContext.AccessRequests.RemoveRange(this.dbContext.AccessRequests.ToList());
            this.dbContext.GroupDictionary.RemoveRange(this.dbContext.GroupDictionary.ToList());
            this.dbContext.Set<TelemetryIdMapEntry>().RemoveRange(this.dbContext.Set<TelemetryIdMapEntry>().ToList());
            this.dbContext.SaveChanges();
        }

        [Test]
        public async Task UserIsCreatedWithGroups()
        {
            // [GIVEN] A user with valid credentials, is member of several groups in AAD
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            this.graphDataProvider.GroupMemberships
                ["00000000-0000-0000-0000-000000000001.00000000-0000-0000-0000-000000000001"] =
                new List<string> { "00000000-0000-0000-9999-000000000001", "00000000-0000-0000-9999-000000009999" };

            var request = TestFactory.CreateHttpRequestData("get");

            // [WHEN] An arbitrary call to a service
            await this.tokenMiddleware.RunAsync(request, claimsPrincipal);
            await this.secretMeta.Run(request, "dummy", claimsPrincipal, this.logger);

            // [THEN] User is created in the database with UPN, DisplayName, TenantId and ObjectId
            var createdUser = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals("first@test.test"));
            Assert.That(createdUser, Is.Not.Null);
            Assert.That(createdUser?.DisplayName, Is.EqualTo("First User"));
            Assert.That(createdUser?.AadTenantId, Is.EqualTo("00000000-0000-0000-0000-000000000001"));
            Assert.That(createdUser?.AadObjectId, Is.EqualTo("00000000-0000-0000-0000-000000000001"));

            Assert.That(createdUser?.CreatedAt, Is.EqualTo(DateTimeProvider.SpecifiedDateTime));
            Assert.That(createdUser?.ModifiedAt, Is.EqualTo(DateTime.MinValue));
            Assert.That(createdUser?.GroupSyncNotBefore, Is.EqualTo(DateTimeProvider.SpecifiedDateTime + TokenMiddlewareCore.GroupSyncDelay));

            // [THEN] User has his 'memberOf' groups persisted
            var userGroups = createdUser?.Groups;
            Assert.That(userGroups, Is.Not.Null);
            Assert.That(userGroups?.Count, Is.EqualTo(2));
            Assert.That(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-000000000001")), Is.True);
            Assert.That(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-000000009999")), Is.True);

            // [THEN] Function context contains UserId in its items.
            Assert.That(request.FunctionContext.GetUserId(), Is.EqualTo(createdUser!.Id));
        }

        [Test]
        public async Task TelemetryId_IsStampedOnInvocationAndMatchesUser()
        {
            // [GIVEN] A user with valid credentials
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var request = TestFactory.CreateHttpRequestData("get");

            // [WHEN] An arbitrary call to a service resolves the user
            await this.tokenMiddleware.RunAsync(request, claimsPrincipal);

            // [THEN] The persisted user carries a non-empty telemetry id
            var createdUser = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals("first@test.test"));
            Assert.That(createdUser, Is.Not.Null);
            Assert.That(createdUser!.TelemetryId, Is.Not.Empty);

            // [THEN] The same telemetry id is stamped onto the invocation so the
            // middleware can re-establish it on TelemetryContext around next().
            Assert.That(request.FunctionContext.GetTelemetryId(), Is.EqualTo(createdUser.TelemetryId));
        }

        [Test]
        public async Task SimultaneousUserCalls_WithGroups()
        {
            var builder = new ConfigurationBuilder().AddUserSecrets<UserTests>();
            var secretConfiguration = builder.Build();

            var dbContextOptionsLocal = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(secretConfiguration.GetConnectionString("CosmosDb"), $"{nameof(UserTests)}Database", CosmosTestOptions.UseGateway)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CosmosEventId.SyncNotSupported))
                .Options;

            // [GIVEN] A user with valid credentials, is member of several groups in AAD
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            var graphDataProvider1 = new TestGraphDataProvider(TimeSpan.FromMilliseconds(100));
            graphDataProvider1.GroupMemberships
                ["00000000-0000-0000-0000-000000000001.00000000-0000-0000-0000-000000000001"] =
                new List<string> { "00000000-0000-0000-9999-000000000001", "00000000-0000-0000-9999-000000009999" };
            var tokenMiddleware1 = new TokenMiddlewareCore(
                this.testConfiguration, new SafeExchangeDbContext(dbContextOptionsLocal), this.tokenHelper,
                graphDataProvider1, new TelemetryIdRotator(), TestFactory.CreateLogger<TokenMiddlewareCore>(LoggerTypes.Console));

            var graphDataProvider2 = new TestGraphDataProvider(TimeSpan.FromMilliseconds(100));
            graphDataProvider2.GroupMemberships
                ["00000000-0000-0000-0000-000000000001.00000000-0000-0000-0000-000000000001"] =
                new List<string> { "00000000-0000-0000-9999-000000000001", "00000000-0000-0000-9999-000000009999" };
            var tokenMiddleware2 = new TokenMiddlewareCore(
                this.testConfiguration, new SafeExchangeDbContext(dbContextOptionsLocal), this.tokenHelper,
                graphDataProvider2, new TelemetryIdRotator(), TestFactory.CreateLogger<TokenMiddlewareCore>(LoggerTypes.Console));

            var graphDataProvider3 = new TestGraphDataProvider(TimeSpan.FromMilliseconds(100));
            graphDataProvider3.GroupMemberships
                ["00000000-0000-0000-0000-000000000001.00000000-0000-0000-0000-000000000001"] =
                new List<string> { "00000000-0000-0000-9999-000000000001", "00000000-0000-0000-9999-000000009999" };
            var tokenMiddleware3 = new TokenMiddlewareCore(
                this.testConfiguration, new SafeExchangeDbContext(dbContextOptionsLocal), this.tokenHelper,
                graphDataProvider3, new TelemetryIdRotator(), TestFactory.CreateLogger<TokenMiddlewareCore>(LoggerTypes.Console));

            var request = TestFactory.CreateHttpRequestData("get");

            // [WHEN] The user makes 3 simultaneous calls to a service
            var logger = TestFactory.CreateLogger(LoggerTypes.Console);
            await Task.WhenAll([
                Task.Run(async () =>
            {
                logger.LogInformation($"Call no. 1 started");
                await tokenMiddleware1.RunAsync(request, claimsPrincipal);
                logger.LogInformation($"Call no. 1 finished");
            }),
            Task.Run(async () =>
            {
                logger.LogInformation($"Call no. 2 started");
                await tokenMiddleware2.RunAsync(request, claimsPrincipal);
                logger.LogInformation($"Call no. 2 finished");
            }),
            Task.Run(async () =>
            {
                logger.LogInformation($"Call no. 3 started");
                await tokenMiddleware3.RunAsync(request, claimsPrincipal);
                logger.LogInformation($"Call no. 3 finished");
            })]);

            // [THEN] User is created in the database with UPN, DisplayName, TenantId and ObjectId
            var assertionDbContext = new SafeExchangeDbContext(dbContextOptionsLocal);
            var createdUsers = await assertionDbContext.Users.Where(u => u.AadUpn.Equals("first@test.test")).ToListAsync();

            Assert.That(createdUsers.Count, Is.EqualTo(1));

            var createdUser = createdUsers.Single();
            Assert.That(createdUser, Is.Not.Null);
            Assert.That(createdUser?.DisplayName, Is.EqualTo("First User"));
            Assert.That(createdUser?.AadTenantId, Is.EqualTo("00000000-0000-0000-0000-000000000001"));
            Assert.That(createdUser?.AadObjectId, Is.EqualTo("00000000-0000-0000-0000-000000000001"));

            Assert.That(createdUser?.CreatedAt, Is.EqualTo(DateTimeProvider.SpecifiedDateTime));
            Assert.That(createdUser?.ModifiedAt, Is.EqualTo(DateTime.MinValue));
            Assert.That(createdUser?.GroupSyncNotBefore, Is.EqualTo(DateTimeProvider.SpecifiedDateTime + TokenMiddlewareCore.GroupSyncDelay));

            // [THEN] User has his 'memberOf' groups persisted
            var userGroups = createdUser?.Groups;
            Assert.That(userGroups, Is.Not.Null);
            Assert.That(userGroups?.Count, Is.EqualTo(2));
            Assert.That(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-000000000001")), Is.True);
            Assert.That(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-000000009999")), Is.True);
        }

        [Test]
        public async Task DoNotUseGroups_UserIsCreatedWithoutGroups()
        {
            // [GIVEN] Service is configured without groups
            var configurationValues = new Dictionary<string, string>
                {
                    {"Features:UseNotifications", "False"},
                    {"Features:UseGroupsAuthorization", "False"}
                };

            var localConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(configurationValues)
                .Build();

            GloballyAllowedGroupsConfiguration gagc = new GloballyAllowedGroupsConfiguration();
            var groupsConfiguration = Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(x => x.CurrentValue == gagc);

            AdminConfiguration ac = new AdminConfiguration();
            var adminConfiguration = Mock.Of<IOptionsMonitor<AdminConfiguration>>(x => x.CurrentValue == ac);

            var localGlobalFilters = new GlobalFilters(groupsConfiguration, adminConfiguration, this.tokenHelper, TestFactory.CreateLogger<GlobalFilters>());
            var localSecretMeta = new SafeExchangeSecretMeta(
                this.testConfiguration, this.dbContext, this.tokenHelper,
                localGlobalFilters, this.purger, this.permissionsManager, new NoOpAuditWriter());

            var localTokenMiddleware = new TokenMiddlewareCore(
                localConfiguration, this.dbContext, this.tokenHelper,
                this.graphDataProvider, new TelemetryIdRotator(), this.middlewareLogger);

            // [GIVEN] A user with valid credentials, is member of several groups in AAD
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            this.graphDataProvider.GroupMemberships
                ["00000000-0000-0000-0000-000000000001.00000000-0000-0000-0000-000000000001"] =
                new List<string> { "00000000-0000-0000-9999-000000000001", "00000000-0000-0000-9999-000000009999" };

            var request = TestFactory.CreateHttpRequestData("get");

            // [WHEN] An arbitrary call to a service
            await localTokenMiddleware.RunAsync(request, claimsPrincipal);
            await localSecretMeta.Run(request, "dummy", claimsPrincipal, this.logger);

            // [THEN] User is created in the database with UPN, DisplayName, TenantId and ObjectId
            var createdUser = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals("first@test.test"));
            Assert.That(createdUser, Is.Not.Null);
            Assert.That(createdUser?.DisplayName, Is.EqualTo("First User"));
            Assert.That(createdUser?.AadTenantId, Is.EqualTo("00000000-0000-0000-0000-000000000001"));
            Assert.That(createdUser?.AadObjectId, Is.EqualTo("00000000-0000-0000-0000-000000000001"));

            Assert.That(createdUser?.CreatedAt, Is.EqualTo(DateTimeProvider.SpecifiedDateTime));
            Assert.That(createdUser?.GroupSyncNotBefore, Is.EqualTo(DateTime.MinValue));

            // [THEN] User has 0 groups, LastGroupSync value is not set.
            var userGroups = createdUser?.Groups;
            Assert.That(userGroups, Is.Not.Null);
            Assert.That(userGroups?.Count, Is.EqualTo(0));

            // [THEN] Function context contains UserId in its items.
            Assert.That(request.FunctionContext.GetUserId(), Is.EqualTo(createdUser!.Id));
        }

        [Test]
        public async Task UserGroupsAreNotRefreshedAfterSmallDelay()
        {
            // [GIVEN] A user with valid credentials, is member of several groups in AAD
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            this.graphDataProvider.GroupMemberships
                ["00000000-0000-0000-0000-000000000001.00000000-0000-0000-0000-000000000001"] =
                new List<string> { "00000000-0000-0000-9999-000000000001", "00000000-0000-0000-9999-000000009999" };

            var request = TestFactory.CreateHttpRequestData("get");

            // [GIVEN] Was making calls to a service before
            await this.tokenMiddleware.RunAsync(request, claimsPrincipal);
            var response = await this.secretMeta.Run(request, "dummy", claimsPrincipal, this.logger);

            // [GIVEN] User is created in the database with groups
            var user = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals("first@test.test"));
            Assert.That(user, Is.Not.Null);
            Assert.That(user?.GroupSyncNotBefore, Is.EqualTo(DateTimeProvider.SpecifiedDateTime + TokenMiddlewareCore.GroupSyncDelay));

            var groupSyncNotBefore = user?.GroupSyncNotBefore;

            var userGroups = user?.Groups;
            Assert.That(userGroups, Is.Not.Null);
            Assert.That(userGroups?.Count, Is.EqualTo(2));
            Assert.That(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-000000000001")), Is.True);
            Assert.That(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-000000009999")), Is.True);

            // [GIVEN] User groups were changed in AAD
            this.graphDataProvider.GroupMemberships
                ["00000000-0000-0000-0000-000000000001.00000000-0000-0000-0000-000000000001"] =
                new List<string> { "00000000-0000-0000-9999-ffff00000001", "00000000-0000-0000-9999-ffff00009999" };

            // [WHEN] Another call to a service is made within 'group refresh delay' time
            DateTimeProvider.SpecifiedDateTime += TokenMiddlewareCore.GroupSyncDelay - TimeSpan.FromSeconds(10);

            await this.tokenMiddleware.RunAsync(request, claimsPrincipal);
            await this.secretMeta.Run(request, "dummy", claimsPrincipal, this.logger);

            // [THEN] Groups are not refreshed
            user = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals("first@test.test"));
            Assert.That(user, Is.Not.Null);
            Assert.That(user?.GroupSyncNotBefore, Is.EqualTo(groupSyncNotBefore));

            userGroups = user?.Groups;
            Assert.That(userGroups, Is.Not.Null);
            Assert.That(userGroups?.Count, Is.EqualTo(2));
            Assert.That(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-000000000001")), Is.True);
            Assert.That(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-000000009999")), Is.True);
        }

        [Test]
        public async Task UserGroupsAreRefreshedAfterGreaterDelay()
        {
            // [GIVEN] A user with valid credentials, is member of several groups in AAD
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            this.graphDataProvider.GroupMemberships
                ["00000000-0000-0000-0000-000000000001.00000000-0000-0000-0000-000000000001"] =
                new List<string> { "00000000-0000-0000-9999-000000000001", "00000000-0000-0000-9999-000000009999" };

            var request = TestFactory.CreateHttpRequestData("get");

            // [GIVEN] Was making calls to a service before
            await this.tokenMiddleware.RunAsync(request, claimsPrincipal);
            var response = await this.secretMeta.Run(request, "dummy", claimsPrincipal, this.logger);

            // [GIVEN] User is created in the database with groups
            var user = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals("first@test.test"));
            Assert.That(user, Is.Not.Null);
            Assert.That(user?.GroupSyncNotBefore, Is.EqualTo(DateTimeProvider.SpecifiedDateTime + TokenMiddlewareCore.GroupSyncDelay));

            var groupSyncNotBefore = user?.GroupSyncNotBefore;

            var userGroups = user?.Groups;
            Assert.That(userGroups, Is.Not.Null);
            Assert.That(userGroups?.Count, Is.EqualTo(2));
            Assert.That(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-000000000001")), Is.True);
            Assert.That(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-000000009999")), Is.True);

            // [GIVEN] User groups were changed in AAD
            this.graphDataProvider.GroupMemberships
                ["00000000-0000-0000-0000-000000000001.00000000-0000-0000-0000-000000000001"] =
                new List<string> { "00000000-0000-0000-9999-ffff00000001", "00000000-0000-0000-9999-ffff00009999", "00000000-0000-0000-9999-eeee00009999" };

            // [WHEN] Another call to a service is made within 'group refresh delay' time
            DateTimeProvider.SpecifiedDateTime += TokenMiddlewareCore.GroupSyncDelay + TimeSpan.FromSeconds(10);
            await this.tokenMiddleware.RunAsync(request, claimsPrincipal);
            await this.secretMeta.Run(request, "dummy", claimsPrincipal, this.logger);

            // [THEN] Groups are refreshed
            user = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals("first@test.test"));
            Assert.That(user, Is.Not.Null);
            Assert.That(user?.GroupSyncNotBefore, Is.EqualTo(DateTimeProvider.SpecifiedDateTime + TokenMiddlewareCore.GroupSyncDelay));

            userGroups = user?.Groups;
            Assert.That(userGroups, Is.Not.Null);
            Assert.That(userGroups?.Count, Is.EqualTo(3));
            Assert.That(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-ffff00000001")), Is.True);
            Assert.That(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-ffff00009999")), Is.True);
            Assert.That(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-eeee00009999")), Is.True);
        }

        [Test]
        public async Task Rotation_WritesTelemetryIdMapEntry_ForRetiredId()
        {
            // [GIVEN] A user that has already been created (id #1 active)
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var request = TestFactory.CreateHttpRequestData("get");
            await this.tokenMiddleware.RunAsync(request, claimsPrincipal);

            var user = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals("first@test.test"));
            Assert.That(user, Is.Not.Null);
            var firstId = user!.TelemetryId;
            Assert.That(firstId, Is.Not.Empty);

            // [WHEN] Time crosses the week boundary and the user calls again -> rotation
            DateTimeProvider.SpecifiedDateTime = user.TelemetryIdExpiresAt.AddSeconds(1);
            await this.tokenMiddleware.RunAsync(request, claimsPrincipal);

            // [THEN] The retired (first) id is recorded in the map for this user
            var entry = await this.dbContext.Set<TelemetryIdMapEntry>()
                .FirstOrDefaultAsync(e => e.id == firstId);
            Assert.That(entry, Is.Not.Null);
            Assert.That(entry!.UserId, Is.EqualTo(user.Id));
            Assert.That(entry.ValidToUtc, Is.EqualTo(DateTimeProvider.SpecifiedDateTime));
        }

        [Test]
        public async Task Rotation_DuplicateRetiredId_IsSwallowed_NoThrow()
        {
            // [GIVEN] A created user with an active telemetry id
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var request = TestFactory.CreateHttpRequestData("get");
            await this.tokenMiddleware.RunAsync(request, claimsPrincipal);

            var user = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals("first@test.test"));
            Assert.That(user, Is.Not.Null);
            var retiredId = user!.TelemetryId;
            Assert.That(retiredId, Is.Not.Empty);

            // [GIVEN] A concurrent process already recorded that exact id in the map. A separate
            // DbContext is used so this is a real Cosmos document (not an EF-tracked duplicate),
            // guaranteeing the middleware's later insert hits a server-side 409.
            using (var seedContext = new SafeExchangeDbContext(this.dbContextOptions))
            {
                await seedContext.Set<TelemetryIdMapEntry>().AddAsync(new TelemetryIdMapEntry
                {
                    id = retiredId,
                    UserId = user.Id,
                    ValidFromUtc = user.TelemetryIdIssuedAt,
                    ValidToUtc = user.TelemetryIdExpiresAt,
                });
                await seedContext.SaveChangesAsync();
            }

            // [WHEN] The boundary is crossed and the same user calls again -> rotation retires the
            // same id and races to insert the map document that already exists.
            DateTimeProvider.SpecifiedDateTime = user.TelemetryIdExpiresAt.AddSeconds(1);

            // [THEN] The benign 409 is swallowed: the request does not throw (no 500).
            Assert.DoesNotThrowAsync(async () => await this.tokenMiddleware.RunAsync(request, claimsPrincipal));

            // [THEN] Exactly one map document exists for the retired id and the user rotated away from it.
            using (var verifyContext = new SafeExchangeDbContext(this.dbContextOptions))
            {
                var entries = await verifyContext.Set<TelemetryIdMapEntry>()
                    .Where(e => e.id == retiredId)
                    .ToListAsync();
                Assert.That(entries.Count, Is.EqualTo(1));

                var rotatedUser = await verifyContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals("first@test.test"));
                Assert.That(rotatedUser!.TelemetryId, Is.Not.EqualTo(retiredId));
            }

            // This test mutates the user through separate DbContexts, so the shared fixture
            // context now tracks a stale etag. Clear it so TearDown re-reads current etags
            // instead of failing its delete with a 412 precondition mismatch.
            this.dbContext.ChangeTracker.Clear();
        }

        [Test]
        public async Task Rotation_ConcurrentBoundaryCrossing_DoesNotThrow_AndDeduplicates()
        {
            // [GIVEN] A created user with an active telemetry id
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var request = TestFactory.CreateHttpRequestData("get");
            await this.tokenMiddleware.RunAsync(request, claimsPrincipal);

            var user = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals("first@test.test"));
            Assert.That(user, Is.Not.Null);
            var retiredId = user!.TelemetryId;

            // [GIVEN] Three middleware instances, each with its own DbContext (separate request scopes),
            // all observing the user just past the rotation boundary.
            DateTimeProvider.SpecifiedDateTime = user.TelemetryIdExpiresAt.AddSeconds(1);

            var middleware1 = new TokenMiddlewareCore(
                this.testConfiguration, new SafeExchangeDbContext(this.dbContextOptions), this.tokenHelper,
                this.graphDataProvider, new TelemetryIdRotator(), this.middlewareLogger);
            var middleware2 = new TokenMiddlewareCore(
                this.testConfiguration, new SafeExchangeDbContext(this.dbContextOptions), this.tokenHelper,
                this.graphDataProvider, new TelemetryIdRotator(), this.middlewareLogger);
            var middleware3 = new TokenMiddlewareCore(
                this.testConfiguration, new SafeExchangeDbContext(this.dbContextOptions), this.tokenHelper,
                this.graphDataProvider, new TelemetryIdRotator(), this.middlewareLogger);

            // [WHEN] All three requests rotate simultaneously and race to record the same retired id.
            // [THEN] None of them throw (the duplicate inserts are swallowed instead of becoming 500s).
            Assert.DoesNotThrowAsync(async () => await Task.WhenAll(
                Task.Run(async () => await middleware1.RunAsync(TestFactory.CreateHttpRequestData("get"), claimsPrincipal)),
                Task.Run(async () => await middleware2.RunAsync(TestFactory.CreateHttpRequestData("get"), claimsPrincipal)),
                Task.Run(async () => await middleware3.RunAsync(TestFactory.CreateHttpRequestData("get"), claimsPrincipal))));

            // [THEN] At most one map document was written for the retired id (no duplicates persisted).
            using (var verifyContext = new SafeExchangeDbContext(this.dbContextOptions))
            {
                var entries = await verifyContext.Set<TelemetryIdMapEntry>()
                    .Where(e => e.id == retiredId)
                    .ToListAsync();
                Assert.That(entries.Count, Is.LessThanOrEqualTo(1));
            }

            // The parallel rotations ran on separate DbContexts, leaving the shared fixture
            // context with a stale user etag. Clear it so TearDown re-reads current etags
            // instead of failing its delete with a 412 precondition mismatch.
            this.dbContext.ChangeTracker.Clear();
        }

        private void CreateDefaultTestConfiguration()
        {
            var configurationValues = new Dictionary<string, string>
                {
                    {"Features:UseNotifications", "False"},
                    {"Features:UseGroupsAuthorization", "True"}
                };

            this.testConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(configurationValues)
                .Build();
        }

        private void CreateTestClasses()
        {
            this.tokenHelper = new TestTokenHelper();
            this.graphDataProvider = new TestGraphDataProvider();

            GloballyAllowedGroupsConfiguration gagc = new GloballyAllowedGroupsConfiguration();
            var groupsConfiguration = Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(x => x.CurrentValue == gagc);

            AdminConfiguration ac = new AdminConfiguration();
            var adminConfiguration = Mock.Of<IOptionsMonitor<AdminConfiguration>>(x => x.CurrentValue == ac);

            this.globalFilters = new GlobalFilters(groupsConfiguration, adminConfiguration, this.tokenHelper, TestFactory.CreateLogger<GlobalFilters>());

            this.blobHelper = new TestBlobHelper();
            this.purger = new PurgeManager(this.testConfiguration, this.blobHelper, TestFactory.CreateLogger<PurgeManager>());

            this.permissionsManager = new PermissionsManager(this.testConfiguration, this.dbContext, TestFactory.CreateLogger<PermissionsManager>());
        }
    }
}