/// <summary>
/// GroupsTests
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
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Claims;
    using System.Threading.Tasks;

    [TestFixture]
    public class GroupsTests
    {
        private ILogger logger;

        private SafeExchangeSecretMeta secretMeta;

        private SafeExchangeAccess secretAccess;

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
        private CaseSensitiveClaimsIdentity thirdIdentity;

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            var builder = new ConfigurationBuilder().AddUserSecrets<GroupsTests>();
            var secretConfiguration = builder.Build();

            this.logger = TestFactory.CreateLogger();

            var configurationValues = new Dictionary<string, string>
                {
                    {"Features:UseNotifications", "False"},
                    {"Features:UseGroupsAuthorization", "True"},
                    {"Features:UseGraphGroupSearch", "True"}
                };

            this.testConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(configurationValues)
                .Build();

            var dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(secretConfiguration.GetConnectionString("CosmosDb"), databaseName: $"{nameof(GroupsTests)}Database")
                .Options;

            this.dbContext = new SafeExchangeDbContext(dbContextOptions);
            this.dbContext.Database.EnsureCreated();

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

            this.thirdIdentity = new CaseSensitiveClaimsIdentity(new List<Claim>()
                {
                    new Claim("upn", "third@test.test"),
                    new Claim("displayname", "Third User"),
                    new Claim("oid", "00000000-0000-0000-0000-000000000003"),
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

            this.secretMeta = new SafeExchangeSecretMeta(
                this.testConfiguration, this.dbContext, this.tokenHelper,
                this.globalFilters, this.purger, this.permissionsManager);

            this.secretAccess = new SafeExchangeAccess(
                this.dbContext, this.tokenHelper,
                this.globalFilters, this.purger, this.permissionsManager);
        }

        [TearDown]
        public void Cleanup()
        {
            this.graphDataProvider.GroupMemberships.Clear();
            this.dbContext.ChangeTracker.Clear();

            this.dbContext.Users.RemoveRange(this.dbContext.Users.ToList());
            this.dbContext.Objects.RemoveRange(this.dbContext.Objects.ToList());
            this.dbContext.Permissions.RemoveRange(this.dbContext.Permissions.ToList());
            this.dbContext.AccessRequests.RemoveRange(this.dbContext.AccessRequests.ToList());
            this.dbContext.GroupDictionary.RemoveRange(this.dbContext.GroupDictionary.ToList());
            this.dbContext.SaveChanges();
        }

        [Test]
        public async Task SearchForExistingGroup_NotPersisted()
        {
            // [GIVEN] TODO: ...

            // [WHEN] TODO: ...

            // [THEN] TODO: ...
        }
    }
}
