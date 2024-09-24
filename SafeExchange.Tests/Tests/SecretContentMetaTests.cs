/// <summary>
/// SecretContentMetaTests
/// </summary>

namespace SafeExchange.Tests
{
    using Azure.Core.Serialization;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Options;
    using Microsoft.IdentityModel.Tokens;
    using Moq;
    using NUnit.Framework;
    using NUnit.Framework.Internal;
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
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    [TestFixture]
    public class SecretContentMetaTests
    {
        private Microsoft.Extensions.Logging.ILogger logger;

        private SafeExchangeSecretMeta secretMeta;
        private SafeExchangeSecretContentMeta secretContentMeta;

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

        private string imageContentFileName = "testimage_small.jpg";
        private byte[] imageContent;

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            var builder = new ConfigurationBuilder().AddUserSecrets<SecretContentMetaTests>();
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
                .UseCosmos(secretConfiguration.GetConnectionString("CosmosDb"), databaseName: $"{nameof(SecretContentMetaTests)}Database")
                .Options;

            this.dbContext = new SafeExchangeDbContext(dbContextOptions);
            this.dbContext.Database.EnsureCreated();

            this.tokenHelper = new TestTokenHelper();
            this.graphDataProvider = new TestGraphDataProvider();

            GloballyAllowedGroupsConfiguration gagc = new GloballyAllowedGroupsConfiguration();
            var groupsConfiguration = Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(x => x.CurrentValue == gagc);

            AdminConfiguration ac = new AdminConfiguration();
            var adminConfiguration = Mock.Of<IOptionsMonitor<AdminConfiguration>>(x => x.CurrentValue == ac);

            this.globalFilters = new GlobalFilters(groupsConfiguration, adminConfiguration, this.tokenHelper, TestFactory.CreateLogger<GlobalFilters>(LoggerTypes.Console));

            this.blobHelper = new TestBlobHelper();
            this.purger = new PurgeManager(this.testConfiguration, this.blobHelper, TestFactory.CreateLogger<PurgeManager>(LoggerTypes.Console));

            this.permissionsManager = new PermissionsManager(this.testConfiguration, this.dbContext, TestFactory.CreateLogger<PermissionsManager>(LoggerTypes.Console));

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

            this.imageContentFileName = "testimage_small.jpg";
            this.imageContent = File.ReadAllBytes(Path.Combine("Resources", this.imageContentFileName));

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
        public async Task Setup()
        {
            DateTimeProvider.SpecifiedDateTime = DateTime.UtcNow;

            this.secretMeta = new SafeExchangeSecretMeta(
                this.testConfiguration, this.dbContext, this.tokenHelper,
                this.globalFilters, this.purger, this.permissionsManager);

            this.secretContentMeta = new SafeExchangeSecretContentMeta(
                this.testConfiguration, this.dbContext, this.tokenHelper,
                this.globalFilters, this.purger, this.permissionsManager);
        }

        [TearDown]
        public async Task Cleanup()
        {
            this.graphDataProvider.GroupMemberships.Clear();

            this.dbContext.ChangeTracker.Clear();
            this.dbContext.Users.RemoveRange(this.dbContext.Users.ToList());
            this.dbContext.Objects.RemoveRange(this.dbContext.Objects.ToList());
            this.dbContext.Permissions.RemoveRange(this.dbContext.Permissions.ToList());
            this.dbContext.AccessRequests.RemoveRange(this.dbContext.AccessRequests.ToList());
            this.dbContext.GroupDictionary.RemoveRange(this.dbContext.GroupDictionary.ToList());
            await this.dbContext.SaveChangesAsync();
        }

        [Test]
        public async Task CreateSecretContentSunshine()
        {
            // [GIVEN] A secret with name 'x'.
            await this.CreateSecret("secret1");
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request is made to add content to the secret.
            var request = TestFactory.CreateHttpRequestData("post");
            var creationInput = new ContentMetadataCreationInput()
            {
                ContentType = "image/jpeg",
                FileName = this.imageContentFileName
            };

            request.SetBodyAsJson(creationInput);

            var response = await this.secretContentMeta.Run(request, "secret1", string.Empty, claimsPrincipal, this.logger);
            var okObjectResult = response as TestHttpResponseData;

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error.
            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectResult?.ReadBodyAsJson<BaseResponseObject<ContentMetadataOutput>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            var contentMetadata = responseResult?.Result;
            Assert.That(contentMetadata, Is.Not.Null);
            Assert.That(contentMetadata?.ContentName, Is.Not.Null);
            Assert.That(contentMetadata?.ContentType, Is.EqualTo("image/jpeg"));
            Assert.That(contentMetadata?.FileName, Is.EqualTo(this.imageContentFileName));
            Assert.That(contentMetadata?.IsMain, Is.False);
            Assert.That(contentMetadata?.IsReady, Is.False);

            var chunks = contentMetadata?.Chunks;
            Assert.That(chunks, Is.Not.Null);
            Assert.That(chunks?.Count, Is.EqualTo(0));
        }

