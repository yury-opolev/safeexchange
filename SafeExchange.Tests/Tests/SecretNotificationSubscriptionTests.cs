/// <summary>
/// SecretAccessTests
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
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Claims;
    using System.Threading.Tasks;

    [TestFixture]
    public class SecretNotificationSubscriptionTests
    {
        private ILogger logger;

        private SafeExchangeSecretMeta secretMeta;

        private SafeExchangeAccess secretAccess;

        private SafeExchangeAccessRequest secretAccessRequest;

        private SafeExchangeNotificationSubscription secretNotificationSubscription;

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
                    {"Features:UseNotifications", "True"},
                    {"Features:UseGroupsAuthorization", "False"}
                };

            this.testConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(configurationValues)
                .Build();

            var dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(secretConfiguration.GetConnectionString("CosmosDb"), databaseName: $"{nameof(SecretNotificationSubscriptionTests)}Database")
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

            this.secretNotificationSubscription = new SafeExchangeNotificationSubscription(
                this.dbContext, this.tokenHelper, this.globalFilters);
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
        public async Task SubscribeSunshine()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] The user has subscribed to web notifications.
            var request = TestFactory.CreateHttpRequestData("post");
            var creationInput = new NotificationSubscriptionCreationInput()
            {
                Url = "https://someurl",
                P256dh = "somep256dh",
                Auth = "someauth"
            };

            request.SetBodyAsJson(creationInput);
            var response = await this.secretNotificationSubscription.Run(request, claimsPrincipal, this.logger);
            var okObjectResult = response as TestHttpResponseData;

            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            var responseResult = okObjectResult?.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);
            Assert.AreEqual("ok", responseResult?.Result);

            // [THEN] The subscription is saved to the database.
            var existingSubscriptions = await this.dbContext.NotificationSubscriptions.ToListAsync();
            Assert.AreEqual(1, existingSubscriptions.Count);

            var subscription = existingSubscriptions.First();
            Assert.AreEqual("first@test.test", subscription.UserUpn);
            Assert.AreEqual(creationInput.Url, subscription.Url);
            Assert.AreEqual(creationInput.P256dh, subscription.P256dh);
            Assert.AreEqual(creationInput.Auth, subscription.Auth);
        }

        [Test]
        public async Task UnsubscribeSunshine()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [GIVEN] The user has successfully subscribed to web notifications.
            var request = TestFactory.CreateHttpRequestData("post");
            var creationInput = new NotificationSubscriptionCreationInput()
            {
                Url = "https://someurl",
                P256dh = "somep256dh",
                Auth = "someauth"
            };

            request.SetBodyAsJson(creationInput);
            var response = await this.secretNotificationSubscription.Run(request, claimsPrincipal, this.logger);
            var okObjectResult = response as TestHttpResponseData;

            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            // [WHEN] The user is unsubscribing from notifications.
            var unsubscribeRequest = TestFactory.CreateHttpRequestData("delete");
            var deletionInput = new NotificationSubscriptionDeletionInput()
            {
                Url = creationInput.Url
            };

            unsubscribeRequest.SetBodyAsJson(deletionInput);

            var unsubResponse = await this.secretNotificationSubscription.Run(unsubscribeRequest, claimsPrincipal, this.logger);
            var unsubOkObjectResult = unsubResponse as TestHttpResponseData;

            Assert.IsNotNull(unsubOkObjectResult);
            Assert.AreEqual(200, unsubOkObjectResult?.StatusCode);

            var unsubResponseResult = unsubOkObjectResult?.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.AreEqual("ok", unsubResponseResult?.Status);
            Assert.IsNull(unsubResponseResult?.Error);
            Assert.AreEqual("ok", unsubResponseResult?.Result);

            // [THEN] The subscription is removed from the database.
            var existingSubscriptions = await this.dbContext.NotificationSubscriptions.ToListAsync();
            Assert.AreEqual(0, existingSubscriptions.Count);
        }

        [Test]
        public async Task CannotUnsubscribeIfNotExists()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] The user is unsubscribing from inexistent notification.
            var unsubscribeRequest = TestFactory.CreateHttpRequestData("delete");
            var deletionInput = new NotificationSubscriptionDeletionInput()
            {
                Url = "https://notexists"
            };

            unsubscribeRequest.SetBodyAsJson(deletionInput);

            var unsubResponse = await this.secretNotificationSubscription.Run(unsubscribeRequest, claimsPrincipal, this.logger);
            
            // [THEN] NotFound reponse received.
            var notFoundObjectResult = unsubResponse as TestHttpResponseData;
            Assert.IsNotNull(notFoundObjectResult);
            Assert.AreEqual(404, notFoundObjectResult?.StatusCode);

            var responseResult = notFoundObjectResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.AreEqual("not_found", responseResult?.Status);
            Assert.IsNotNull(responseResult?.Error);
            Assert.IsNull(responseResult?.Result);
        }

        private async Task<List<AccessRequestOutput>> ListRequests(ClaimsIdentity identity)
        {
            var requestForRequests = TestFactory.CreateHttpRequestData("get");
            var claimsPrincipal = new ClaimsPrincipal(identity);
            var listRequestsResponse = await this.secretAccessRequest.RunList(requestForRequests, claimsPrincipal, this.logger);
            var listResult = listRequestsResponse as TestHttpResponseData;

            Assert.IsNotNull(listResult);
            Assert.AreEqual(200, listResult?.StatusCode);

            var responseResult = listResult?.ReadBodyAsJson<BaseResponseObject<List<AccessRequestOutput>>>();
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);

            var requests = responseResult?.Result ?? throw new AssertionException("List of requests is null.");
            return requests;
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
            Assert.AreEqual(200, okObjectResult?.StatusCode);
        }

        private async Task RequestAccess(ClaimsIdentity identity, string secretName, string subjectName, bool read, bool write, bool grantAccess, bool revokeAccess)
        {
            var accessRequest = TestFactory.CreateHttpRequestData("post");
            var accessInput = new SubjectPermissionsInput()
            {
                SubjectName = subjectName,
                CanRead = read,
                CanWrite = write,
                CanGrantAccess = grantAccess,
                CanRevokeAccess = revokeAccess
            };

            accessRequest.SetBodyAsJson(accessInput);

            var claimsPrincipal = new ClaimsPrincipal(identity);
            var accessResponse = await this.secretAccessRequest.Run(accessRequest, secretName, claimsPrincipal, this.logger);

            var okObjectAccessResult = accessResponse as TestHttpResponseData;

            Assert.IsNotNull(okObjectAccessResult);
            Assert.AreEqual(200, okObjectAccessResult?.StatusCode);

            var responseResult = okObjectAccessResult?.ReadBodyAsJson<BaseResponseObject<string>>();
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
            Assert.AreEqual(200, okObjectAccessResult?.StatusCode);

            var responseResult = okObjectAccessResult?.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);
            Assert.AreEqual("ok", responseResult?.Result);
        }
    }
}
