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
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
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
    public class SecretAccessTests
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

        private ClaimsIdentity firstIdentity;
        private ClaimsIdentity secondIdentity;
        private ClaimsIdentity thirdIdentity;

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            var builder = new ConfigurationBuilder().AddUserSecrets<SecretAccessTests>();
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
                .UseCosmos(secretConfiguration.GetConnectionString("CosmosDb"), databaseName: $"{nameof(SecretAccessTests)}Database")
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
        public async Task TryToReadPermissionsWithNoAccess()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [WHEN] A second user has made a request to get secret access list.
            var accessRequest = TestFactory.CreateHttpRequestData("get");
            var secondClaimsPrincipal = new ClaimsPrincipal(this.secondIdentity);
            var accessResponse = await this.secretAccess.Run(accessRequest, "sunshine", secondClaimsPrincipal, this.logger);

            // [THEN] UnauthorizedObjectResult is returned with Status = 'unauthorized', null Result and non-null Error.
            var forbiddenObjectResult = accessResponse as TestHttpResponseData;

            Assert.IsNotNull(forbiddenObjectResult);
            Assert.AreEqual(HttpStatusCode.Forbidden, forbiddenObjectResult?.StatusCode);

            var responseResult = forbiddenObjectResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.AreEqual("forbidden", responseResult?.Status);
            Assert.IsNull(responseResult?.Result);
            Assert.IsNotNull(responseResult?.Error);
        }

        [Test]
        public async Task GrantReadAccess()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [GIVEN] The user has granted read access for the secret to another user.
            await this.GrantAccess(this.firstIdentity, "sunshine", "second@test.test", true, false, false, false);

            // [WHEN] A second user has made a request to get secret access list.
            var accessRequest = TestFactory.CreateHttpRequestData("get");
            var secondClaimsPrincipal = new ClaimsPrincipal(this.secondIdentity);
            var accessResponse = await this.secretAccess.Run(accessRequest, "sunshine", secondClaimsPrincipal, this.logger);

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result with a list of permissions and null Error.
            var okObjectResult = accessResponse as TestHttpResponseData;

            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(HttpStatusCode.OK, okObjectResult?.StatusCode);

            var responseResult = okObjectResult?.ReadBodyAsJson<BaseResponseObject<List<SubjectPermissionsOutput>>>();
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);

            var permissions = responseResult?.Result;
            if (permissions == null)
            {
                throw new AssertionException("Returned permissions are null.");
            }

            Assert.AreEqual(2, permissions.Count);
            var firstUserPermissions = permissions.First(p => p.ObjectName.Equals("sunshine") && p.SubjectName.Equals("first@test.test"));
            Assert.IsTrue(firstUserPermissions.CanRead);
            Assert.IsTrue(firstUserPermissions.CanWrite);
            Assert.IsTrue(firstUserPermissions.CanGrantAccess);
            Assert.IsTrue(firstUserPermissions.CanRevokeAccess);

            var secondUserPermissions = permissions.First(p => p.ObjectName.Equals("sunshine") && p.SubjectName.Equals("second@test.test"));
            Assert.IsTrue(secondUserPermissions.CanRead);
            Assert.IsFalse(secondUserPermissions.CanWrite);
            Assert.IsFalse(secondUserPermissions.CanGrantAccess);
            Assert.IsFalse(secondUserPermissions.CanRevokeAccess);
        }

        [Test]
        public async Task GrantAllAccessExceptRevoke()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [GIVEN] The user has granted full access except 'revoke permissions' for the secret to another user.
            await this.GrantAccess(this.firstIdentity, "sunshine", "second@test.test", true, true, true, false);

            // [WHEN] A second user has made a request to get secret access list.
            var accessRequest = TestFactory.CreateHttpRequestData("get");
            var secondClaimsPrincipal = new ClaimsPrincipal(this.secondIdentity);
            var accessResponse = await this.secretAccess.Run(accessRequest, "sunshine", secondClaimsPrincipal, this.logger);

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result with a list of permissions and null Error.
            var okObjectResult = accessResponse as TestHttpResponseData;

            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(HttpStatusCode.OK, okObjectResult?.StatusCode);

            var responseResult = okObjectResult?.ReadBodyAsJson<BaseResponseObject<List<SubjectPermissionsOutput>>>();
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);

            var permissions = responseResult?.Result;
            if (permissions == null)
            {
                throw new AssertionException("Returned permissions are null.");
            }

            Assert.AreEqual(2, permissions.Count);
            var firstUserPermissions = permissions.First(p => p.ObjectName.Equals("sunshine") && p.SubjectName.Equals("first@test.test"));
            Assert.IsTrue(firstUserPermissions.CanRead);
            Assert.IsTrue(firstUserPermissions.CanWrite);
            Assert.IsTrue(firstUserPermissions.CanGrantAccess);
            Assert.IsTrue(firstUserPermissions.CanRevokeAccess);

            var secondUserPermissions = permissions.First(p => p.ObjectName.Equals("sunshine") && p.SubjectName.Equals("second@test.test"));
            Assert.IsTrue(secondUserPermissions.CanRead);
            Assert.IsTrue(secondUserPermissions.CanWrite);
            Assert.IsTrue(secondUserPermissions.CanGrantAccess);
            Assert.IsFalse(secondUserPermissions.CanRevokeAccess);
        }

        [Test]
        public async Task CannotGrantRevokeAccessWithoutHavingIt()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [GIVEN] The user has granted full access except 'RevokeAccess' permission for the secret to a second user.
            await this.GrantAccess(this.firstIdentity, "sunshine", "second@test.test", true, true, true, false);

            // [WHEN] The second user is granting full access to a third user.
            var accessRequest = TestFactory.CreateHttpRequestData("post");
            var accessInput = new List<SubjectPermissionsInput>()
            {
                new SubjectPermissionsInput()
                {
                    SubjectName = "third@test.test",
                    CanRead = true,
                    CanWrite = true,
                    CanGrantAccess = true,
                    CanRevokeAccess = true
                }
            };

            accessRequest.SetBodyAsJson(accessInput);

            var claimsPrincipal = new ClaimsPrincipal(this.secondIdentity);
            var accessResponse = await this.secretAccess.Run(accessRequest, "sunshine", claimsPrincipal, this.logger);

            var okObjectAccessResult = accessResponse as TestHttpResponseData;

            Assert.IsNotNull(okObjectAccessResult);
            Assert.AreEqual(HttpStatusCode.OK, okObjectAccessResult?.StatusCode);

            // [THEN] OkObjectResult is returned with Status = 'ok', but third user has only 'Read, Write, GrantAccess' permissions.
            var okObjectResult = accessResponse as TestHttpResponseData;

            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(HttpStatusCode.OK, okObjectResult?.StatusCode);

            var responseResult = okObjectResult?.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);
            Assert.AreEqual("ok", responseResult?.Result);

            var dbPermissions = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("sunshine") && p.SubjectName.Equals("third@test.test"))
                .ToListAsync();

            var thirdUserPermissions = dbPermissions.First();

            Assert.IsTrue(thirdUserPermissions.CanRead);
            Assert.IsTrue(thirdUserPermissions.CanWrite);
            Assert.IsTrue(thirdUserPermissions.CanGrantAccess);
            Assert.IsFalse(thirdUserPermissions.CanRevokeAccess);
        }

        [Test]
        public async Task CanGrantFullAccess()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [GIVEN] The user has granted full access for the secret to a second user.
            await this.GrantAccess(this.firstIdentity, "sunshine", "second@test.test", true, true, true, true);

            // [WHEN] The second user is granting full access to a third user.
            var accessRequest = TestFactory.CreateHttpRequestData("post");
            var accessInput = new List<SubjectPermissionsInput>()
            {
                new SubjectPermissionsInput()
                {
                    SubjectName = "third@test.test",
                    CanRead = true,
                    CanWrite = true,
                    CanGrantAccess = true,
                    CanRevokeAccess = true
                }
            };

            accessRequest.SetBodyAsJson(accessInput);

            var claimsPrincipal = new ClaimsPrincipal(this.secondIdentity);
            var accessResponse = await this.secretAccess.Run(accessRequest, "sunshine", claimsPrincipal, this.logger);

            var okObjectAccessResult = accessResponse as TestHttpResponseData;

            Assert.IsNotNull(okObjectAccessResult);
            Assert.AreEqual(HttpStatusCode.OK, okObjectAccessResult?.StatusCode);

            // [THEN] OkObjectResult is returned with Status = 'ok', third user has full permissions.
            var okObjectResult = accessResponse as TestHttpResponseData;

            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(HttpStatusCode.OK, okObjectResult?.StatusCode);

            var responseResult = okObjectResult?.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);
            Assert.AreEqual("ok", responseResult?.Result);

            var dbPermissions = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("sunshine") && p.SubjectName.Equals("third@test.test"))
                .ToListAsync();

            var thirdUserPermissions = dbPermissions.First();

            Assert.IsTrue(thirdUserPermissions.CanRead);
            Assert.IsTrue(thirdUserPermissions.CanWrite);
            Assert.IsTrue(thirdUserPermissions.CanGrantAccess);
            Assert.IsTrue(thirdUserPermissions.CanRevokeAccess);
        }

        [Test]
        public async Task GrantAndRevokeAccess()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [GIVEN] The user has granted read/write access for the secret to a second user.
            await this.GrantAccess(this.firstIdentity, "sunshine", "second@test.test", true, true, false, false);

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromMinutes(1);

            // [GIVEN] The second user can read the secret metadata.
            var getRequest = TestFactory.CreateHttpRequestData("get");
            var claimsPrincipal = new ClaimsPrincipal(this.secondIdentity);
            var getResponse = await this.secretMeta.Run(getRequest, "sunshine", claimsPrincipal, this.logger);

            var okObjectAccessResult = getResponse as TestHttpResponseData;

            Assert.IsNotNull(okObjectAccessResult);
            Assert.AreEqual(HttpStatusCode.OK, okObjectAccessResult?.StatusCode);

            var getResponseResult = okObjectAccessResult?.ReadBodyAsJson<BaseResponseObject<ObjectMetadataOutput>>();
            var mainContent = getResponseResult?.Result?.Content.FirstOrDefault();
            if (mainContent == null)
            {
                throw new AssertionException("Main content is null.");
            }

            Assert.IsTrue(mainContent.IsMain);
            Assert.IsFalse(string.IsNullOrEmpty(mainContent.ContentName));

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromMinutes(1);

            // [WHEN] The user has revoked all access for the secret from the second user.
            await this.RevokeAccess(this.firstIdentity, "sunshine", "second@test.test", true, true, true, true);

            // [THEN] The database does not contain any records for second user to access the secret.
            var dbPermissions = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("sunshine") && p.SubjectName.Equals("second@test.test"))
                .ToListAsync();

            Assert.IsFalse(dbPermissions.Any());
        }

        [Test]
        public async Task GrantAndRevokeAccessPartially()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [GIVEN] The user has granted read/write access for the secret to a second user.
            await this.GrantAccess(this.firstIdentity, "sunshine", "second@test.test", true, true, false, false);

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromMinutes(1);

            // [GIVEN] The second user can read the secret metadata.
            var getRequest = TestFactory.CreateHttpRequestData("get");
            var claimsPrincipal = new ClaimsPrincipal(this.secondIdentity);
            var getResponse = await this.secretMeta.Run(getRequest, "sunshine", claimsPrincipal, this.logger);

            var okObjectAccessResult = getResponse as TestHttpResponseData;

            Assert.IsNotNull(okObjectAccessResult);
            Assert.AreEqual(HttpStatusCode.OK, okObjectAccessResult?.StatusCode);

            var getResponseResult = okObjectAccessResult?.ReadBodyAsJson<BaseResponseObject<ObjectMetadataOutput>>();
            var mainContent = getResponseResult?.Result?.Content.FirstOrDefault();
            if (mainContent == null)
            {
                throw new AssertionException("Main content is null.");
            }

            Assert.IsTrue(mainContent.IsMain);
            Assert.IsFalse(string.IsNullOrEmpty(mainContent.ContentName));

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromMinutes(1);

            // [WHEN] The user has revoked write access for the secret from the second user.
            await this.RevokeAccess(this.firstIdentity, "sunshine", "second@test.test", false, true, false, false);

            // [THEN] The database contains 1 record for second user to access the secret, only for read access.
            var dbPermissions = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("sunshine") && p.SubjectName.Equals("second@test.test"))
                .ToListAsync();

            Assert.AreEqual(1, dbPermissions?.Count);

            Assert.IsTrue(dbPermissions?.First().CanRead);
            Assert.IsFalse(dbPermissions?.First().CanWrite);
            Assert.IsFalse(dbPermissions?.First().CanGrantAccess);
            Assert.IsFalse(dbPermissions?.First().CanRevokeAccess);
        }

        [Test]
        public async Task PermissionsCreatedCorrectlyOnSecretCreation()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

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

            // [WHEN] A request is made to create a secret.
            var response = await this.secretMeta.Run(request, "sunshine", claimsPrincipal, this.logger);
            var okObjectResult = response as TestHttpResponseData;

            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(HttpStatusCode.OK, okObjectResult?.StatusCode);

            // [THEN] Database contains persisted permissions only for the initial user to the created secret.
            var permissions = await this.dbContext.Permissions.Where(p => p.SecretName.Equals("sunshine")).ToListAsync();

            Assert.AreEqual(1, permissions.Count);

            Assert.AreEqual("first@test.test", permissions.First().SubjectName);
            Assert.AreEqual("sunshine", permissions.First().SecretName);

            Assert.IsTrue(permissions.First().CanRead);
            Assert.IsTrue(permissions.First().CanWrite);
            Assert.IsTrue(permissions.First().CanGrantAccess);
            Assert.IsTrue(permissions.First().CanRevokeAccess);
        }

        [Test]
        public async Task CreateSecretSunshine()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [WHEN] A request is made to get secret access list.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var accessRequest = TestFactory.CreateHttpRequestData("get");
            var accessResponse = await this.secretAccess.Run(accessRequest, "sunshine", claimsPrincipal, this.logger);
            var okObjectAccessResult = accessResponse as TestHttpResponseData;

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error.
            Assert.IsNotNull(okObjectAccessResult);
            Assert.AreEqual(HttpStatusCode.OK, okObjectAccessResult?.StatusCode);

            var responseResult = okObjectAccessResult?.ReadBodyAsJson<BaseResponseObject<List<SubjectPermissionsOutput>>>();
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);

            var permissions = responseResult?.Result;
            if (permissions == null)
            {
                throw new AssertionException("Permissions list is null.");
            }

            Assert.AreEqual(1, permissions.Count);

            Assert.AreEqual("first@test.test", permissions.First().SubjectName);
            Assert.AreEqual("sunshine", permissions.First().ObjectName);

            Assert.IsTrue(permissions.First().CanRead);
            Assert.IsTrue(permissions.First().CanWrite);
            Assert.IsTrue(permissions.First().CanGrantAccess);
            Assert.IsTrue(permissions.First().CanRevokeAccess);
        }

        private async Task CreateSecret(ClaimsIdentity identity, string secretName)
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

            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(HttpStatusCode.OK, okObjectResult?.StatusCode);
        }

        private async Task GrantAccess(ClaimsIdentity identity, string secretName, string subjectName, bool read, bool write, bool grantAccess, bool revokeAccess)
            => await this.InternalAccessRequest(identity, "post", secretName, subjectName, read, write, grantAccess, revokeAccess);

        private async Task RevokeAccess(ClaimsIdentity identity, string secretName, string subjectName, bool read, bool write, bool grantAccess, bool revokeAccess)
            => await this.InternalAccessRequest(identity, "delete", secretName, subjectName, read, write, grantAccess, revokeAccess);

        private async Task InternalAccessRequest(ClaimsIdentity identity, string method, string secretName, string subjectName, bool read, bool write, bool grantAccess, bool revokeAccess)
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

            Assert.IsNotNull(okObjectAccessResult);
            Assert.AreEqual(HttpStatusCode.OK, okObjectAccessResult?.StatusCode);

            var responseResult = okObjectAccessResult?.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);
            Assert.AreEqual("ok", responseResult?.Result);
        }
    }
}
