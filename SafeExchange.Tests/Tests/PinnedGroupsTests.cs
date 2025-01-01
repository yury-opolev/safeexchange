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
    using SafeExchange.Core.Middleware;
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
    public class PinnedGroupsTests
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

        private SafeExchangePinnedGroups pinnedGroups;
        private SafeExchangePinnedGroupsList pinnedGroupsList;

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

            AdminConfiguration ac = new AdminConfiguration();
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
        public async Task RegisterOnePinnedGroup_Sunshine()
        {
            // [GIVEN] No pinned group items are persisted.
            var existingGroupItems = await this.dbContext.PinnedGroups.ToListAsync();
            Assert.That(existingGroupItems.Count, Is.EqualTo(0));

            // [WHEN] New group is pinned for user A.
            var userId = "32100111-0000-0000-0000-321000000111";
            var okObjectAccessResult = await this.RegisterPinnedGroupAsync(
                "00000111-0000-0000-0000-000000000111", "Group Display Name", "test@group.mail",
                userId, this.firstIdentity);

            // [THEN] One group is returned.
            Assert.That(okObjectAccessResult, Is.Not.Null);
            Assert.That(okObjectAccessResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectAccessResult?.ReadBodyAsJson<BaseResponseObject<GraphGroupOutput>>();
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            Assert.That(responseResult.Result.Id, Is.EqualTo("00000111-0000-0000-0000-000000000111"));
            Assert.That(responseResult.Result.DisplayName, Is.EqualTo("Group Display Name"));
            Assert.That(responseResult.Result.Mail, Is.EqualTo("test@group.mail"));

            // [THEN] One group is persisted in the database.
            existingGroupItems = await this.dbContext.PinnedGroups.ToListAsync();
            Assert.That(existingGroupItems.Count, Is.EqualTo(1));

            var existingGroupItem = existingGroupItems.First();
            Assert.That(existingGroupItem.UserId, Is.EqualTo(userId));
            Assert.That(existingGroupItem.GroupItemId, Is.EqualTo("00000111-0000-0000-0000-000000000111"));
        }

        [Test]
        public async Task RegisterPinnedGroups_DifferentUsers()
        {
            // [GIVEN] No pinned group items are persisted.
            var existingGroupItems = await this.dbContext.PinnedGroups.ToListAsync();
            Assert.That(existingGroupItems.Count, Is.EqualTo(0));

            // [GIVEN] New group X is pinned for user A, group Y is pinned for user B.
            var firstUserId = "32100111-0000-0000-0000-321000000111";
            await this.RegisterPinnedGroupAsync(
                "00000111-0000-0000-0000-000000000111", "Group Display Name", "test@group.mail",
                firstUserId, this.firstIdentity);

            var secondUserId = "32100222-0000-0000-0000-321000000222";
            await this.RegisterPinnedGroupAsync(
                "00000222-0000-0000-0000-000000000222", "Group 2 Display Name", "test2@group.mail",
                secondUserId, this.secondIdentity);

            // [WHEN] User A is listing pinned groups, user B is also listing pinned groups.
            var firstUserPinnedGroupsResponse = await this.ListPinnedGroupsAsync(firstUserId, this.firstIdentity);
            Assert.That(firstUserPinnedGroupsResponse, Is.Not.Null);
            Assert.That(firstUserPinnedGroupsResponse?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = firstUserPinnedGroupsResponse?.ReadBodyAsJson<BaseResponseObject<List<PinnedGroupOutput>>>();
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            Assert.That(responseResult.Result.Count, Is.EqualTo(1));
            Assert.That(responseResult.Result[0].GroupId, Is.EqualTo("00000111-0000-0000-0000-000000000111"));
            Assert.That(responseResult.Result[0].GroupDisplayName, Is.EqualTo("Group Display Name"));
            Assert.That(responseResult.Result[0].GroupMail, Is.EqualTo("test@group.mail"));

            var secondUserPinnedGroupsResponse = await this.ListPinnedGroupsAsync(secondUserId, this.secondIdentity);
            Assert.That(secondUserPinnedGroupsResponse, Is.Not.Null);
            Assert.That(secondUserPinnedGroupsResponse?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            responseResult = secondUserPinnedGroupsResponse?.ReadBodyAsJson<BaseResponseObject<List<PinnedGroupOutput>>>();
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            Assert.That(responseResult.Result.Count, Is.EqualTo(1));
            Assert.That(responseResult.Result[0].GroupId, Is.EqualTo("00000222-0000-0000-0000-000000000222"));
            Assert.That(responseResult.Result[0].GroupDisplayName, Is.EqualTo("Group 2 Display Name"));
            Assert.That(responseResult.Result[0].GroupMail, Is.EqualTo("test2@group.mail"));
        }

        private TestHttpRequestData CreatePinnedGroupRegistrationRequest(string groupId, string groupDisplayName, string? groupMail, string userId)
        {
            var groupRegistrationRequest = TestFactory.CreateHttpRequestData("put");
            var groupInput = new PinnedGroupInput()
            {
                GroupId = groupId,
                GroupDisplayName = groupDisplayName,
                GroupMail = groupMail
            };

            groupRegistrationRequest.SetBodyAsJson(groupInput);
            groupRegistrationRequest.FunctionContext.Items[DefaultAuthenticationMiddleware.InvocationContextUserIdKey] = userId;
            return groupRegistrationRequest;
        }

        private async Task<TestHttpResponseData?> RegisterPinnedGroupAsync(string groupId, string groupDisplayName, string? groupMail, string userId, CaseSensitiveClaimsIdentity identity)
        {
            var groupRegistrationRequest = this.CreatePinnedGroupRegistrationRequest(groupId, groupDisplayName, groupMail, userId);
            var claimsPrincipal = new ClaimsPrincipal(identity);
            var groupResponse = await this.pinnedGroups.Run(groupRegistrationRequest, groupId, claimsPrincipal, this.logger);

            return groupResponse as TestHttpResponseData;
        }

        private async Task<TestHttpResponseData?> ListPinnedGroupsAsync(string userId, CaseSensitiveClaimsIdentity identity)
        {
            var listPinnedGroupsRequest = TestFactory.CreateHttpRequestData("get");
            var claimsPrincipal = new ClaimsPrincipal(identity);
            listPinnedGroupsRequest.FunctionContext.Items[DefaultAuthenticationMiddleware.InvocationContextUserIdKey] = userId;
            var groupResponse = await this.pinnedGroupsList.RunList(listPinnedGroupsRequest, claimsPrincipal, this.logger);

            return groupResponse as TestHttpResponseData;
        }
    }
}
