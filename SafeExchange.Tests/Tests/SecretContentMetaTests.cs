/// <summary>
/// SecretContentMetaTests
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
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Security.Claims;
    using System.Text.Json;
    using System.Threading.Tasks;

    [TestFixture]
    public class SecretContentMetaTests
    {
        private ILogger logger;

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

        private ClaimsIdentity firstIdentity;
        private ClaimsIdentity secondIdentity;

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
            this.globalFilters = new GlobalFilters(this.testConfiguration, this.tokenHelper, this.graphDataProvider, TestFactory.CreateLogger<GlobalFilters>(LoggerTypes.Console));

            this.blobHelper = new TestBlobHelper();
            this.purger = new PurgeManager(this.testConfiguration, this.blobHelper, TestFactory.CreateLogger<PurgeManager>(LoggerTypes.Console));

            this.permissionsManager = new PermissionsManager(this.testConfiguration, this.dbContext, TestFactory.CreateLogger<PermissionsManager>(LoggerTypes.Console));

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

            DateTimeProvider.SpecifiedDateTime = DateTime.UtcNow;
            DateTimeProvider.UseSpecifiedDateTime = true;

            this.imageContentFileName = "testimage_small.jpg";
            this.imageContent = File.ReadAllBytes(Path.Combine("Resources", this.imageContentFileName));
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
            this.dbContext.NotificationSubscriptions.RemoveRange(this.dbContext.NotificationSubscriptions.ToList());
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
            var request = TestFactory.CreateHttpRequest("post");
            var creationInput = new ContentMetadataCreationInput()
            {
                ContentType = "image/jpeg",
                FileName = this.imageContentFileName
            };

            request.Body = new StringContent(DefaultJsonSerializer.Serialize(creationInput)).ReadAsStream();

            var response = await this.secretContentMeta.Run(request, "secret1", string.Empty, claimsPrincipal, this.logger);
            var okObjectResult = response as OkObjectResult;

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error.
            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            var responseResult = okObjectResult?.Value as BaseResponseObject<ContentMetadataOutput>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);

            var contentMetadata = responseResult?.Result;
            Assert.IsNotNull(contentMetadata);
            Assert.IsNotNull(contentMetadata?.ContentName);
            Assert.AreEqual("image/jpeg", contentMetadata?.ContentType);
            Assert.AreEqual(this.imageContentFileName, contentMetadata?.FileName);
            Assert.IsFalse(contentMetadata?.IsMain);
            Assert.IsFalse(contentMetadata?.IsReady);

            var chunks = contentMetadata?.Chunks;
            Assert.IsNotNull(chunks);
            Assert.AreEqual(0, chunks?.Count);
        }

        [Test]
        public async Task CreateSecretContent_NotExists()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request is made to add content to the secret that not exists.
            var request = TestFactory.CreateHttpRequest("post");
            var creationInput = new ContentMetadataCreationInput()
            {
                ContentType = "image/jpeg",
                FileName = this.imageContentFileName
            };

            request.Body = new StringContent(DefaultJsonSerializer.Serialize(creationInput)).ReadAsStream();

            var response = await this.secretContentMeta.Run(request, "notexists", string.Empty, claimsPrincipal, this.logger);
            var notFoundObjectResult = response as NotFoundObjectResult;

            // [THEN] NotFoundObjectResult is returned with Status = 'not_found', non-null Result and null Error.
            Assert.IsNotNull(notFoundObjectResult);
            Assert.AreEqual(404, notFoundObjectResult?.StatusCode);

            var responseResult = notFoundObjectResult?.Value as BaseResponseObject<object>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("not_found", responseResult?.Status);
            Assert.IsNotNull(responseResult?.Error);
            Assert.IsNull(responseResult?.Result);
        }

        [Test]
        public async Task CreateSecretContent_WrongContentId()
        {
            // [GIVEN] A user with valid credentials.
            await this.CreateSecret("secret2");
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request is made to add content to the secret the not exists.
            var request = TestFactory.CreateHttpRequest("post");
            var creationInput = new ContentMetadataCreationInput()
            {
                ContentType = "image/jpeg",
                FileName = this.imageContentFileName
            };

            request.Body = new StringContent(DefaultJsonSerializer.Serialize(creationInput)).ReadAsStream();

            var response = await this.secretContentMeta.Run(request, "secret2", "content", claimsPrincipal, this.logger);
            var badRequestObjectResult = response as BadRequestObjectResult;

            // [THEN] BadRequestObjectResult is returned with Status = 'bad_request', null Result and non-null Error.
            Assert.IsNotNull(badRequestObjectResult);
            Assert.AreEqual(400, badRequestObjectResult?.StatusCode);

            var responseResult = badRequestObjectResult?.Value as BaseResponseObject<object>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("bad_request", responseResult?.Status);
            Assert.IsNotNull(responseResult?.Error);
            Assert.IsNull(responseResult?.Result);
        }

        [Test]
        public async Task CreateContentAndGetSecretSunshine()
        {
            // [GIVEN] A secret with name 'x'.
            await this.CreateSecret("secret3");
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [GIVEN] A content is added to the secret.
            var request = TestFactory.CreateHttpRequest("post");
            var creationInput = new ContentMetadataCreationInput()
            {
                ContentType = "image/jpeg",
                FileName = this.imageContentFileName
            };

            request.Body = new StringContent(DefaultJsonSerializer.Serialize(creationInput)).ReadAsStream();

            var response = await this.secretContentMeta.Run(request, "secret3", string.Empty, claimsPrincipal, this.logger);
            var okObjectResult = response as OkObjectResult;

            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromHours(1);

            // [WHEN] A request is made to get secret 'x'.
            var getRequest = TestFactory.CreateHttpRequest("get");
            var getResponse = await this.secretMeta.Run(getRequest, "secret3", claimsPrincipal, this.logger);

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error. The result contains added content.
            okObjectResult = getResponse as OkObjectResult;
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            var responseResult = okObjectResult?.Value as BaseResponseObject<ObjectMetadataOutput>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);

            var metadata = responseResult?.Result;
            var contentList = metadata?.Content;
            Assert.IsNotNull(contentList);
            Assert.AreEqual(2, contentList?.Count);

            var addedContent = contentList?.FirstOrDefault(c => !c.IsMain);
            Assert.IsNotNull(addedContent);

            Assert.AreEqual("image/jpeg", addedContent?.ContentType);
            Assert.AreEqual(this.imageContentFileName, addedContent?.FileName);
            Assert.IsFalse(string.IsNullOrWhiteSpace(addedContent?.ContentName));
            Assert.IsFalse(addedContent?.IsMain);
            Assert.IsFalse(addedContent?.IsReady);

            var chunks = addedContent?.Chunks;
            Assert.IsNotNull(chunks);
            Assert.AreEqual(0, chunks?.Count);
        }

        [Test]
        public async Task UpdateSecretContentSunshine()
        {
            // [GIVEN] A secret with name 'x' and added content.
            await this.CreateSecret("secret4");
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request is made to add content to the secret.
            var request = TestFactory.CreateHttpRequest("post");
            var creationInput = new ContentMetadataCreationInput()
            {
                ContentType = "image/jpeg",
                FileName = this.imageContentFileName
            };

            request.Body = new StringContent(DefaultJsonSerializer.Serialize(creationInput)).ReadAsStream();
            var response = await this.secretContentMeta.Run(request, "secret4", string.Empty, claimsPrincipal, this.logger);
            var okObjectResult = response as OkObjectResult;

            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            var createResponseResult = okObjectResult?.Value as BaseResponseObject<ContentMetadataOutput>;
            var contentName = createResponseResult?.Result?.ContentName;
            Assert.IsNotNull(contentName);

            // [WHEN] A request is made to change content type and file name.
            var patchRequest = TestFactory.CreateHttpRequest("patch");
            var patchInput = new ContentMetadataUpdateInput()
            {
                ContentType = "image/bmp",
                FileName = "image.bmp"
            };

            patchRequest.Body = new StringContent(DefaultJsonSerializer.Serialize(patchInput)).ReadAsStream();

            response = await this.secretContentMeta.Run(patchRequest, "secret4", contentName, claimsPrincipal, this.logger);
            okObjectResult = response as OkObjectResult;

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error. Content is updated in the DB.
            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            var responseResult = okObjectResult?.Value as BaseResponseObject<ContentMetadataOutput>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);

            var contentMetadata = responseResult?.Result;
            Assert.IsNotNull(contentMetadata);
            Assert.IsNotNull(contentMetadata?.ContentName);
            Assert.AreEqual(patchInput.ContentType, contentMetadata?.ContentType);
            Assert.AreEqual(patchInput.FileName, contentMetadata?.FileName);
            Assert.IsFalse(contentMetadata?.IsMain);
            Assert.IsFalse(contentMetadata?.IsReady);

            var chunks = contentMetadata?.Chunks;
            Assert.IsNotNull(chunks);
            Assert.AreEqual(0, chunks?.Count);

            var objectMetadata = await this.dbContext.Objects.FindAsync("secret4");
            var content = objectMetadata?.Content.FirstOrDefault(c => c.ContentName.Equals(contentName));
            Assert.AreEqual(patchInput.ContentType, content?.ContentType);
            Assert.AreEqual(patchInput.FileName, content?.FileName);
        }

        [Test]
        public async Task DeleteSecretContentSunshine()
        {
            // [GIVEN] A secret with name 'x' and added content.
            await this.CreateSecret("secret5");
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request is made to add content to the secret.
            var request = TestFactory.CreateHttpRequest("post");
            var creationInput = new ContentMetadataCreationInput()
            {
                ContentType = "image/jpeg",
                FileName = this.imageContentFileName
            };

            request.Body = new StringContent(DefaultJsonSerializer.Serialize(creationInput)).ReadAsStream();
            var response = await this.secretContentMeta.Run(request, "secret5", string.Empty, claimsPrincipal, this.logger);
            var okObjectResult = response as OkObjectResult;

            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            var createResponseResult = okObjectResult?.Value as BaseResponseObject<ContentMetadataOutput>;
            var contentName = createResponseResult?.Result?.ContentName;
            Assert.IsNotNull(contentName);

            // [WHEN] A request is made to delete existing content.
            var deleteRequest = TestFactory.CreateHttpRequest("delete");
            response = await this.secretContentMeta.Run(deleteRequest, "secret5", contentName, claimsPrincipal, this.logger);
            okObjectResult = response as OkObjectResult;

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error. Content is deleted from the DB.
            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            var responseResult = okObjectResult?.Value as BaseResponseObject<string>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);
            Assert.AreEqual("ok", responseResult?.Result);

            var objectMetadata = await this.dbContext.Objects.FindAsync("secret5");
            var content = objectMetadata?.Content.FirstOrDefault(c => c.ContentName.Equals(contentName));
            Assert.IsNull(content);
        }

        [Test]
        public async Task CannotDeleteSecretMainContent()
        {
            // [GIVEN] A secret with name 'x'.
            await this.CreateSecret("secret6");
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            var existingSecret = await this.dbContext.Objects.FindAsync("secret6");
            var mainContent = existingSecret.Content.FirstOrDefault(c => c.IsMain);
            Assert.IsNotNull(mainContent);

            // [WHEN] A request is made to delete main content of the secret.
            var deleteRequest = TestFactory.CreateHttpRequest("delete");
            var response = await this.secretContentMeta.Run(deleteRequest, "secret6", mainContent.ContentName, claimsPrincipal, this.logger);
            var badRequestObjectResult = response as BadRequestObjectResult;

            // [THEN] BadRequestObjectResult is returned with Status = 'bad_request', null Result and non-null Error. Content is not deleted.
            Assert.IsNotNull(badRequestObjectResult);
            Assert.AreEqual(400, badRequestObjectResult?.StatusCode);

            var responseResult = badRequestObjectResult?.Value as BaseResponseObject<object>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("bad_request", responseResult?.Status);
            Assert.IsNull(responseResult?.Result);
            Assert.IsNotNull(responseResult?.Error);

            existingSecret = await this.dbContext.Objects.FindAsync("secret6");
            mainContent = existingSecret?.Content.FirstOrDefault(c => c.IsMain);
            Assert.IsNotNull(mainContent);
        }

        private async Task CreateSecret(string secretName)
        {
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
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
    }
}