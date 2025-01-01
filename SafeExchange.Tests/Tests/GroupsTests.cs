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
    using SafeExchange.Core.Functions.Admin;
    using SafeExchange.Core.Graph;
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
    public class GroupsTests
    {
        private ILogger logger;

        private IConfiguration testConfiguration;

        private SafeExchangeDbContext dbContext;

        private ITokenHelper tokenHelper;

        private TestGraphDataProvider graphDataProvider;

        private GlobalFilters globalFilters;

        private CaseSensitiveClaimsIdentity firstIdentity;
        private CaseSensitiveClaimsIdentity secondIdentity;
        private CaseSensitiveClaimsIdentity thirdIdentity;
        private CaseSensitiveClaimsIdentity adminIdentity;

        private SafeExchangeGroupSearch groupSearch;
        private SafeExchangeGroups groups;
        private SafeExchangeGroupsList groupsList;
        private SafeExchangePinnedGroups pinnedGroups;
        private SafeExchangePinnedGroupsList pinnedGroupsList;

        private SafeExchangeAdminGroups groupsAdministration;

        private DbContextOptions<SafeExchangeDbContext> dbContextOptions;

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

            this.dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(secretConfiguration.GetConnectionString("CosmosDb"), databaseName: $"{nameof(GroupsTests)}Database")
                .Options;

            this.dbContext = new SafeExchangeDbContext(this.dbContextOptions);
            this.dbContext.Database.EnsureCreated();

            this.tokenHelper = new TestTokenHelper();
            this.graphDataProvider = new TestGraphDataProvider();

            GloballyAllowedGroupsConfiguration gagc = new GloballyAllowedGroupsConfiguration();
            var groupsConfiguration = Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(x => x.CurrentValue == gagc);

            AdminConfiguration ac = new AdminConfiguration()
            {
                AdminUsers = "00000321-0000-0000-0000-000000000321"
            };

            var adminConfiguration = Mock.Of<IOptionsMonitor<AdminConfiguration>>(x => x.CurrentValue == ac);

            this.globalFilters = new GlobalFilters(groupsConfiguration, adminConfiguration, this.tokenHelper, TestFactory.CreateLogger<GlobalFilters>());

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

            this.adminIdentity = new CaseSensitiveClaimsIdentity(new List<Claim>()
                {
                    new Claim("upn", "adm@test.test"),
                    new Claim("displayname", "Admin User"),
                    new Claim("oid", "00000321-0000-0000-0000-000000000321"),
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
            this.groups = new SafeExchangeGroups(this.dbContext, this.tokenHelper, this.globalFilters);
            this.groupsList = new SafeExchangeGroupsList(this.dbContext, this.tokenHelper, this.globalFilters);

            this.groupsAdministration = new SafeExchangeAdminGroups(this.dbContext, this.tokenHelper, this.globalFilters);

            this.pinnedGroups = new SafeExchangePinnedGroups(this.dbContext, this.tokenHelper, this.globalFilters);
            this.pinnedGroupsList = new SafeExchangePinnedGroupsList(this.dbContext, this.tokenHelper, this.globalFilters);
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
            this.dbContext.PinnedGroups.RemoveRange(this.dbContext.PinnedGroups.ToList());
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

        [Test]
        public async Task RegisterOneGroup_Sunshine()
        {
            // [GIVEN] No group items are persisted.
            var existingGroupItems = await this.dbContext.GroupDictionary.ToListAsync();
            Assert.That(existingGroupItems.Count, Is.EqualTo(0));

            // [WHEN] New group is registered.
            var okObjectAccessResult = await this.RegisterGroupAsync(
                "00000011-0000-0000-0000-000000000011", "Group Display Name", "test@group.mail");

            // [THEN] One group is returned.
            Assert.That(okObjectAccessResult, Is.Not.Null);
            Assert.That(okObjectAccessResult?.StatusCode, Is.EqualTo(HttpStatusCode.Created));

            var responseResult = okObjectAccessResult?.ReadBodyAsJson<BaseResponseObject<GraphGroupOutput>>();
            Assert.That(responseResult?.Status, Is.EqualTo("created"));
            Assert.That(responseResult?.Error, Is.Null);

            Assert.That(responseResult.Result.Id, Is.EqualTo("00000011-0000-0000-0000-000000000011"));
            Assert.That(responseResult.Result.DisplayName, Is.EqualTo("Group Display Name"));
            Assert.That(responseResult.Result.Mail, Is.EqualTo("test@group.mail"));

            // [THEN] One group is persisted in the database.
            existingGroupItems = await this.dbContext.GroupDictionary.ToListAsync();
            Assert.That(existingGroupItems.Count, Is.EqualTo(1));

            var existingGroupItem = existingGroupItems.First();
            Assert.That(existingGroupItem.GroupId, Is.EqualTo("00000011-0000-0000-0000-000000000011"));
            Assert.That(existingGroupItem.DisplayName, Is.EqualTo("Group Display Name"));
            Assert.That(existingGroupItem.GroupMail, Is.EqualTo("test@group.mail"));
        }

        [Test]
        public async Task GetGroupAfterRegistration()
        {
            // [GIVEN] Two groups are registered.
            await this.RegisterGroupAsync(
                "00000011-0000-0000-0000-000000000011", "Group Display Name", "test@group.mail");
            await this.RegisterGroupAsync(
                "00000022-0000-0000-0000-000000000022", "Group Display Name 2", "test2@group.mail");

            // [WHEN] A registered group is read.
            var okObjectAccessResult = await this.GetGroupAsync("00000022-0000-0000-0000-000000000022");

            // [THEN] The correct group is returned.
            Assert.That(okObjectAccessResult, Is.Not.Null);
            Assert.That(okObjectAccessResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectAccessResult?.ReadBodyAsJson<BaseResponseObject<GraphGroupOutput>>();
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            Assert.That(responseResult.Result.Id, Is.EqualTo("00000022-0000-0000-0000-000000000022"));
            Assert.That(responseResult.Result.DisplayName, Is.EqualTo("Group Display Name 2"));
            Assert.That(responseResult.Result.Mail, Is.EqualTo("test2@group.mail"));
        }

        [Test]
        public async Task ListGroupsAfterRegistration()
        {
            // [GIVEN] Three groups are registered, one of them does not have group mail.
            await this.RegisterGroupAsync(
                "00000011-0000-0000-0000-000000000011", "Group Display Name", "test@group.mail");
            await this.RegisterGroupAsync(
                "00000022-0000-0000-0000-000000000022", "Group Display Name 2", null);
            await this.RegisterGroupAsync(
                "00000033-0000-0000-0000-000000000033", "Group Display Name 3", "test3@group.mail");

            // [WHEN] Registered groups are listed.
            var okObjectAccessResult = await this.ListGroupsAsync();

            // [THEN] Only the groups with registered emails are returned.
            Assert.That(okObjectAccessResult, Is.Not.Null);
            Assert.That(okObjectAccessResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectAccessResult?.ReadBodyAsJson<BaseResponseObject<List<GroupOverviewOutput>>>();
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            Assert.That(responseResult.Result.Count, Is.EqualTo(2));

            Assert.That(responseResult.Result[0].DisplayName, Is.EqualTo("Group Display Name"));
            Assert.That(responseResult.Result[0].GroupMail, Is.EqualTo("test@group.mail"));

            Assert.That(responseResult.Result[1].DisplayName, Is.EqualTo("Group Display Name 3"));
            Assert.That(responseResult.Result[1].GroupMail, Is.EqualTo("test3@group.mail"));
        }

        [Test]
        public async Task DeleteExistingGroup()
        {
            // [GIVEN] Three groups are registered.
            await this.RegisterGroupAsync(
                "00000011-0000-0000-0000-000000000011", "Group Display Name", "test@group.mail");
            await this.RegisterGroupAsync(
                "00000022-0000-0000-0000-000000000022", "Group Display Name 2", "test2@group.mail");
            await this.RegisterGroupAsync(
                "00000033-0000-0000-0000-000000000033", "Group Display Name 3", "test3@group.mail");

            // [WHEN] One group is deleted by admin.
            var deletionResult = await this.DeleteGroupAsync("00000022-0000-0000-0000-000000000022", this.adminIdentity);

            Assert.That(deletionResult, Is.Not.Null);
            Assert.That(deletionResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var deletionResponse = deletionResult?.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.That(deletionResponse?.Status, Is.EqualTo("ok"));
            Assert.That(deletionResponse?.Error, Is.Null);
            Assert.That(deletionResponse?.Result, Is.EqualTo("ok"));

            // [THEN] The groups do not contain deleted item.
            var okObjectAccessResult = await this.ListGroupsAsync();
            var responseResult = okObjectAccessResult?.ReadBodyAsJson<BaseResponseObject<List<GroupOverviewOutput>>>();
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            Assert.That(responseResult.Result.Count, Is.EqualTo(2));

            Assert.That(responseResult.Result[0].DisplayName, Is.EqualTo("Group Display Name"));
            Assert.That(responseResult.Result[0].GroupMail, Is.EqualTo("test@group.mail"));

            Assert.That(responseResult.Result[1].DisplayName, Is.EqualTo("Group Display Name 3"));
            Assert.That(responseResult.Result[1].GroupMail, Is.EqualTo("test3@group.mail"));
        }

        [Test]
        public async Task TryDeleteExistingGroup_NotAnAdmin()
        {
            // [GIVEN] Three groups are registered.
            await this.RegisterGroupAsync(
                "00000011-0000-0000-0000-000000000011", "Group Display Name", "test@group.mail");
            await this.RegisterGroupAsync(
                "00000022-0000-0000-0000-000000000022", "Group Display Name 2", null);
            await this.RegisterGroupAsync(
                "00000033-0000-0000-0000-000000000033", "Group Display Name 3", null);

            // [WHEN] One group is tried to be deleted by non-admin.
            var deletionResult = await this.DeleteGroupAsync("00000022-0000-0000-0000-000000000022", this.firstIdentity);

            Assert.That(deletionResult, Is.Not.Null);
            Assert.That(deletionResult?.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

            var deletionResponse = deletionResult?.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.That(deletionResponse?.Status, Is.EqualTo("forbidden"));
            Assert.That(deletionResponse?.Result, Is.Null);
            Assert.That(deletionResponse?.Error, Is.EqualTo("Not an admin or a member of an admin group."));

            // [THEN] The groups contain all previous items.
            var groups = this.dbContext.GroupDictionary.ToList();
            Assert.That(groups.Count, Is.EqualTo(3));

            // [THEN] The groups list returns only groups with non-empty email.
            var okObjectAccessResult = await this.ListGroupsAsync();
            var responseResult = okObjectAccessResult?.ReadBodyAsJson<BaseResponseObject<List<GroupOverviewOutput>>>();
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            Assert.That(responseResult.Result.Count, Is.EqualTo(1));

            Assert.That(responseResult.Result[0].DisplayName, Is.EqualTo("Group Display Name"));
            Assert.That(responseResult.Result[0].GroupMail, Is.EqualTo("test@group.mail"));
        }

        [Test]
        public async Task TryGetInexistentGroup()
        {
            // [GIVEN] Two groups are registered.
            await this.RegisterGroupAsync(
                "00000011-0000-0000-0000-000000000011", "Group Display Name", "test@group.mail");
            await this.RegisterGroupAsync(
                "00000022-0000-0000-0000-000000000022", "Group Display Name 2", "test2@group.mail");

            // [WHEN] An inexistent group is trying to be read.
            var okObjectAccessResult = await this.GetGroupAsync("00000033-0000-0000-0000-000000000033");

            // [THEN] The result that is returned is 'no content'.
            Assert.That(okObjectAccessResult, Is.Not.Null);
            Assert.That(okObjectAccessResult?.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

            var responseResult = okObjectAccessResult?.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.That(responseResult?.Status, Is.EqualTo("no_content"));
            Assert.That(responseResult?.Error, Is.Null);

            Assert.That(responseResult.Result, Is.EqualTo("Group registration '00000033-0000-0000-0000-000000000033' does not exist."));
        }

        [Test]
        public async Task RegisterOneGroup_MultipleTimes()
        {
            // [GIVEN] No group items are persisted.
            var existingGroupItems = await this.dbContext.GroupDictionary.ToListAsync();
            Assert.That(existingGroupItems.Count, Is.EqualTo(0));

            // [WHEN] New group is registered.
            var response = await RegisterGroupAsync(
                "00000011-0000-0000-0000-000000000011", "Group Display Name", "test@group.mail");
            response = await RegisterGroupAsync(
                "00000011-0000-0000-0000-000000000011", "Group Display Name", "test@group.mail");
            response = await RegisterGroupAsync(
                "00000011-0000-0000-0000-000000000011", "Group Display Name", "test@group.mail");

            // [THEN] One group is returned.
            Assert.That(response, Is.Not.Null);
            Assert.That(response?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = response?.ReadBodyAsJson<BaseResponseObject<GraphGroupOutput>>();
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            Assert.That(responseResult.Result.Id, Is.EqualTo("00000011-0000-0000-0000-000000000011"));
            Assert.That(responseResult.Result.DisplayName, Is.EqualTo("Group Display Name"));
            Assert.That(responseResult.Result.Mail, Is.EqualTo("test@group.mail"));

            // [THEN] One group is persisted in the database.
            existingGroupItems = await this.dbContext.GroupDictionary.ToListAsync();
            Assert.That(existingGroupItems.Count, Is.EqualTo(1));

            var existingGroupItem = existingGroupItems.First();
            Assert.That(existingGroupItem.GroupId, Is.EqualTo("00000011-0000-0000-0000-000000000011"));
            Assert.That(existingGroupItem.DisplayName, Is.EqualTo("Group Display Name"));
            Assert.That(existingGroupItem.GroupMail, Is.EqualTo("test@group.mail"));
        }

        [Test]
        public async Task RegisterOneGroup_Simultaneously()
        {
            // [GIVEN] No group items are persisted.
            var existingGroupItems = await this.dbContext.GroupDictionary.ToListAsync();
            Assert.That(existingGroupItems.Count, Is.EqualTo(0));

            // [WHEN] New group is registered in several calls simultaneously.
            var groupId = "00000011-0000-0000-0000-000000000011";
            var groupDisplayName = "Group Display Name";
            var groupMail = "test@group.mail";
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var groups1 = new SafeExchangeGroups(
                new SafeExchangeDbContext(this.dbContextOptions), this.tokenHelper, this.globalFilters);
            var groups2 = new SafeExchangeGroups(
                new SafeExchangeDbContext(this.dbContextOptions), this.tokenHelper, this.globalFilters);
            var groups3 = new SafeExchangeGroups(
                new SafeExchangeDbContext(this.dbContextOptions), this.tokenHelper, this.globalFilters);

            await Task.WhenAll([
                Task.Run(async () =>
                {
                    var groupRegistrationRequest = this.CreateGroupRegistrationRequest(groupId, groupDisplayName, groupMail);
                    await groups1.Run(groupRegistrationRequest, groupId, claimsPrincipal, this.logger);
                }),
                Task.Run(async () =>
                {
                    var groupRegistrationRequest = this.CreateGroupRegistrationRequest(groupId, groupDisplayName, groupMail);
                    await groups2.Run(groupRegistrationRequest, groupId, claimsPrincipal, this.logger);
                }),
                Task.Run(async () =>
                {
                    var groupRegistrationRequest = this.CreateGroupRegistrationRequest(groupId, groupDisplayName, groupMail);
                    await groups3.Run(groupRegistrationRequest, groupId, claimsPrincipal, this.logger);
                })
            ]);

            // [THEN] One group is persisted in the database.
            existingGroupItems = await this.dbContext.GroupDictionary.ToListAsync();
            Assert.That(existingGroupItems.Count, Is.EqualTo(1));

            var existingGroupItem = existingGroupItems.First();
            Assert.That(existingGroupItem.GroupId, Is.EqualTo("00000011-0000-0000-0000-000000000011"));
            Assert.That(existingGroupItem.DisplayName, Is.EqualTo("Group Display Name"));
            Assert.That(existingGroupItem.GroupMail, Is.EqualTo("test@group.mail"));
        }

        private TestHttpRequestData CreateGroupRegistrationRequest(string groupId, string displayName, string? groupMail)
        {
            var groupRegistrationRequest = TestFactory.CreateHttpRequestData("put");
            var groupInput = new GroupInput()
            {
                DisplayName = displayName,
                Mail = groupMail
            };

            groupRegistrationRequest.SetBodyAsJson(groupInput);
            return groupRegistrationRequest;
        }

        private async Task<TestHttpResponseData?> RegisterGroupAsync(string groupId, string displayName, string? groupMail)
        {
            var groupRegistrationRequest = this.CreateGroupRegistrationRequest(groupId, displayName, groupMail);
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var groupResponse = await this.groups.Run(groupRegistrationRequest, groupId, claimsPrincipal, this.logger);

            return groupResponse as TestHttpResponseData;
        }

        private async Task<TestHttpResponseData?> GetGroupAsync(string groupId)
        {
            var groupRegistrationRequest = TestFactory.CreateHttpRequestData("get");
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var groupResponse = await this.groups.Run(groupRegistrationRequest, groupId, claimsPrincipal, this.logger);

            return groupResponse as TestHttpResponseData;
        }

        private async Task<TestHttpResponseData?> ListGroupsAsync()
        {
            var groupRegistrationRequest = TestFactory.CreateHttpRequestData("get");
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var groupResponse = await this.groupsList.RunList(groupRegistrationRequest, claimsPrincipal, this.logger);

            return groupResponse as TestHttpResponseData;
        }

        private async Task<TestHttpResponseData?> DeleteGroupAsync(string groupId, CaseSensitiveClaimsIdentity identity)
        {
            var groupDeletionRequest = TestFactory.CreateHttpRequestData("delete");
            var claimsPrincipal = new ClaimsPrincipal(identity);
            var deletionResponse = await this.groupsAdministration.Run(groupDeletionRequest, groupId, claimsPrincipal, this.logger);

            return deletionResponse as TestHttpResponseData;
        }
    }
}
