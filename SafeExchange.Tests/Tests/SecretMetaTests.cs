/// <summary>
/// SecretMetaTests
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
    public class SecretMetaTests
    {
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

        private CaseSensitiveClaimsIdentity firstIdentity;
        private CaseSensitiveClaimsIdentity secondIdentity;

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            var builder = new ConfigurationBuilder().AddUserSecrets<SecretMetaTests>();
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
                .UseCosmos(secretConfiguration.GetConnectionString("CosmosDb"), databaseName: $"{nameof(SecretMetaTests)}Database")
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
        public async Task GetSecret_NotFound()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request to get secret metadata is made for nonexistent secret.
            var request = TestFactory.CreateHttpRequestData("get");
            var response = await this.secretMeta.Run(request, "dummy", claimsPrincipal, this.logger);
            var notFoundObjectResult = response as TestHttpResponseData;

            // [THEN] NotFoundObjectResult is returned with Status = 'not_found', Error message and null Result
            Assert.That(notFoundObjectResult, Is.Not.Null);
            Assert.That(notFoundObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

            var responseResult = notFoundObjectResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("not_found"));
            Assert.That(responseResult?.Error, Is.Not.Null);
            Assert.That(responseResult?.Result, Is.Null);
        }

        [Test]
        public async Task UpdateSecret_NotFound()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request to get secret metadata is made for nonexistent secret.
            var request = TestFactory.CreateHttpRequestData("patch");
            var response = await this.secretMeta.Run(request, "dummy", claimsPrincipal, this.logger);
            var notFoundObjectResult = response as TestHttpResponseData;

            // [THEN] NotFoundObjectResult is returned with Status = 'not_found', Error message and null Result
            Assert.That(notFoundObjectResult, Is.Not.Null);
            Assert.That(notFoundObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

            var responseResult = notFoundObjectResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("not_found"));
            Assert.That(responseResult?.Error, Is.Not.Null);
            Assert.That(responseResult?.Result, Is.Null);
        }

        [Test]
        public async Task DeleteSecret_NotFound()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request to get secret metadata is made for nonexistent secret.
            var request = TestFactory.CreateHttpRequestData("delete");
            var response = await this.secretMeta.Run(request, "dummy", claimsPrincipal, this.logger);
            var notFoundObjectResult = response as TestHttpResponseData;

            // [THEN] NotFoundObjectResult is returned with Status = 'not_found', Error message and null Result
            Assert.That(notFoundObjectResult, Is.Not.Null);
            Assert.That(notFoundObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

            var responseResult = notFoundObjectResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("not_found"));
            Assert.That(responseResult?.Error, Is.Not.Null);
            Assert.That(responseResult?.Result, Is.Null);
        }

        [Test]
        public async Task CreateSecretWithNoIdProvided()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request to create secret is made, without secret id specified.
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

            var response = await this.secretMeta.Run(request, string.Empty, claimsPrincipal, this.logger);
            var badRequestObjectResult = response as TestHttpResponseData;

            // [THEN] BadRequestObjectResult is returned with Status = 'error', null Result and non-null Error.
            Assert.That(badRequestObjectResult, Is.Not.Null);
            Assert.That(badRequestObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

            var responseResult = badRequestObjectResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("bad_request"));
            Assert.That(responseResult?.Error, Is.Not.Null);
            Assert.That(responseResult?.Result, Is.Null);
        }

        [Test]
        public async Task CreateSecretWithNoDataProvided()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request to create secret is made, without body specified.
            var request = TestFactory.CreateHttpRequestData("post");
            var response = await this.secretMeta.Run(request, "badrequest", claimsPrincipal, this.logger);
            var badRequestObjectResult = response as TestHttpResponseData;

            // [THEN] BadRequestObjectResult is returned with Status = 'error', null Result and non-null Error.
            Assert.That(badRequestObjectResult, Is.Not.Null);
            Assert.That(badRequestObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

            var responseResult = badRequestObjectResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("bad_request"));
            Assert.That(responseResult?.Error, Is.Not.Null);
            Assert.That(responseResult?.Result, Is.Null);
        }

        [Test]
        public async Task UpdateSecretWithNoDataProvided()
        {
            // [GIVEN] A secret with name 'x' exists.
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
            var response = await this.secretMeta.Run(request, "badrequest2", claimsPrincipal, this.logger);
            var okObjectResult = response as TestHttpResponseData;
            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // [WHEN] A request to update secret 'x' is made, without body specified.
            request = TestFactory.CreateHttpRequestData("patch");
            response = await this.secretMeta.Run(request, "badrequest2", claimsPrincipal, this.logger);
            var badRequestObjectResult = response as TestHttpResponseData;

            // [THEN] BadRequestObjectResult is returned with Status = 'error', null Result and non-null Error.
            Assert.That(badRequestObjectResult, Is.Not.Null);
            Assert.That(badRequestObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

            var responseResult = badRequestObjectResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("bad_request"));
            Assert.That(responseResult?.Error, Is.Not.Null);
            Assert.That(responseResult?.Result, Is.Null);
        }

        [Test]
        public async Task CreateSecretWithNoExpirationSettingsProvided()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request to create secret is made, without body specified.
            var request = TestFactory.CreateHttpRequestData("post");
            request.SetBodyAsJson(new MetadataCreationInput());

            var response = await this.secretMeta.Run(request, "badrequest", claimsPrincipal, this.logger);
            var badRequestObjectResult = response as TestHttpResponseData;

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error.
            Assert.That(badRequestObjectResult, Is.Not.Null);
            Assert.That(badRequestObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

            var responseResult = badRequestObjectResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("bad_request"));
            Assert.That(responseResult?.Error, Is.Not.Null);
            Assert.That(responseResult?.Result, Is.Null);
        }

        [Test]
        public async Task UpdateSecretWithNoExpirationSettingsProvided()
        {
            // [GIVEN] A secret with name 'x' exists.
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
            var response = await this.secretMeta.Run(request, "badrequest3", claimsPrincipal, this.logger);
            var okObjectResult = response as TestHttpResponseData;
            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // [WHEN] A request to update secret 'x' is made, without expiration settings specified.
            request = TestFactory.CreateHttpRequestData("patch");
            var patchInput = new MetadataUpdateInput();
            request.SetBodyAsJson(patchInput);
            response = await this.secretMeta.Run(request, "badrequest3", claimsPrincipal, this.logger);
            var badRequestObjectResult = response as TestHttpResponseData;

            // [THEN] BadRequestObjectResult is returned with Status = 'error', null Result and non-null Error.
            Assert.That(badRequestObjectResult, Is.Not.Null);
            Assert.That(badRequestObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

            var responseResult = badRequestObjectResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("bad_request"));
            Assert.That(responseResult?.Error, Is.Not.Null);
            Assert.That(responseResult?.Result, Is.Null);
        }

        [Test]
        public async Task CreateSecretSunshine()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request is made to create secret.
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

            var response = await this.secretMeta.Run(request, "sunshine", claimsPrincipal, this.logger);
            var okObjectResult = response as TestHttpResponseData;

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error.
            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectResult?.ReadBodyAsJson<BaseResponseObject<ObjectMetadataOutput>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            var metadata = responseResult?.Result;
            Assert.That(metadata, Is.Not.Null);
            Assert.That(metadata?.ObjectName, Is.EqualTo("sunshine"));

            Assert.That(metadata?.ExpirationSettings?.ScheduleExpiration, Is.EqualTo(creationInput.ExpirationSettings.ScheduleExpiration));
            Assert.That(metadata?.ExpirationSettings?.ExpireAt, Is.EqualTo(creationInput.ExpirationSettings.ExpireAt));
            Assert.That(metadata?.ExpirationSettings?.ExpireOnIdleTime, Is.EqualTo(creationInput.ExpirationSettings.ExpireOnIdleTime));
            Assert.That(metadata?.ExpirationSettings?.IdleTimeToExpire, Is.EqualTo(creationInput.ExpirationSettings.IdleTimeToExpire));

            var contentList = metadata?.Content;
            Assert.That(contentList, Is.Not.Null);
            Assert.That(contentList?.Count, Is.EqualTo(1));

            var mainContent = contentList?[0];
            Assert.That(mainContent?.ContentType, Is.EqualTo(string.Empty));
            Assert.That(mainContent?.FileName, Is.EqualTo(string.Empty));
            Assert.That(string.IsNullOrWhiteSpace(mainContent?.ContentName), Is.False);
            Assert.That(mainContent?.IsMain, Is.True);
            Assert.That(mainContent?.IsReady, Is.False);

            var createdSecret = await this.dbContext.Objects.FindAsync("sunshine");
            Assert.That(createdSecret?.CreatedBy, Is.EqualTo("User first@test.test"));
            Assert.That(createdSecret?.CreatedAt, Is.EqualTo(DateTimeProvider.UtcNow));
            Assert.That(createdSecret?.ModifiedBy, Is.EqualTo(string.Empty));
            Assert.That(createdSecret?.ModifiedAt, Is.EqualTo(DateTime.MinValue));
        }

        [Test]
        public async Task CreateAndListSecretsSunshine()
        {
            // [GIVEN] A user has several secrets with 'Read' access.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
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

            var secretCount = 10;
            for (int i = 0; i < secretCount; i++)
            {
                var request = TestFactory.CreateHttpRequestData("post");
                request.SetBodyAsJson(creationInput);
                var response = await this.secretMeta.Run(request, $"somesecret{i}", claimsPrincipal, this.logger);
                var okObjectResult = response as TestHttpResponseData;

                Assert.That(okObjectResult, Is.Not.Null);
                Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            }

            var secrets = await this.dbContext.Objects.Where(o => o.ObjectName.StartsWith("somesecret")).ToListAsync();
            Assert.That(secrets.Count, Is.EqualTo(secretCount));

            var getRequest = TestFactory.CreateHttpRequestData("get");
            var getResponse = await this.secretMeta.RunList(getRequest, claimsPrincipal, this.logger);
            var okObjectListResult = getResponse as TestHttpResponseData;

            Assert.That(okObjectListResult, Is.Not.Null);
            Assert.That(okObjectListResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var listResponseResult = okObjectListResult?.ReadBodyAsJson<BaseResponseObject<List<SubjectPermissionsOutput>>>();
            Assert.That(listResponseResult, Is.Not.Null);
            Assert.That(listResponseResult?.Status, Is.EqualTo("ok"));
            Assert.That(listResponseResult?.Error, Is.Null);

            var list = listResponseResult?.Result;
            Assert.That(list, Is.Not.Null);
            Assert.That(list?.Count, Is.EqualTo(secretCount));
        }

        [Test]
        public async Task CreateSecretTwice()
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

            var response = await this.secretMeta.Run(request, "twice", claimsPrincipal, this.logger);
            var okObjectResult = response as TestHttpResponseData;

            // [GIVEN] A secret 'x' exists.
            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // [WHEN] A request is made to create secret with then same name 'x'.
            request = TestFactory.CreateHttpRequestData("post");
            request.SetBodyAsJson(creationInput);
            response = await this.secretMeta.Run(request, "twice", claimsPrincipal, this.logger);
            var conflictObjectResult = response as TestHttpResponseData;

            // [THEN] ConflictObjectResult is returned with Status = 'conflict', null Result and non-null Error.
            Assert.That(conflictObjectResult, Is.Not.Null);
            Assert.That(conflictObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));

            var responseResult = conflictObjectResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("conflict"));
            Assert.That(responseResult?.Error, Is.Not.Null);
            Assert.That(responseResult?.Result, Is.Null);
        }

        [Test]
        public async Task CreateAndGetSecretSunshine()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            var postRequest = TestFactory.CreateHttpRequestData("post");
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

            postRequest.SetBodyAsJson(creationInput);

            var postResponse = await this.secretMeta.Run(postRequest, "sunshine2", claimsPrincipal, this.logger);
            var okObjectResult = postResponse as TestHttpResponseData;

            // [GIVEN] Secret is created with name 'x'.
            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromHours(1);

            // [WHEN] The same user is making a request to get secret 'x'.
            var getRequest = TestFactory.CreateHttpRequestData("get");
            var getResponse = await this.secretMeta.Run(getRequest, "sunshine2", claimsPrincipal, this.logger);

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error.
            okObjectResult = getResponse as TestHttpResponseData;
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectResult?.ReadBodyAsJson<BaseResponseObject<ObjectMetadataOutput>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            var metadata = responseResult?.Result;
            Assert.That(metadata, Is.Not.Null);
            Assert.That(metadata?.ObjectName, Is.EqualTo("sunshine2"));

            Assert.That(metadata?.ExpirationSettings?.ScheduleExpiration, Is.EqualTo(creationInput.ExpirationSettings.ScheduleExpiration));
            Assert.That(metadata?.ExpirationSettings?.ExpireAt, Is.EqualTo(creationInput.ExpirationSettings.ExpireAt));
            Assert.That(metadata?.ExpirationSettings?.ExpireOnIdleTime, Is.EqualTo(creationInput.ExpirationSettings.ExpireOnIdleTime));
            Assert.That(metadata?.ExpirationSettings?.IdleTimeToExpire, Is.EqualTo(creationInput.ExpirationSettings.IdleTimeToExpire));

            var contentList = metadata?.Content;
            Assert.That(contentList, Is.Not.Null);
            Assert.That(contentList?.Count, Is.EqualTo(1));

            var mainContent = contentList?[0];
            Assert.That(mainContent?.ContentType, Is.EqualTo(string.Empty));
            Assert.That(mainContent?.FileName, Is.EqualTo(string.Empty));
            Assert.That(string.IsNullOrWhiteSpace(mainContent?.ContentName), Is.False);
            Assert.That(mainContent?.IsMain, Is.True);
            Assert.That(mainContent?.IsReady, Is.False);
        }

        [Test]
        public async Task CreateAndUpdateSecretSunshine()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            var postRequest = TestFactory.CreateHttpRequestData("post");
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

            postRequest.SetBodyAsJson(creationInput);

            var postResponse = await this.secretMeta.Run(postRequest, "sunshine3", claimsPrincipal, this.logger);
            var okObjectResult = postResponse as TestHttpResponseData;

            // [GIVEN] Secret is created with name 'x'.
            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromHours(1);

            // [WHEN] The same user is making a request to get secret 'x'.
            var patchRequest = TestFactory.CreateHttpRequestData("patch");
            var updateInput = new MetadataCreationInput()
            {
                ExpirationSettings = new ExpirationSettingsInput()
                {
                    ScheduleExpiration = true,
                    ExpireAt = DateTimeProvider.UtcNow + TimeSpan.FromDays(180),
                    ExpireOnIdleTime = false,
                    IdleTimeToExpire = TimeSpan.FromDays(180)
                }
            };

            patchRequest.SetBodyAsJson(updateInput);
            var patchResponse = await this.secretMeta.Run(patchRequest, "sunshine3", claimsPrincipal, this.logger);

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error.
            okObjectResult = patchResponse as TestHttpResponseData;
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectResult?.ReadBodyAsJson<BaseResponseObject<ObjectMetadataOutput>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            var metadata = responseResult?.Result;
            Assert.That(metadata, Is.Not.Null);
            Assert.That(metadata?.ObjectName, Is.EqualTo("sunshine3"));

            Assert.That(metadata?.ExpirationSettings?.ScheduleExpiration, Is.EqualTo(updateInput.ExpirationSettings.ScheduleExpiration));
            Assert.That(metadata?.ExpirationSettings?.ExpireAt, Is.EqualTo(updateInput.ExpirationSettings.ExpireAt));
            Assert.That(metadata?.ExpirationSettings?.ExpireOnIdleTime, Is.EqualTo(updateInput.ExpirationSettings.ExpireOnIdleTime));
            Assert.That(metadata?.ExpirationSettings?.IdleTimeToExpire, Is.EqualTo(updateInput.ExpirationSettings.IdleTimeToExpire));

            var contentList = metadata?.Content;
            Assert.That(contentList, Is.Not.Null);
            Assert.That(contentList?.Count, Is.EqualTo(1));

            var mainContent = contentList?[0];
            Assert.That(mainContent?.ContentType, Is.EqualTo(string.Empty));
            Assert.That(mainContent?.FileName, Is.EqualTo(string.Empty));
            Assert.That(string.IsNullOrWhiteSpace(mainContent?.ContentName), Is.False);
            Assert.That(mainContent?.IsMain, Is.True);
            Assert.That(mainContent?.IsReady, Is.False);
        }

        [Test]
        public async Task CreateAndDeleteSecretSunshine()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            var postRequest = TestFactory.CreateHttpRequestData("post");
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

            postRequest.SetBodyAsJson(creationInput);

            var postResponse = await this.secretMeta.Run(postRequest, "sunshine4", claimsPrincipal, this.logger);
            var okObjectResult = postResponse as TestHttpResponseData;

            // [GIVEN] Secret is created with name 'x'.
            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromHours(1);

            // [WHEN] The same user is making a request to delete secret 'x'.
            var deleteRequest = TestFactory.CreateHttpRequestData("delete");
            var deleteResponse = await this.secretMeta.Run(deleteRequest, "sunshine4", claimsPrincipal, this.logger);

            // [THEN] OkObjectResult is returned with Status = 'ok', Result = 'ok' and null Error.
            okObjectResult = deleteResponse as TestHttpResponseData;
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectResult?.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);
            Assert.That(responseResult?.Result, Is.EqualTo("ok"));

            var deletedSecret = await this.dbContext.Objects.FindAsync("sunshine4");
            Assert.That(deletedSecret, Is.Null);
        }
    }
}