/// <summary>
/// UserTests
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
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
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
        public void OneTimeSetup()
        {
            var builder = new ConfigurationBuilder().AddUserSecrets<UserTests>();
            var secretConfiguration = builder.Build();

            this.logger = TestFactory.CreateLogger();

            this.dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(secretConfiguration.GetConnectionString("CosmosDb"), $"{nameof(UserTests)}Database")
                .Options;

            this.dbContext = new SafeExchangeDbContext(this.dbContextOptions);
            this.dbContext.Database.EnsureCreated();

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
                this.graphDataProvider, this.middlewareLogger);

            this.secretMeta = new SafeExchangeSecretMeta(
                this.testConfiguration, this.dbContext, this.tokenHelper,
                this.globalFilters, this.purger, this.permissionsManager);
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
                localGlobalFilters, this.purger, this.permissionsManager);

            var localTokenMiddleware = new TokenMiddlewareCore(
                localConfiguration, this.dbContext, this.tokenHelper,
                this.graphDataProvider, this.middlewareLogger);

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