        [Test]
        public async Task CreateSecretContent_NotExists()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request is made to add content to the secret that not exists.
            var request = TestFactory.CreateHttpRequestData("post");
            var creationInput = new ContentMetadataCreationInput()
            {
                ContentType = "image/jpeg",
                FileName = this.imageContentFileName
            };

            request.SetBodyAsJson(creationInput);

            var response = await this.secretContentMeta.Run(request, "notexists", string.Empty, claimsPrincipal, this.logger);
            var notFoundObjectResult = response as TestHttpResponseData;

            // [THEN] NotFoundObjectResult is returned with Status = 'not_found', non-null Result and null Error.
            Assert.That(notFoundObjectResult, Is.Not.Null);
            Assert.That(notFoundObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

            var responseResult = notFoundObjectResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("not_found"));
            Assert.That(responseResult?.Error, Is.Not.Null);
            Assert.That(responseResult?.Result, Is.Null);
        }

        [Test]
        public async Task CreateSecretContent_WrongContentId()
        {
            // [GIVEN] A user with valid credentials.
            await this.CreateSecret("secret2");
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request is made to add content to the secret the not exists.
            var request = TestFactory.CreateHttpRequestData("post");
            var creationInput = new ContentMetadataCreationInput()
            {
                ContentType = "image/jpeg",
                FileName = this.imageContentFileName
            };

            request.SetBodyAsJson(creationInput);

            var response = await this.secretContentMeta.Run(request, "secret2", "content", claimsPrincipal, this.logger);
            var badRequestObjectResult = response as TestHttpResponseData;

            // [THEN] BadRequestObjectResult is returned with Status = 'bad_request', null Result and non-null Error.
            Assert.That(badRequestObjectResult, Is.Not.Null);
            Assert.That(badRequestObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

            var responseResult = badRequestObjectResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("bad_request"));
            Assert.That(responseResult?.Error, Is.Not.Null);
            Assert.That(responseResult?.Result, Is.Null);
        }

        [Test]
        public async Task CreateContentAndGetSecretSunshine()
        {
            // [GIVEN] A secret with name 'x'.
            await this.CreateSecret("secret3");
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [GIVEN] A content is added to the secret.
            var request = TestFactory.CreateHttpRequestData("post");
            var creationInput = new ContentMetadataCreationInput()
            {
                ContentType = "image/jpeg",
                FileName = this.imageContentFileName
            };

            request.SetBodyAsJson(creationInput);

            var response = await this.secretContentMeta.Run(request, "secret3", string.Empty, claimsPrincipal, this.logger);
            var okObjectResult = response as TestHttpResponseData;

            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromHours(1);

            // [WHEN] A request is made to get secret 'x'.
            var getRequest = TestFactory.CreateHttpRequestData("get");
            var getResponse = await this.secretMeta.Run(getRequest, "secret3", claimsPrincipal, this.logger);

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error. The result contains added content.
            okObjectResult = getResponse as TestHttpResponseData;
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectResult?.ReadBodyAsJson<BaseResponseObject<ObjectMetadataOutput>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            var metadata = responseResult?.Result;
            var contentList = metadata?.Content;
            Assert.That(contentList, Is.Not.Null);
            Assert.That(contentList?.Count, Is.EqualTo(2));

            var addedContent = contentList?.FirstOrDefault(c => !c.IsMain);
            Assert.That(addedContent, Is.Not.Null);

            Assert.That(addedContent?.ContentType, Is.EqualTo("image/jpeg"));
            Assert.That(addedContent?.FileName, Is.EqualTo(this.imageContentFileName));
            Assert.That(string.IsNullOrWhiteSpace(addedContent?.ContentName), Is.False);
            Assert.That(addedContent?.IsMain, Is.False);
            Assert.That(addedContent?.IsReady, Is.False);

            var chunks = addedContent?.Chunks;
            Assert.That(chunks, Is.Not.Null);
            Assert.That(chunks?.Count, Is.EqualTo(0));
        }

