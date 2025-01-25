/// <summary>
/// SecretAccessTests
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
    using SafeExchange.Core.DelayedTasks;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Groups;
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
    public class SecretAccessRequestTests
    {
        private ILogger logger;

        private SafeExchangeSecretMeta secretMeta;

        private SafeExchangeAccess secretAccess;

        private SafeExchangeAccessRequest secretAccessRequest;

        private IConfiguration testConfiguration;

        private SafeExchangeDbContext dbContext;

        private IGroupsManager groupsManager;

        private ITokenHelper tokenHelper;

        private TestGraphDataProvider graphDataProvider;

        private GlobalFilters globalFilters;

        private IBlobHelper blobHelper;

        private IPurger purger;

        private IPermissionsManager permissionsManager;

        private IDelayedTaskScheduler delayedTaskScheduler;

        private CaseSensitiveClaimsIdentity firstIdentity;
        private CaseSensitiveClaimsIdentity secondIdentity;
        private CaseSensitiveClaimsIdentity thirdIdentity;

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            var builder = new ConfigurationBuilder().AddUserSecrets<SecretAccessRequestTests>();
            var secretConfiguration = builder.Build();

            this.logger = TestFactory.CreateLogger(LoggerTypes.Console);

            var configurationValues = new Dictionary<string, string>
                {
                    {"Features:UseNotifications", "False"},
                    {"Features:UseGroupsAuthorization", "True"}
                };

            this.testConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(configurationValues)
                .Build();

            var dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(secretConfiguration.GetConnectionString("CosmosDb"), databaseName: $"{nameof(SecretAccessRequestTests)}Database")
                .Options;

            this.dbContext = new SafeExchangeDbContext(dbContextOptions);
            this.dbContext.Database.EnsureCreated();

            this.groupsManager = new GroupsManager(this.dbContext, Mock.Of<ILogger<GroupsManager>>());
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

            this.delayedTaskScheduler = new NullDelayedTaskScheduler();

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
                this.dbContext, this.groupsManager, this.tokenHelper,
                this.globalFilters, this.purger, this.permissionsManager);

            this.secretAccessRequest = new SafeExchangeAccessRequest(
                this.testConfiguration, this.dbContext, this.globalFilters,
                this.tokenHelper, this.purger, this.permissionsManager, this.delayedTaskScheduler);
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
        public async Task CannotRequestAccessForDifferentUser()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [WHEN] A second user has requested read access to the secret with specified third user.
            var accessRequest = TestFactory.CreateHttpRequestData("post");
            var accessInput = new SubjectPermissionsInput()
            {
                SubjectName = "third@test.test",
                CanRead = true,
                CanWrite = false,
                CanGrantAccess = false,
                CanRevokeAccess = false
            };

            accessRequest.SetBodyAsJson(accessInput);

            var claimsPrincipal = new ClaimsPrincipal(this.secondIdentity);
            var response = await this.secretAccessRequest.Run(accessRequest, "sunshine", claimsPrincipal, this.logger);

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error, but requester is the second user.
            var testResponse = response as TestHttpResponseData;
            Assert.That(testResponse, Is.Not.Null);
            Assert.That(testResponse?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = testResponse.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);
            Assert.That(responseResult?.Result, Is.EqualTo("ok"));

            var accessRequests = await this.dbContext.AccessRequests.Where(ar => ar.ObjectName.Equals("sunshine")).ToListAsync();
            Assert.That(accessRequests.Count, Is.EqualTo(1));
            Assert.That(accessRequests.First().SubjectName, Is.EqualTo("second@test.test"));
            Assert.That(accessRequests.First().Permission, Is.EqualTo(PermissionType.Read));
            Assert.That(accessRequests.First().Status, Is.EqualTo(RequestStatus.InProgress));
        }

        [Test]
        public async Task ListRequestsSunshine()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [WHEN] A second user has requested read/write access to the secret.
            await this.RequestAccess(this.secondIdentity, "sunshine", "second@test.test", true, true, false, false);

            var accessRequests = await this.dbContext.AccessRequests.Where(ar => ar.ObjectName.Equals("sunshine")).ToListAsync();

            Assert.That(accessRequests.Count, Is.EqualTo(1));
            Assert.That(accessRequests.First().SubjectName, Is.EqualTo("second@test.test"));
            Assert.That(accessRequests.First().Permission, Is.EqualTo(PermissionType.Read | PermissionType.Write));
            Assert.That(accessRequests.First().Status, Is.EqualTo(RequestStatus.InProgress));

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromMinutes(1);

            // [THEN] Both first and second user can see the request is requests list.
            var requests = await this.ListRequests(this.firstIdentity);
            Assert.That(requests.Count, Is.EqualTo(1));

            var requestId = requests.First().Id;
            Assert.That(requests.First().SubjectName, Is.EqualTo("second@test.test"));
            Assert.That(requests.First().ObjectName, Is.EqualTo("sunshine"));

            Assert.That(requests.First().CanRead, Is.True);
            Assert.That(requests.First().CanWrite, Is.True);
            Assert.That(requests.First().CanGrantAccess, Is.False);
            Assert.That(requests.First().CanRevokeAccess, Is.False);

            Assert.That(requests.First().RequestedAt, Is.EqualTo(DateTimeProvider.SpecifiedDateTime - TimeSpan.FromMinutes(1)));

            requests = await this.ListRequests(this.secondIdentity);
            Assert.That(requests.Count, Is.EqualTo(1));

            requestId = requests.First().Id;
            Assert.That(requests.First().SubjectName, Is.EqualTo("second@test.test"));
            Assert.That(requests.First().ObjectName, Is.EqualTo("sunshine"));

            Assert.That(requests.First().CanRead, Is.True);
            Assert.That(requests.First().CanWrite, Is.True);
            Assert.That(requests.First().CanGrantAccess, Is.False);
            Assert.That(requests.First().CanRevokeAccess, Is.False);

            Assert.That(requests.First().RequestedAt, Is.EqualTo(DateTimeProvider.SpecifiedDateTime - TimeSpan.FromMinutes(1)));
        }

        [Test]
        public async Task GrantReadAccess()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [GIVEN] A second user has requested read access to the secret.
            await this.RequestAccess(this.secondIdentity, "sunshine", "second@test.test", true, false, false, false);

            var accessRequests = await this.dbContext.AccessRequests.Where(ar => ar.ObjectName.Equals("sunshine")).ToListAsync();

            Assert.That(accessRequests.Count, Is.EqualTo(1));
            Assert.That(accessRequests.First().SubjectName, Is.EqualTo("second@test.test"));
            Assert.That(accessRequests.First().Permission, Is.EqualTo(PermissionType.Read));
            Assert.That(accessRequests.First().Status, Is.EqualTo(RequestStatus.InProgress));

            // [WHEN] The first user has approved the request.
            var requests = await this.ListRequests(this.firstIdentity);
            Assert.That(requests.Count, Is.EqualTo(1));
            var requestId = requests.First().Id;

            var approvalRequest = TestFactory.CreateHttpRequestData("patch");
            var approvalInput = new AccessRequestUpdateInput()
            {
                RequestId = requestId,
                Approve = true
            };

            approvalRequest.SetBodyAsJson(approvalInput);

            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var approvalResponse = await this.secretAccessRequest.Run(approvalRequest, "sunshine", claimsPrincipal, this.logger);

            var approvalOkResult = approvalResponse as TestHttpResponseData;
            Assert.That(approvalOkResult, Is.Not.Null);
            Assert.That(approvalOkResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var approvalResult = approvalOkResult?.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.That(approvalResult?.Status, Is.EqualTo("ok"));
            Assert.That(approvalResult?.Error, Is.Null);
            Assert.That(approvalResult?.Result, Is.EqualTo("ok"));

            // [THEN] The second user has acquired read access to the secret.
            Assert.That(await this.permissionsManager.IsAuthorizedAsync(SubjectType.User, "second@test.test", "sunshine", PermissionType.Read), Is.True);
            Assert.That(await this.permissionsManager.IsAuthorizedAsync(SubjectType.User, "second@test.test", "sunshine", PermissionType.Write), Is.False);
            Assert.That(await this.permissionsManager.IsAuthorizedAsync(SubjectType.User, "second@test.test", "sunshine", PermissionType.GrantAccess), Is.False);
            Assert.That(await this.permissionsManager.IsAuthorizedAsync(SubjectType.User, "second@test.test", "sunshine", PermissionType.RevokeAccess), Is.False);

            var permissions = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("sunshine") && p.SubjectId.Equals("second@test.test"))
                .ToListAsync();

            Assert.That(permissions.Count, Is.EqualTo(1));
            
            Assert.That(permissions.First().CanRead, Is.True);
            Assert.That(permissions.First().CanWrite, Is.False);
            Assert.That(permissions.First().CanGrantAccess, Is.False);
            Assert.That(permissions.First().CanRevokeAccess, Is.False);
        }

        [Test]
        public async Task CancelAccessRequest()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [GIVEN] A second user has requested read access to the secret.
            await this.RequestAccess(this.secondIdentity, "sunshine", "second@test.test", true, false, false, false);

            var accessRequests = await this.dbContext.AccessRequests.Where(ar => ar.ObjectName.Equals("sunshine")).ToListAsync();

            Assert.That(accessRequests.Count, Is.EqualTo(1));
            Assert.That(accessRequests.First().SubjectName, Is.EqualTo("second@test.test"));
            Assert.That(accessRequests.First().Permission, Is.EqualTo(PermissionType.Read));
            Assert.That(accessRequests.First().Status, Is.EqualTo(RequestStatus.InProgress));

            // [WHEN] The second user has cancelled the request.
            var requests = await this.ListRequests(this.secondIdentity);
            Assert.That(requests.Count, Is.EqualTo(1));
            var requestId = requests.First().Id;

            var deletionRequest = TestFactory.CreateHttpRequestData("delete");
            var deletionInput = new AccessRequestDeletionInput()
            {
                RequestId = requestId
            };

            deletionRequest.SetBodyAsJson(deletionInput);

            var claimsPrincipal = new ClaimsPrincipal(this.secondIdentity);
            var deletionResponse = await this.secretAccessRequest.Run(deletionRequest, "sunshine", claimsPrincipal, this.logger);
            var deletionOkResult = deletionResponse as TestHttpResponseData;

            Assert.That(deletionOkResult, Is.Not.Null);
            Assert.That(deletionOkResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var deletionResult = deletionOkResult?.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.That(deletionResult?.Status, Is.EqualTo("ok"));
            Assert.That(deletionResult?.Error, Is.Null);
            Assert.That(deletionResult?.Result, Is.EqualTo("ok"));

            // [THEN] The access request is deleted from database, the second user does not have permissions for the secret.
            Assert.That(await this.permissionsManager.IsAuthorizedAsync(SubjectType.User, "second@test.test", "sunshine", PermissionType.Read), Is.False);
            Assert.That(await this.permissionsManager.IsAuthorizedAsync(SubjectType.User, "second@test.test", "sunshine", PermissionType.Write), Is.False);
            Assert.That(await this.permissionsManager.IsAuthorizedAsync(SubjectType.User, "second@test.test", "sunshine", PermissionType.GrantAccess), Is.False);
            Assert.That(await this.permissionsManager.IsAuthorizedAsync(SubjectType.User, "second@test.test", "sunshine", PermissionType.RevokeAccess), Is.False);

            accessRequests = await this.dbContext.AccessRequests.Where(ar => ar.ObjectName.Equals("sunshine")).ToListAsync();
            Assert.That(accessRequests.Count, Is.EqualTo(0));
        }

        [Test]
        public async Task CannotCancelOtherAccessRequest()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [GIVEN] A second user has requested read access to the secret.
            await this.RequestAccess(this.secondIdentity, "sunshine", "second@test.test", true, false, false, false);

            var accessRequests = await this.dbContext.AccessRequests.Where(ar => ar.ObjectName.Equals("sunshine")).ToListAsync();

            Assert.That(accessRequests.Count, Is.EqualTo(1));
            Assert.That(accessRequests.First().SubjectName, Is.EqualTo("second@test.test"));
            Assert.That(accessRequests.First().Permission, Is.EqualTo(PermissionType.Read));
            Assert.That(accessRequests.First().Status, Is.EqualTo(RequestStatus.InProgress));

            // [WHEN] The first user is trying to cancel the request.
            var requests = await this.ListRequests(this.firstIdentity);
            Assert.That(requests.Count, Is.EqualTo(1));
            var requestId = requests.First().Id;

            var deletionRequest = TestFactory.CreateHttpRequestData("delete");
            var deletionInput = new AccessRequestDeletionInput()
            {
                RequestId = requestId
            };

            deletionRequest.SetBodyAsJson(deletionInput);

            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var deletionResponse = await this.secretAccessRequest.Run(deletionRequest, "sunshine", claimsPrincipal, this.logger);
            var deletionForbiddenResult = deletionResponse as TestHttpResponseData;

            // [THEN] The first user receives 'Forbidden' response and access request is not deleted.
            Assert.That(deletionForbiddenResult, Is.Not.Null);
            Assert.That(deletionForbiddenResult?.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

            var deletionResult = deletionForbiddenResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(deletionResult?.Status, Is.EqualTo("forbidden"));
            Assert.That(deletionResult?.Error, Is.Not.Null);
            Assert.That(deletionResult?.Result, Is.Null);

            accessRequests = await this.dbContext.AccessRequests.Where(ar => ar.ObjectName.Equals("sunshine")).ToListAsync();
            Assert.That(accessRequests.Count, Is.EqualTo(1));
        }

        [Test]
        public async Task DenyReadAccess()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [GIVEN] A second user has requested read access to the secret.
            await this.RequestAccess(this.secondIdentity, "sunshine", "second@test.test", true, false, false, false);

            var accessRequests = await this.dbContext.AccessRequests.Where(ar => ar.ObjectName.Equals("sunshine")).ToListAsync();

            Assert.That(accessRequests.Count, Is.EqualTo(1));
            Assert.That(accessRequests.First().SubjectName, Is.EqualTo("second@test.test"));
            Assert.That(accessRequests.First().Permission, Is.EqualTo(PermissionType.Read));
            Assert.That(accessRequests.First().Status, Is.EqualTo(RequestStatus.InProgress));

            // [WHEN] The first user has rejected the request.
            var requests = await this.ListRequests(this.firstIdentity);
            Assert.That(requests.Count, Is.EqualTo(1));
            var requestId = requests.First().Id;

            var approvalRequest = TestFactory.CreateHttpRequestData("patch");
            var approvalInput = new AccessRequestUpdateInput()
            {
                RequestId = requestId,
                Approve = false
            };

            approvalRequest.SetBodyAsJson(approvalInput);

            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var approvalResponse = await this.secretAccessRequest.Run(approvalRequest, "sunshine", claimsPrincipal, this.logger);
            var approvalOkResult = approvalResponse as TestHttpResponseData;

            Assert.That(approvalOkResult, Is.Not.Null);
            Assert.That(approvalOkResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var approvalResult = approvalOkResult?.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.That(approvalResult?.Status, Is.EqualTo("ok"));
            Assert.That(approvalResult?.Error, Is.Null);
            Assert.That(approvalResult?.Result, Is.EqualTo("ok"));

            // [THEN] The second user has not acquired any access to the secret.
            Assert.That(await this.permissionsManager.IsAuthorizedAsync(SubjectType.User, "second@test.test", "sunshine", PermissionType.Read), Is.False);
            Assert.That(await this.permissionsManager.IsAuthorizedAsync(SubjectType.User, "second@test.test", "sunshine", PermissionType.Write), Is.False);
            Assert.That(await this.permissionsManager.IsAuthorizedAsync(SubjectType.User, "second@test.test", "sunshine", PermissionType.GrantAccess), Is.False);
            Assert.That(await this.permissionsManager.IsAuthorizedAsync(SubjectType.User, "second@test.test", "sunshine", PermissionType.RevokeAccess), Is.False);

            var permissions = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("sunshine") && p.SubjectId.Equals("second@test.test"))
                .ToListAsync();

            Assert.That(permissions.Count, Is.EqualTo(0));
        }

        private async Task<List<AccessRequestOutput>> ListRequests(CaseSensitiveClaimsIdentity identity)
        {
            var requestForRequests = TestFactory.CreateHttpRequestData("get");
            var claimsPrincipal = new ClaimsPrincipal(identity);
            var listRequestsResponse = await this.secretAccessRequest.RunList(requestForRequests, claimsPrincipal, this.logger);
            var listResult = listRequestsResponse as TestHttpResponseData;

            Assert.That(listResult, Is.Not.Null);
            Assert.That(listResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = listResult?.ReadBodyAsJson<BaseResponseObject<List<AccessRequestOutput>>>();
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            var requests = responseResult?.Result ?? throw new AssertionException("List of requests is null.");
            return requests;
        }

        private async Task CreateSecret(CaseSensitiveClaimsIdentity identity, string secretName)
        {
            var claimsPrincipal = new ClaimsPrincipal(identity);
            var request = TestFactory.CreateHttpRequestData("post");
            var creationInput = new MetadataCreationInput()
            {
                ExpirationSettings = new ExpirationSettingsInput()
                {
                    ScheduleExpiration = false,
                    ExpireAt = DateTimeProvider.UtcNow + TimeSpan.FromDays(180),
                    ExpireOnIdleTime = false,
                    IdleTimeToExpire = TimeSpan.FromDays(180)
                }
            };

            request.SetBodyAsJson(creationInput);
            var response = await this.secretMeta.Run(request, secretName, claimsPrincipal, this.logger);
            var okObjectResult = response as TestHttpResponseData;

            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        private async Task RequestAccess(CaseSensitiveClaimsIdentity identity, string secretName, string subjectName, bool read, bool write, bool grantAccess, bool revokeAccess)
        {
            var accessRequest = TestFactory.CreateHttpRequestData("post");
            var accessInput = new SubjectPermissionsInput()
            {
                SubjectName = subjectName,
                SubjectId = subjectName,
                CanRead = read,
                CanWrite = write,
                CanGrantAccess = grantAccess,
                CanRevokeAccess = revokeAccess
            };

            accessRequest.SetBodyAsJson(accessInput);

            var claimsPrincipal = new ClaimsPrincipal(identity);
            var accessResponse = await this.secretAccessRequest.Run(accessRequest, secretName, claimsPrincipal, this.logger);

            var okObjectAccessResult = accessResponse as TestHttpResponseData;

            Assert.That(okObjectAccessResult, Is.Not.Null);
            Assert.That(okObjectAccessResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectAccessResult?.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);
            Assert.That(responseResult?.Result, Is.EqualTo("ok"));
        }

        private async Task GrantAccess(CaseSensitiveClaimsIdentity identity, string secretName, string subjectName, bool read, bool write, bool grantAccess, bool revokeAccess)
            => await this.InternalAccessRequest(identity, "post", secretName, subjectName, read, write, grantAccess, revokeAccess);

        private async Task RevokeAccess(CaseSensitiveClaimsIdentity identity, string secretName, string subjectName, bool read, bool write, bool grantAccess, bool revokeAccess)
            => await this.InternalAccessRequest(identity, "delete", secretName, subjectName, read, write, grantAccess, revokeAccess);

        private async Task InternalAccessRequest(CaseSensitiveClaimsIdentity identity, string method, string secretName, string subjectName, bool read, bool write, bool grantAccess, bool revokeAccess)
        {
            var accessRequest = TestFactory.CreateHttpRequestData(method);
            var accessInput = new List<SubjectPermissionsInput>()
            {
                new SubjectPermissionsInput()
                {
                    SubjectName = subjectName,
                    CanRead = read,
                    CanWrite = write,
                    CanGrantAccess = grantAccess,
                    CanRevokeAccess = revokeAccess,
                }
            };

            accessRequest.SetBodyAsJson(accessInput);

            var claimsPrincipal = new ClaimsPrincipal(identity);
            var accessResponse = await this.secretAccess.Run(accessRequest, secretName, claimsPrincipal, this.logger);

            var okObjectAccessResult = accessResponse as TestHttpResponseData;

            Assert.That(okObjectAccessResult, Is.Not.Null);
            Assert.That(okObjectAccessResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectAccessResult?.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);
            Assert.That(responseResult?.Result, Is.EqualTo("ok"));
        }
    }
}
