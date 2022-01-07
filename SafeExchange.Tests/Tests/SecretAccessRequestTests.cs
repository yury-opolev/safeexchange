/// <summary>
/// SecretAccessTests
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Security.Claims;
    using System.Text.Json;
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

        private ITokenHelper tokenHelper;

        private TestGraphDataProvider graphDataProvider;

        private GlobalFilters globalFilters;

        private IBlobHelper blobHelper;

        private IPurger purger;

        private IPermissionsManager permissionsManager;

        private ClaimsIdentity firstIdentity;
        private ClaimsIdentity secondIdentity;
        private ClaimsIdentity thirdIdentity;

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            var builder = new ConfigurationBuilder().AddUserSecrets<SecretAccessRequestTests>();
            var secretConfiguration = builder.Build();

            this.logger = TestFactory.CreateLogger();

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

            this.tokenHelper = new TestTokenHelper();
            this.graphDataProvider = new TestGraphDataProvider();
            this.globalFilters = new GlobalFilters(this.testConfiguration, this.tokenHelper, this.graphDataProvider, TestFactory.CreateLogger<GlobalFilters>());

            this.blobHelper = new TestBlobHelper();
            this.purger = new PurgeManager(this.testConfiguration, this.blobHelper, TestFactory.CreateLogger<PurgeManager>());

            this.permissionsManager = new PermissionsManager(this.testConfiguration, this.dbContext, TestFactory.CreateLogger<PermissionsManager>());

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

            this.thirdIdentity = new ClaimsIdentity(new List<Claim>()
                {
                    new Claim("upn", "third@test.test"),
                    new Claim("displayname", "Third User"),
                    new Claim("oid", "00000000-0000-0000-0000-000000000003"),
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
            DateTimeProvider.SpecifiedDateTime = DateTime.UtcNow;

            this.secretMeta = new SafeExchangeSecretMeta(
                this.testConfiguration, this.dbContext, this.tokenHelper,
                this.globalFilters, this.purger, this.permissionsManager);

            this.secretAccess = new SafeExchangeAccess(
                this.dbContext, this.tokenHelper,
                this.globalFilters, this.purger, this.permissionsManager);

            this.secretAccessRequest = new SafeExchangeAccessRequest(
                this.testConfiguration, this.dbContext, this.globalFilters,
                this.tokenHelper, this.purger, this.permissionsManager);
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
            this.dbContext.NotificationSubscriptions.RemoveRange(this.dbContext.NotificationSubscriptions.ToList());
            this.dbContext.GroupDictionary.RemoveRange(this.dbContext.GroupDictionary.ToList());
            this.dbContext.SaveChanges();
        }

        [Test]
        public async Task CannotRequestAccessForDifferentUser()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [WHEN] A second user has requested read access to the secret with specified third user.
            var accessRequest = TestFactory.CreateHttpRequest("post");
            var accessInput = new SubjectPermissionsInput()
            {
                SubjectName = "third@test.test",
                CanRead = true,
                CanWrite = false,
                CanGrantAccess = false,
                CanRevokeAccess = false
            };

            accessRequest.Body = new StringContent(DefaultJsonSerializer.Serialize(accessInput)).ReadAsStream();

            var claimsPrincipal = new ClaimsPrincipal(this.secondIdentity);
            var response = await this.secretAccessRequest.Run(accessRequest, "sunshine", claimsPrincipal, this.logger);

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error, but requester is the second user.
            var okObjectResult = response as OkObjectResult;
            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            var responseResult = okObjectResult?.Value as BaseResponseObject<string>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);
            Assert.AreEqual("ok", responseResult?.Result);

            var accessRequests = await this.dbContext.AccessRequests.Where(ar => ar.ObjectName.Equals("sunshine")).ToListAsync();
            Assert.AreEqual(1, accessRequests.Count);
            Assert.AreEqual("second@test.test", accessRequests.First().SubjectName);
            Assert.AreEqual(PermissionType.Read, accessRequests.First().Permission);
            Assert.AreEqual(RequestStatus.InProgress, accessRequests.First().Status);
        }

        [Test]
        public async Task ListRequestsSunshine()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [WHEN] A second user has requested read/write access to the secret.
            await this.RequestAccess(this.secondIdentity, "sunshine", "second@test.test", true, true, false, false);

            var accessRequests = await this.dbContext.AccessRequests.Where(ar => ar.ObjectName.Equals("sunshine")).ToListAsync();

            Assert.AreEqual(1, accessRequests.Count);
            Assert.AreEqual("second@test.test", accessRequests.First().SubjectName);
            Assert.AreEqual(PermissionType.Read | PermissionType.Write, accessRequests.First().Permission);
            Assert.AreEqual(RequestStatus.InProgress, accessRequests.First().Status);

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromMinutes(1);

            // [THEN] Both first and second user can see the request is requests list.
            var requests = await this.ListRequests(this.firstIdentity);
            Assert.AreEqual(1, requests.Count);

            var requestId = requests.First().Id;
            Assert.AreEqual("second@test.test", requests.First().SubjectName);
            Assert.AreEqual("sunshine", requests.First().ObjectName);

            Assert.IsTrue(requests.First().CanRead);
            Assert.IsTrue(requests.First().CanWrite);
            Assert.IsFalse(requests.First().CanGrantAccess);
            Assert.IsFalse(requests.First().CanRevokeAccess);

            Assert.AreEqual(DateTimeProvider.SpecifiedDateTime - TimeSpan.FromMinutes(1), requests.First().RequestedAt);

            requests = await this.ListRequests(this.secondIdentity);
            Assert.AreEqual(1, requests.Count);

            requestId = requests.First().Id;
            Assert.AreEqual("second@test.test", requests.First().SubjectName);
            Assert.AreEqual("sunshine", requests.First().ObjectName);

            Assert.IsTrue(requests.First().CanRead);
            Assert.IsTrue(requests.First().CanWrite);
            Assert.IsFalse(requests.First().CanGrantAccess);
            Assert.IsFalse(requests.First().CanRevokeAccess);

            Assert.AreEqual(DateTimeProvider.SpecifiedDateTime - TimeSpan.FromMinutes(1), requests.First().RequestedAt);
        }

        [Test]
        public async Task GrantReadAccess()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [GIVEN] A second user has requested read access to the secret.
            await this.RequestAccess(this.secondIdentity, "sunshine", "second@test.test", true, false, false, false);

            var accessRequests = await this.dbContext.AccessRequests.Where(ar => ar.ObjectName.Equals("sunshine")).ToListAsync();

            Assert.AreEqual(1, accessRequests.Count);
            Assert.AreEqual("second@test.test", accessRequests.First().SubjectName);
            Assert.AreEqual(PermissionType.Read, accessRequests.First().Permission);
            Assert.AreEqual(RequestStatus.InProgress, accessRequests.First().Status);

            // [WHEN] The first user has approved the request.
            var requests = await this.ListRequests(this.firstIdentity);
            Assert.AreEqual(1, requests.Count);
            var requestId = requests.First().Id;

            var approvalRequest = TestFactory.CreateHttpRequest("patch");
            var approvalInput = new AccessRequestUpdateInput()
            {
                RequestId = requestId,
                Approve = true
            };

            approvalRequest.Body = new StringContent(DefaultJsonSerializer.Serialize(approvalInput)).ReadAsStream();

            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var approvalResponse = await this.secretAccessRequest.Run(approvalRequest, "sunshine", claimsPrincipal, this.logger);
            var approvalOkResult = approvalResponse as OkObjectResult;

            Assert.IsNotNull(approvalOkResult);
            Assert.AreEqual(200, approvalOkResult?.StatusCode);

            var approvalResult = approvalOkResult?.Value as BaseResponseObject<string>;
            Assert.AreEqual("ok", approvalResult?.Status);
            Assert.IsNull(approvalResult?.Error);
            Assert.AreEqual("ok", approvalResult?.Result);

            // [THEN] The second user has acquired read access to the secret.
            Assert.IsTrue(await this.permissionsManager.IsAuthorizedAsync("second@test.test", "sunshine", PermissionType.Read));
            Assert.IsFalse(await this.permissionsManager.IsAuthorizedAsync("second@test.test", "sunshine", PermissionType.Write));
            Assert.IsFalse(await this.permissionsManager.IsAuthorizedAsync("second@test.test", "sunshine", PermissionType.GrantAccess));
            Assert.IsFalse(await this.permissionsManager.IsAuthorizedAsync("second@test.test", "sunshine", PermissionType.RevokeAccess));

            var permissions = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("sunshine") && p.SubjectName.Equals("second@test.test"))
                .ToListAsync();

            Assert.AreEqual(1, permissions.Count);
            
            Assert.IsTrue(permissions.First().CanRead);
            Assert.IsFalse(permissions.First().CanWrite);
            Assert.IsFalse(permissions.First().CanGrantAccess);
            Assert.IsFalse(permissions.First().CanRevokeAccess);
        }

        [Test]
        public async Task CancelAccessRequest()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [GIVEN] A second user has requested read access to the secret.
            await this.RequestAccess(this.secondIdentity, "sunshine", "second@test.test", true, false, false, false);

            var accessRequests = await this.dbContext.AccessRequests.Where(ar => ar.ObjectName.Equals("sunshine")).ToListAsync();

            Assert.AreEqual(1, accessRequests.Count);
            Assert.AreEqual("second@test.test", accessRequests.First().SubjectName);
            Assert.AreEqual(PermissionType.Read, accessRequests.First().Permission);
            Assert.AreEqual(RequestStatus.InProgress, accessRequests.First().Status);

            // [WHEN] The second user has cancelled the request.
            var requests = await this.ListRequests(this.secondIdentity);
            Assert.AreEqual(1, requests.Count);
            var requestId = requests.First().Id;

            var deletionRequest = TestFactory.CreateHttpRequest("delete");
            var deletionInput = new AccessRequestDeletionInput()
            {
                RequestId = requestId
            };

            deletionRequest.Body = new StringContent(DefaultJsonSerializer.Serialize(deletionInput)).ReadAsStream();

            var claimsPrincipal = new ClaimsPrincipal(this.secondIdentity);
            var deletionResponse = await this.secretAccessRequest.Run(deletionRequest, "sunshine", claimsPrincipal, this.logger);
            var deletionOkResult = deletionResponse as OkObjectResult;

            Assert.IsNotNull(deletionOkResult);
            Assert.AreEqual(200, deletionOkResult?.StatusCode);

            var deletionResult = deletionOkResult?.Value as BaseResponseObject<string>;
            Assert.AreEqual("ok", deletionResult?.Status);
            Assert.IsNull(deletionResult?.Error);
            Assert.AreEqual("ok", deletionResult?.Result);

            // [THEN] The access request is deleted from database, the second user does not have permissions for the secret.
            Assert.IsFalse(await this.permissionsManager.IsAuthorizedAsync("second@test.test", "sunshine", PermissionType.Read));
            Assert.IsFalse(await this.permissionsManager.IsAuthorizedAsync("second@test.test", "sunshine", PermissionType.Write));
            Assert.IsFalse(await this.permissionsManager.IsAuthorizedAsync("second@test.test", "sunshine", PermissionType.GrantAccess));
            Assert.IsFalse(await this.permissionsManager.IsAuthorizedAsync("second@test.test", "sunshine", PermissionType.RevokeAccess));

            accessRequests = await this.dbContext.AccessRequests.Where(ar => ar.ObjectName.Equals("sunshine")).ToListAsync();
            Assert.AreEqual(0, accessRequests.Count);
        }

        [Test]
        public async Task CannotCancelOtherAccessRequest()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [GIVEN] A second user has requested read access to the secret.
            await this.RequestAccess(this.secondIdentity, "sunshine", "second@test.test", true, false, false, false);

            var accessRequests = await this.dbContext.AccessRequests.Where(ar => ar.ObjectName.Equals("sunshine")).ToListAsync();

            Assert.AreEqual(1, accessRequests.Count);
            Assert.AreEqual("second@test.test", accessRequests.First().SubjectName);
            Assert.AreEqual(PermissionType.Read, accessRequests.First().Permission);
            Assert.AreEqual(RequestStatus.InProgress, accessRequests.First().Status);

            // [WHEN] The first user is trying to cancel the request.
            var requests = await this.ListRequests(this.firstIdentity);
            Assert.AreEqual(1, requests.Count);
            var requestId = requests.First().Id;

            var deletionRequest = TestFactory.CreateHttpRequest("delete");
            var deletionInput = new AccessRequestDeletionInput()
            {
                RequestId = requestId
            };

            deletionRequest.Body = new StringContent(DefaultJsonSerializer.Serialize(deletionInput)).ReadAsStream();

            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var deletionResponse = await this.secretAccessRequest.Run(deletionRequest, "sunshine", claimsPrincipal, this.logger);
            var deletionOkResult = deletionResponse as ObjectResult;

            Assert.IsNotNull(deletionOkResult);
            Assert.AreEqual(401, deletionOkResult?.StatusCode);

            var deletionResult = deletionOkResult?.Value as BaseResponseObject<object>;
            Assert.AreEqual("unauthorized", deletionResult?.Status);
            Assert.IsNotNull(deletionResult?.Error);
            Assert.IsNull(deletionResult?.Result);

            accessRequests = await this.dbContext.AccessRequests.Where(ar => ar.ObjectName.Equals("sunshine")).ToListAsync();
            Assert.AreEqual(1, accessRequests.Count);
        }

        [Test]
        public async Task DenyReadAccess()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [GIVEN] A second user has requested read access to the secret.
            await this.RequestAccess(this.secondIdentity, "sunshine", "second@test.test", true, false, false, false);

            var accessRequests = await this.dbContext.AccessRequests.Where(ar => ar.ObjectName.Equals("sunshine")).ToListAsync();

            Assert.AreEqual(1, accessRequests.Count);
            Assert.AreEqual("second@test.test", accessRequests.First().SubjectName);
            Assert.AreEqual(PermissionType.Read, accessRequests.First().Permission);
            Assert.AreEqual(RequestStatus.InProgress, accessRequests.First().Status);

            // [WHEN] The first user has rejected the request.
            var requests = await this.ListRequests(this.firstIdentity);
            Assert.AreEqual(1, requests.Count);
            var requestId = requests.First().Id;

            var approvalRequest = TestFactory.CreateHttpRequest("patch");
            var approvalInput = new AccessRequestUpdateInput()
            {
                RequestId = requestId,
                Approve = false
            };

            approvalRequest.Body = new StringContent(DefaultJsonSerializer.Serialize(approvalInput)).ReadAsStream();

            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var approvalResponse = await this.secretAccessRequest.Run(approvalRequest, "sunshine", claimsPrincipal, this.logger);
            var approvalOkResult = approvalResponse as OkObjectResult;

            Assert.IsNotNull(approvalOkResult);
            Assert.AreEqual(200, approvalOkResult?.StatusCode);

            var approvalResult = approvalOkResult?.Value as BaseResponseObject<string>;
            Assert.AreEqual("ok", approvalResult?.Status);
            Assert.IsNull(approvalResult?.Error);
            Assert.AreEqual("ok", approvalResult?.Result);

            // [THEN] The second user has acquired read access to the secret.
            Assert.IsFalse(await this.permissionsManager.IsAuthorizedAsync("second@test.test", "sunshine", PermissionType.Read));
            Assert.IsFalse(await this.permissionsManager.IsAuthorizedAsync("second@test.test", "sunshine", PermissionType.Write));
            Assert.IsFalse(await this.permissionsManager.IsAuthorizedAsync("second@test.test", "sunshine", PermissionType.GrantAccess));
            Assert.IsFalse(await this.permissionsManager.IsAuthorizedAsync("second@test.test", "sunshine", PermissionType.RevokeAccess));

            var permissions = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("sunshine") && p.SubjectName.Equals("second@test.test"))
                .ToListAsync();

            Assert.AreEqual(0, permissions.Count);
        }

        private async Task<List<AccessRequestOutput>> ListRequests(ClaimsIdentity identity)
        {
            var requestForRequests = TestFactory.CreateHttpRequest("get");
            var claimsPrincipal = new ClaimsPrincipal(identity);
            var listRequestsResponse = await this.secretAccessRequest.RunList(requestForRequests, claimsPrincipal, this.logger);
            var listResult = listRequestsResponse as OkObjectResult;

            Assert.IsNotNull(listResult);
            Assert.AreEqual(200, listResult?.StatusCode);

            var responseResult = listResult?.Value as BaseResponseObject<List<AccessRequestOutput>>;
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);

            var requests = responseResult?.Result ?? throw new AssertionException("List of requests is null.");
            return requests;
        }

        private async Task CreateSecret(ClaimsIdentity identity, string secretName)
        {
            var claimsPrincipal = new ClaimsPrincipal(identity);
            var request = TestFactory.CreateHttpRequest("post");
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

            request.Body = new StringContent(DefaultJsonSerializer.Serialize(creationInput)).ReadAsStream();
            var response = await this.secretMeta.Run(request, secretName, claimsPrincipal, this.logger);
            var okObjectResult = response as OkObjectResult;

            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(200, okObjectResult?.StatusCode);
        }

        private async Task RequestAccess(ClaimsIdentity identity, string secretName, string subjectName, bool read, bool write, bool grantAccess, bool revokeAccess)
        {
            var accessRequest = TestFactory.CreateHttpRequest("post");
            var accessInput = new SubjectPermissionsInput()
            {
                SubjectName = subjectName,
                CanRead = read,
                CanWrite = write,
                CanGrantAccess = grantAccess,
                CanRevokeAccess = revokeAccess
            };

            accessRequest.Body = new StringContent(DefaultJsonSerializer.Serialize(accessInput)).ReadAsStream();

            var claimsPrincipal = new ClaimsPrincipal(identity);
            var accessResponse = await this.secretAccessRequest.Run(accessRequest, secretName, claimsPrincipal, this.logger);

            var okObjectAccessResult = accessResponse as OkObjectResult;

            Assert.IsNotNull(okObjectAccessResult);
            Assert.AreEqual(200, okObjectAccessResult?.StatusCode);

            var responseResult = okObjectAccessResult?.Value as BaseResponseObject<string>;
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);
            Assert.AreEqual("ok", responseResult?.Result);
        }

        private async Task GrantAccess(ClaimsIdentity identity, string secretName, string subjectName, bool read, bool write, bool grantAccess, bool revokeAccess)
            => await this.InternalAccessRequest(identity, "post", secretName, subjectName, read, write, grantAccess, revokeAccess);

        private async Task RevokeAccess(ClaimsIdentity identity, string secretName, string subjectName, bool read, bool write, bool grantAccess, bool revokeAccess)
            => await this.InternalAccessRequest(identity, "delete", secretName, subjectName, read, write, grantAccess, revokeAccess);

        private async Task InternalAccessRequest(ClaimsIdentity identity, string method, string secretName, string subjectName, bool read, bool write, bool grantAccess, bool revokeAccess)
        {
            var accessRequest = TestFactory.CreateHttpRequest(method);
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

            accessRequest.Body = new StringContent(DefaultJsonSerializer.Serialize(accessInput)).ReadAsStream();

            var claimsPrincipal = new ClaimsPrincipal(identity);
            var accessResponse = await this.secretAccess.Run(accessRequest, secretName, claimsPrincipal, this.logger);

            var okObjectAccessResult = accessResponse as OkObjectResult;

            Assert.IsNotNull(okObjectAccessResult);
            Assert.AreEqual(200, okObjectAccessResult?.StatusCode);

            var responseResult = okObjectAccessResult?.Value as BaseResponseObject<string>;
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);
            Assert.AreEqual("ok", responseResult?.Result);
        }
    }
}