        [Test]
        public async Task UpdateSecretContentSunshine()
        {
            // [GIVEN] A secret with name 'x' and added content.
            await this.CreateSecret("secret4");
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request is made to add content to the secret.
            var request = TestFactory.CreateHttpRequestData("post");
            var creationInput = new ContentMetadataCreationInput()
            {
                ContentType = "image/jpeg",
                FileName = this.imageContentFileName
            };

            request.SetBodyAsJson(creationInput);
            var response = await this.secretContentMeta.Run(request, "secret4", string.Empty, claimsPrincipal, this.logger);
            var okObjectResult = response as TestHttpResponseData;

            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var createResponseResult = okObjectResult?.ReadBodyAsJson<BaseResponseObject<ContentMetadataOutput>>();
            var contentName = createResponseResult?.Result?.ContentName;
            Assert.That(contentName, Is.Not.Null);

            // [WHEN] A request is made to change content type and file name.
            var patchRequest = TestFactory.CreateHttpRequestData("patch");
            var patchInput = new ContentMetadataUpdateInput()
            {
                ContentType = "image/bmp",
                FileName = "image.bmp"
            };

            patchRequest.SetBodyAsJson(patchInput);

            response = await this.secretContentMeta.Run(patchRequest, "secret4", contentName, claimsPrincipal, this.logger);
            okObjectResult = response as TestHttpResponseData;

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error. Content is updated in the DB.
            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectResult?.ReadBodyAsJson<BaseResponseObject<ContentMetadataOutput>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            var contentMetadata = responseResult?.Result;
            Assert.That(contentMetadata, Is.Not.Null);
            Assert.That(contentMetadata?.ContentName, Is.Not.Null);
            Assert.That(contentMetadata?.ContentType, Is.EqualTo(patchInput.ContentType));
            Assert.That(contentMetadata?.FileName, Is.EqualTo(patchInput.FileName));
            Assert.That(contentMetadata?.IsMain, Is.False);
            Assert.That(contentMetadata?.IsReady, Is.False);

            var chunks = contentMetadata?.Chunks;
            Assert.That(chunks, Is.Not.Null);
            Assert.That(chunks?.Count, Is.EqualTo(0));

            var objectMetadata = await this.dbContext.Objects.FindAsync("secret4");
            var content = objectMetadata?.Content.FirstOrDefault(c => c.ContentName.Equals(contentName));
            Assert.That(content?.ContentType, Is.EqualTo(patchInput.ContentType));
            Assert.That(content?.FileName, Is.EqualTo(patchInput.FileName));
        }

        [Test]
        public async Task DeleteSecretContentSunshine()
        {
            // [GIVEN] A secret with name 'x' and added content.
            await this.CreateSecret("secret5");
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request is made to add content to the secret.
            var request = TestFactory.CreateHttpRequestData("post");
            var creationInput = new ContentMetadataCreationInput()
            {
                ContentType = "image/jpeg",
                FileName = this.imageContentFileName
            };

            request.SetBodyAsJson(creationInput);
            var response = await this.secretContentMeta.Run(request, "secret5", string.Empty, claimsPrincipal, this.logger);
            var okObjectResult = response as TestHttpResponseData;

            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var createResponseResult = okObjectResult?.ReadBodyAsJson<BaseResponseObject<ContentMetadataOutput>>();
            var contentName = createResponseResult?.Result?.ContentName;
            Assert.That(contentName, Is.Not.Null);

            // [WHEN] A request is made to delete existing content.
            var deleteRequest = TestFactory.CreateHttpRequestData("delete");
            response = await this.secretContentMeta.Run(deleteRequest, "secret5", contentName, claimsPrincipal, this.logger);
            okObjectResult = response as TestHttpResponseData;

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error. Content is deleted from the DB.
            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectResult?.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);
            Assert.That(responseResult?.Result, Is.EqualTo("ok"));

            var objectMetadata = await this.dbContext.Objects.FindAsync("secret5");
            var content = objectMetadata?.Content.FirstOrDefault(c => c.ContentName.Equals(contentName));
            Assert.That(content, Is.Null);
        }

        [Test]
        public async Task CannotDeleteSecretMainContent()
        {
            // [GIVEN] A secret with name 'x'.
            await this.CreateSecret("secret6");
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            var existingSecret = await this.dbContext.Objects.FindAsync("secret6");
            var mainContent = existingSecret.Content.FirstOrDefault(c => c.IsMain);
            Assert.That(mainContent, Is.Not.Null);

            // [WHEN] A request is made to delete main content of the secret.
            var deleteRequest = TestFactory.CreateHttpRequestData("delete");
            var response = await this.secretContentMeta.Run(deleteRequest, "secret6", mainContent.ContentName, claimsPrincipal, this.logger);
            var badRequestObjectResult = response as TestHttpResponseData;

            // [THEN] BadRequestObjectResult is returned with Status = 'bad_request', null Result and non-null Error. Content is not deleted.
            Assert.That(badRequestObjectResult, Is.Not.Null);
            Assert.That(badRequestObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

            var responseResult = badRequestObjectResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("bad_request"));
            Assert.That(responseResult?.Result, Is.Null);
            Assert.That(responseResult?.Error, Is.Not.Null);

            existingSecret = await this.dbContext.Objects.FindAsync("secret6");
            mainContent = existingSecret?.Content.FirstOrDefault(c => c.IsMain);
            Assert.That(mainContent, Is.Not.Null);
        }

        private async Task CreateSecret(string secretName)
        {
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
            var response = await this.secretMeta.Run(request, secretName, claimsPrincipal, this.logger);
            var okObjectResult = response as TestHttpResponseData;

            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
    }
}