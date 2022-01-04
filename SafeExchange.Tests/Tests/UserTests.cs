/// <summary>
/// UserTests
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
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

        private ILogger logger;

        private SafeExchangeSecretMeta secretMeta;

        private IConfiguration testConfiguration;

        private SafeExchangeDbContext dbContext;

        private ITokenHelper tokenHelper;

        private TestGraphDataProvider graphDataProvider;

        private GlobalFilters globalFilters;

        private IBlobHelper blobHelper;

        private IPurger purger;

        private IPermissionsManager permissionsManager;

        private ClaimsIdentity firstIdentity;
        private ClaimsIdentity secondIdentity;

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

            this.firstIdentity = new ClaimsIdentity(new List<Claim>()
                {
                    new Claim("upn", "first@test.test"),
                    new Claim("displayname", "First User"),
                    new Claim("oid", "00000000-0000-0000-0000-000000000001"),
                    new Claim("tid", "00000000-0000-0000-0000-000000000001"),
                }.AsEnumerable());

            this.secondIdentity = new ClaimsIdentity(new List<Claim>()
                {
                    new Claim("upn", "second@test.test"),
                    new Claim("displayname", "Second User"),
                    new Claim("oid", "00000000-0000-0000-0000-000000000002"),
                    new Claim("tid", "00000000-0000-0000-0000-000000000001"),
                }.AsEnumerable());

            DateTimeProvider.SpecifiedDateTime = DateTime.UtcNow;
            DateTimeProvider.UseSpecifiedDateTime = true;
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
            this.dbContext.NotificationSubscriptions.RemoveRange(this.dbContext.NotificationSubscriptions.ToList());
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

            var request = TestFactory.CreateHttpRequest("get");

            // [WHEN] An arbitrary call to a service
            await this.secretMeta.Run(request, "dummy", claimsPrincipal, this.logger);

            // [THEN] User is created in the database with UPN, DisplayName, TenantId and ObjectId
            var createdUser = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals("first@test.test"));
            Assert.IsNotNull(createdUser);
            Assert.AreEqual("First User", createdUser?.DisplayName);
            Assert.AreEqual("00000000-0000-0000-0000-000000000001", createdUser?.AadTenantId);
            Assert.AreEqual("00000000-0000-0000-0000-000000000001", createdUser?.AadObjectId);

            Assert.AreEqual(DateTimeProvider.SpecifiedDateTime, createdUser?.CreatedAt);
            Assert.AreEqual(DateTime.MinValue, createdUser?.ModifiedAt);
            Assert.AreEqual(DateTimeProvider.SpecifiedDateTime, createdUser?.LastGroupSync);

            // [THEN] User has his 'memberOf' groups persisted
            var userGroups = createdUser?.Groups;
            Assert.IsNotNull(userGroups);
            Assert.AreEqual(2, userGroups?.Count);
            Assert.IsTrue(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-000000000001")));
            Assert.IsTrue(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-000000009999")));
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

            var localGlobalFilters = new GlobalFilters(localConfiguration, this.tokenHelper, this.graphDataProvider, TestFactory.CreateLogger<GlobalFilters>());
            var localSecretMeta = new SafeExchangeSecretMeta(
                this.testConfiguration, this.dbContext, this.tokenHelper,
                localGlobalFilters, this.purger, this.permissionsManager);

            // [GIVEN] A user with valid credentials, is member of several groups in AAD
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            this.graphDataProvider.GroupMemberships
                ["00000000-0000-0000-0000-000000000001.00000000-0000-0000-0000-000000000001"] =
                new List<string> { "00000000-0000-0000-9999-000000000001", "00000000-0000-0000-9999-000000009999" };

            var request = TestFactory.CreateHttpRequest("get");

            // [WHEN] An arbitrary call to a service
            await localSecretMeta.Run(request, "dummy", claimsPrincipal, this.logger);

            // [THEN] User is created in the database with UPN, DisplayName, TenantId and ObjectId
            var createdUser = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals("first@test.test"));
            Assert.IsNotNull(createdUser);
            Assert.AreEqual("First User", createdUser?.DisplayName);
            Assert.AreEqual("00000000-0000-0000-0000-000000000001", createdUser?.AadTenantId);
            Assert.AreEqual("00000000-0000-0000-0000-000000000001", createdUser?.AadObjectId);

            Assert.AreEqual(DateTimeProvider.SpecifiedDateTime, createdUser?.CreatedAt);
            Assert.AreEqual(DateTime.MinValue, createdUser?.LastGroupSync);

            // [THEN] User has 0 groups, LastGroupSync value is not set.
            var userGroups = createdUser?.Groups;
            Assert.IsNotNull(userGroups);
            Assert.AreEqual(0, userGroups?.Count);
        }

        [Test]
        public async Task UserGroupsAreNotRefreshedAfterSmallDelay()
        {
            // [GIVEN] A user with valid credentials, is member of several groups in AAD
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            this.graphDataProvider.GroupMemberships
                ["00000000-0000-0000-0000-000000000001.00000000-0000-0000-0000-000000000001"] =
                new List<string> { "00000000-0000-0000-9999-000000000001", "00000000-0000-0000-9999-000000009999" };

            var request = TestFactory.CreateHttpRequest("get");

            // [GIVEN] Was making calls to a service before
            var response = await this.secretMeta.Run(request, "dummy", claimsPrincipal, this.logger);

            // [GIVEN] User is created in the database with groups
            var user = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals("first@test.test"));
            Assert.IsNotNull(user);
            Assert.AreEqual(DateTimeProvider.SpecifiedDateTime, user?.LastGroupSync);

            var lastGroupSync = user?.LastGroupSync;

            var userGroups = user?.Groups;
            Assert.IsNotNull(userGroups);
            Assert.AreEqual(2, userGroups?.Count);
            Assert.IsTrue(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-000000000001")));
            Assert.IsTrue(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-000000009999")));

            // [GIVEN] User groups were changed in AAD
            this.graphDataProvider.GroupMemberships
                ["00000000-0000-0000-0000-000000000001.00000000-0000-0000-0000-000000000001"] =
                new List<string> { "00000000-0000-0000-9999-ffff00000001", "00000000-0000-0000-9999-ffff00009999" };

            // [WHEN] Another call to a service is made within 'group refresh delay' time
            DateTimeProvider.SpecifiedDateTime += UserTokenFilter.GroupSyncDelay - TimeSpan.FromSeconds(10);
            await this.secretMeta.Run(request, "dummy", claimsPrincipal, this.logger);

            // [THEN] Groups are not refreshed
            user = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals("first@test.test"));
            Assert.IsNotNull(user);
            Assert.AreEqual(lastGroupSync, user?.LastGroupSync);

            userGroups = user?.Groups;
            Assert.IsNotNull(userGroups);
            Assert.AreEqual(2, userGroups?.Count);
            Assert.IsTrue(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-000000000001")));
            Assert.IsTrue(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-000000009999")));
        }

        [Test]
        public async Task UserGroupsAreRefreshedAfterGreaterDelay()
        {
            // [GIVEN] A user with valid credentials, is member of several groups in AAD
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            this.graphDataProvider.GroupMemberships
                ["00000000-0000-0000-0000-000000000001.00000000-0000-0000-0000-000000000001"] =
                new List<string> { "00000000-0000-0000-9999-000000000001", "00000000-0000-0000-9999-000000009999" };

            var request = TestFactory.CreateHttpRequest("get");

            // [GIVEN] Was making calls to a service before
            var response = await this.secretMeta.Run(request, "dummy", claimsPrincipal, this.logger);

            // [GIVEN] User is created in the database with groups
            var user = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals("first@test.test"));
            Assert.IsNotNull(user);
            Assert.AreEqual(DateTimeProvider.SpecifiedDateTime, user?.LastGroupSync);

            var lastGroupSync = user?.LastGroupSync;

            var userGroups = user?.Groups;
            Assert.IsNotNull(userGroups);
            Assert.AreEqual(2, userGroups?.Count);
            Assert.IsTrue(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-000000000001")));
            Assert.IsTrue(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-000000009999")));

            // [GIVEN] User groups were changed in AAD
            this.graphDataProvider.GroupMemberships
                ["00000000-0000-0000-0000-000000000001.00000000-0000-0000-0000-000000000001"] =
                new List<string> { "00000000-0000-0000-9999-ffff00000001", "00000000-0000-0000-9999-ffff00009999", "00000000-0000-0000-9999-eeee00009999" };

            // [WHEN] Another call to a service is made within 'group refresh delay' time
            DateTimeProvider.SpecifiedDateTime += UserTokenFilter.GroupSyncDelay + TimeSpan.FromSeconds(10);
            await this.secretMeta.Run(request, "dummy", claimsPrincipal, this.logger);

            // [THEN] Groups are refreshed
            user = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals("first@test.test"));
            Assert.IsNotNull(user);
            Assert.AreEqual(DateTimeProvider.SpecifiedDateTime, user?.LastGroupSync);

            userGroups = user?.Groups;
            Assert.IsNotNull(userGroups);
            Assert.AreEqual(3, userGroups?.Count);
            Assert.IsTrue(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-ffff00000001")));
            Assert.IsTrue(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-ffff00009999")));
            Assert.IsTrue(userGroups?.Any(g => g.AadGroupId.Equals("00000000-0000-0000-9999-eeee00009999")));
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
            this.globalFilters = new GlobalFilters(this.testConfiguration, this.tokenHelper, this.graphDataProvider, TestFactory.CreateLogger<GlobalFilters>());

            this.blobHelper = new TestBlobHelper();
            this.purger = new PurgeManager(this.testConfiguration, this.blobHelper, TestFactory.CreateLogger<PurgeManager>());

            this.permissionsManager = new PermissionsManager(this.testConfiguration, this.dbContext, TestFactory.CreateLogger<PermissionsManager>());
        }
    }
}