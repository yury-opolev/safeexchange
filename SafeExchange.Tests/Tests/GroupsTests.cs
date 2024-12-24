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
    using Microsoft.Graph.Groups.Item.Onenote.Notebooks.GetNotebookFromWebUrl;
    using Microsoft.IdentityModel.Tokens;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Graph;
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
    public class GroupsTests
    {
        private ILogger logger;

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

        private SafeExchangeGroupSearch  groupSearch;

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

            this.groupSearch = new SafeExchangeGroupSearch(
                this.testConfiguration, this.dbContext, this.graphDataProvider, this.tokenHelper, this.globalFilters);
        }

        [TearDown]
        public void Cleanup()
        {
            this.graphDataProvider.GroupMemberships.Clear();
            this.graphDataProvider.FoundGroups.Clear();
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
            // [GIVEN] Three groups exist in Entra that match search string.
            this.graphDataProvider.FoundGroups.Add(
                "00000000-0000-0000-0000-000000000001.00000000-0000-0000-0000-000000000001",
                new List<GraphGroupInfo>()
                {
                    new GraphGroupInfo()
                    {
                        Id = "00000001-0000-0000-0000-000000000001",
                        DisplayName = "First Group",
                        Mail = "first.group@test.test"
                    },
                    new GraphGroupInfo()
                    {
                        Id = "00000001-0000-0000-0000-000000000002",
                        DisplayName = "Second Group",
                        Mail = null
                    },
                    new GraphGroupInfo()
                    {
                        Id = "00000001-0000-0000-0000-000000000003",
                        DisplayName = "Third Group",
                        Mail = "third.group@test.test"
                    }
                });

            // [GIVEN] No group items are persisted.
            var existingGroupItems = await this.dbContext.GroupDictionary.ToListAsync();
            Assert.That(existingGroupItems.Count, Is.EqualTo(0));

            // [WHEN] Search is run.
            var searchRequest = TestFactory.CreateHttpRequestData("post");
            var searchInput = new SearchInput()
            {
                SearchString = "group"
            };

            searchRequest.SetBodyAsJson(searchInput);

            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var searchResponse = await this.groupSearch.RunSearch(searchRequest, claimsPrincipal, this.logger);

            // [THEN] Three groups are returned.
            var okObjectAccessResult = searchResponse as TestHttpResponseData;

            Assert.That(okObjectAccessResult, Is.Not.Null);
            Assert.That(okObjectAccessResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectAccessResult?.ReadBodyAsJson<BaseResponseObject<List<GraphGroupOutput>>>();
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            Assert.That(responseResult.Result.Count, Is.EqualTo(3));

            Assert.That(responseResult.Result[0].Id, Is.EqualTo("00000001-0000-0000-0000-000000000001"));
            Assert.That(responseResult.Result[1].Id, Is.EqualTo("00000001-0000-0000-0000-000000000002"));
            Assert.That(responseResult.Result[2].Id, Is.EqualTo("00000001-0000-0000-0000-000000000003"));

            existingGroupItems = await this.dbContext.GroupDictionary.ToListAsync();
            Assert.That(existingGroupItems.Count, Is.EqualTo(0));
        }

        [Test]
        public async Task SearchForExistingGroup_PreviouslyPersisted()
        {
            // [GIVEN] Three groups exist in Entra that match search string.
            this.graphDataProvider.FoundGroups.Add(
                "00000000-0000-0000-0000-000000000001.00000000-0000-0000-0000-000000000001",
                new List<GraphGroupInfo>()
                {
                    new GraphGroupInfo()
                    {
                        Id = "00000001-0000-0000-0000-000000000001",
                        DisplayName = "First Group 2",
                        Mail = "first.group@test.test"
                    },
                    new GraphGroupInfo()
                    {
                        Id = "00000001-0000-0000-0000-000000000002",
                        DisplayName = "Second Group 2",
                        Mail = null
                    },
                    new GraphGroupInfo()
                    {
                        Id = "00000001-0000-0000-0000-000000000003",
                        DisplayName = "Third Group 2",
                        Mail = "third.group@test.test"
                    }
                });

            // [GIVEN] First two group items are persisted.
            var group1 = new GroupDictionaryItem(
                "00000001-0000-0000-0000-000000000001",
                "First Group 1",
                "first.group@test.test",
                "User X");

            this.dbContext.GroupDictionary.Add(group1);

            var group2 = new GroupDictionaryItem(
                "00000001-0000-0000-0000-000000000002",
                "Second Group 1",
                null,
                "User X");

            this.dbContext.GroupDictionary.Add(group2);

            await this.dbContext.SaveChangesAsync();

            // [WHEN] Search is run.
            var searchRequest = TestFactory.CreateHttpRequestData("post");
            var searchInput = new SearchInput()
            {
                SearchString = "group"
            };

            searchRequest.SetBodyAsJson(searchInput);

            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var searchResponse = await this.groupSearch.RunSearch(searchRequest, claimsPrincipal, this.logger);

            // [THEN] Three groups are returned from graph source.
            var okObjectAccessResult = searchResponse as TestHttpResponseData;

            Assert.That(okObjectAccessResult, Is.Not.Null);
            Assert.That(okObjectAccessResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectAccessResult?.ReadBodyAsJson<BaseResponseObject<List<GraphGroupOutput>>>();
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            Assert.That(responseResult.Result.Count, Is.EqualTo(3));

            Assert.That(responseResult.Result[0].Id, Is.EqualTo("00000001-0000-0000-0000-000000000001"));
            Assert.That(responseResult.Result[0].DisplayName, Is.EqualTo("First Group 2"));
            Assert.That(responseResult.Result[1].Id, Is.EqualTo("00000001-0000-0000-0000-000000000002"));
            Assert.That(responseResult.Result[1].DisplayName, Is.EqualTo("Second Group 2"));
            Assert.That(responseResult.Result[2].Id, Is.EqualTo("00000001-0000-0000-0000-000000000003"));
            Assert.That(responseResult.Result[2].DisplayName, Is.EqualTo("Third Group 2"));

            var existingGroupItems = await this.dbContext.GroupDictionary.ToListAsync();
            Assert.That(existingGroupItems.Count, Is.EqualTo(2));
        }
    }
}
