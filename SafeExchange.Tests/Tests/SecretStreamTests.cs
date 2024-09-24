/// <summary>
/// SecretStreamTests
/// </summary>

namespace SafeExchange.Tests
{
    using Azure.Core.Serialization;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
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
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Security.Claims;
    using System.Threading.Tasks;

    [TestFixture]
    public class SecretStreamTests
    {
        private static readonly string DefaultSecretName = "defaultsecret";

        private ILogger logger;

        private SafeExchangeSecretMeta secretMeta;
        private SafeExchangeSecretContentMeta secretContentMeta;
        private SafeExchangeSecretStream secretStream;

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

        private string mainContentFileName = "main_content.txt";
        private byte[] mainContent;

        private byte[] mainContent_part_1;
        private byte[] mainContent_part_2;

        private byte[] imageContent_part_1;
        private byte[] imageContent_part_2;

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            var builder = new ConfigurationBuilder().AddUserSecrets<SecretStreamTests>();
            var secretConfiguration = builder.Build();

            this.logger = TestFactory.CreateLogger(LoggerTypes.Console);

            var configurationValues = new Dictionary<string, string>
                {
                    {"Features:UseNotifications", "False"},
                    {"Features:UseGroupsAuthorization", "True"},

                    {"AccessTickets:AccessTicketTimeout", "00:10:00.000"}
                };

            this.testConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(configurationValues)
                .Build();

            var dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(secretConfiguration.GetConnectionString("CosmosDb"), databaseName: $"{nameof(SecretStreamTests)}Database")
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

            var imageContentLengthPart1 = this.imageContent.Length / 2;
            var imageContentLengthPart2 = this.imageContent.Length - imageContentLengthPart1;

            this.imageContent_part_1 = new byte[imageContentLengthPart1];
            Buffer.BlockCopy(this.imageContent, 0, this.imageContent_part_1, 0, imageContentLengthPart1);

            this.imageContent_part_2 = new byte[imageContentLengthPart2];
            Buffer.BlockCopy(this.imageContent, imageContentLengthPart1, this.imageContent_part_2, 0, imageContentLengthPart2);

            this.mainContent = File.ReadAllBytes(Path.Combine("Resources", this.mainContentFileName));

            var mainContentLengthPart1 = this.mainContent.Length / 2;
            var mainContentLengthPart2 = this.mainContent.Length - mainContentLengthPart1;

            this.mainContent_part_1 = new byte[mainContentLengthPart1];
            Buffer.BlockCopy(this.mainContent, 0, this.mainContent_part_1, 0, mainContentLengthPart1);

            this.mainContent_part_2 = new byte[mainContentLengthPart2];
            Buffer.BlockCopy(this.mainContent, mainContentLengthPart1, this.mainContent_part_2, 0, mainContentLengthPart2);

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

            this.secretContentMeta = new SafeExchangeSecretContentMeta(
                this.testConfiguration, this.dbContext, this.tokenHelper,
                this.globalFilters, this.purger, this.permissionsManager);

            this.secretStream = new SafeExchangeSecretStream(
                this.testConfiguration, this.dbContext, this.tokenHelper,
                this.globalFilters, this.purger, this.blobHelper, this.permissionsManager);

            this.CreateSecret(DefaultSecretName).GetAwaiter().GetResult();
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
        public async Task CannotAddChunkIfSecretNotExists()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request is made to upload data to a secret that not exists.
            var request = TestFactory.CreateHttpRequestData("post");
            request.SetBodyAsStream(new ByteArrayContent(this.imageContent).ReadAsStream());

            var response = await this.secretStream.Run(request, "inexistent", "default", string.Empty, claimsPrincipal, this.logger);
            var notFoundObjectResult = response as TestHttpResponseData;

            Assert.That(notFoundObjectResult, Is.Not.Null);
            Assert.That(notFoundObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

            var responseResult = notFoundObjectResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("not_found"));
            Assert.That(responseResult?.Error, Is.Not.Null);
            Assert.That(responseResult?.Result, Is.Null);
        }

        [Test]
        public async Task CannotAddChunkIfContentNotExists()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var objectMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(DefaultSecretName));
            if (objectMetadata == null)
            {
                throw new AssertionException($"Metadata for secret is null.");
            }

            // [WHEN] A request is made to upload data to a secret that not exists.
            var request = TestFactory.CreateHttpRequestData("post");
            request.SetBodyAsStream(new ByteArrayContent(this.imageContent).ReadAsStream());

            var response = await this.secretStream.Run(request, objectMetadata.ObjectName, "inexistent", string.Empty, claimsPrincipal, this.logger);
            var notFoundObjectResult = response as TestHttpResponseData;

            Assert.That(notFoundObjectResult, Is.Not.Null);
            Assert.That(notFoundObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

            var responseResult = notFoundObjectResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("not_found"));
            Assert.That(responseResult?.Error, Is.Not.Null);
            Assert.That(responseResult?.Result, Is.Null);
        }

        [Test]
        public async Task AddChunkSunshine()
        {
            // [GIVEN] A secret with name 'x'.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            var objectMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(DefaultSecretName));
            var mainContent = objectMetadata?.Content.FirstOrDefault(c => c.IsMain);
            if (mainContent == null)
            {
                throw new AssertionException($"Main content for secret is null.");
            }

            // [WHEN] A request is made to upload data to main content.
            var request = TestFactory.CreateHttpRequestData("post");
            request.SetBodyAsStream(new ByteArrayContent(this.mainContent).ReadAsStream());

            var response = await this.secretStream.Run(request, DefaultSecretName, mainContent.ContentName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult = response as TestHttpResponseData;

            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectResult?.ReadBodyAsJson<BaseResponseObject<ChunkCreationOutput>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            var chunkMetadata = responseResult?.Result;
            Assert.That(chunkMetadata, Is.Not.Null);
            Assert.That(chunkMetadata?.ChunkName, Is.EqualTo($"{mainContent.ContentName}-{0:00000000}"));
            Assert.That(chunkMetadata?.Length, Is.EqualTo(this.mainContent.Length));
            Assert.That(string.IsNullOrEmpty(chunkMetadata?.AccessTicket), Is.True);

            // [THEN] A chunk is created with uploaded data.
            var existingChunkData = await this.blobHelper.DownloadAndDecryptBlobAsync(chunkMetadata?.ChunkName);
            Assert.That(existingChunkData.Length, Is.EqualTo(this.mainContent.Length));

            var existingChunkBytes = new byte[existingChunkData.Length];
            existingChunkData.Read(existingChunkBytes, 0, (int)existingChunkData.Length);

            for (var pos = 0; pos < existingChunkBytes.Length; pos++)
            {
                Assert.That(existingChunkBytes[pos], Is.EqualTo(this.mainContent[pos]));
            }
        }

        [Test]
        public async Task AddChunkAndGetSecretSunshine()
        {
            // [GIVEN] A secret with name 'x'.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            var objectMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(DefaultSecretName));
            var mainContent = objectMetadata?.Content.FirstOrDefault(c => c.IsMain);
            if (mainContent == null)
            {
                throw new AssertionException($"Main content for secret is null.");
            }

            // [GIVEN] Main content data was uploaded.
            var request = TestFactory.CreateHttpRequestData("post");
            request.SetBodyAsStream(new ByteArrayContent(this.imageContent).ReadAsStream());

            var response = await this.secretStream.Run(request, DefaultSecretName, mainContent.ContentName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult = response as TestHttpResponseData;

            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectResult?.ReadBodyAsJson<BaseResponseObject<ChunkCreationOutput>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            // [GIVEN] An attachment is uploaded.
            var request2 = TestFactory.CreateHttpRequestData("post");
            var creationInput = new ContentMetadataCreationInput()
            {
                ContentType = "image/jpeg",
                FileName = this.imageContentFileName
            };

            request2.SetBodyAsJson(creationInput);

            var response2 = await this.secretContentMeta.Run(request2, DefaultSecretName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult2 = response2 as TestHttpResponseData;

            Assert.That(okObjectResult2, Is.Not.Null);
            Assert.That(okObjectResult2?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var attachmentContent = objectMetadata.Content.FirstOrDefault(c => !c.IsMain);
            if (attachmentContent == null)
            {
                throw new AssertionException($"Attachment content for secret is null.");
            }

            var (chunkMetadata, headers) = await this.UploadDataAsync(objectMetadata.ObjectName, attachmentContent.ContentName, this.imageContent.Length);

            Assert.That(chunkMetadata, Is.Not.Null);
            Assert.That(chunkMetadata?.ChunkName, Is.Not.EqualTo($"{mainContent.ContentName}-{0:00000000}"));
            Assert.That(chunkMetadata?.Length, Is.EqualTo(this.imageContent.Length));

            IEnumerable<string>? ticketResponseHeader = default;
            headers?.TryGetValues(SafeExchangeSecretStream.AccessTicketHeaderName, out ticketResponseHeader);
            var accessTicket = ticketResponseHeader?.FirstOrDefault();
            Assert.That(string.IsNullOrEmpty(accessTicket), Is.True);

            // [WHEN] A request is made to get secret metadata.
            var getRequest = TestFactory.CreateHttpRequestData("get");

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error. Newly added chunk is present.
            var getResponse = await this.secretMeta.Run(getRequest, DefaultSecretName, claimsPrincipal, this.logger);
            var okObjectGetResult = getResponse as TestHttpResponseData;

            Assert.That(okObjectGetResult, Is.Not.Null);
            Assert.That(okObjectGetResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseGetResult = okObjectGetResult?.ReadBodyAsJson<BaseResponseObject<ObjectMetadataOutput>>();
            Assert.That(responseGetResult, Is.Not.Null);
            Assert.That(responseGetResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseGetResult?.Error, Is.Null);

            var metadata = responseGetResult?.Result;
            var content = responseGetResult?.Result?.Content;
            if (content == null || content.Count == 0)
            {
                throw new AssertionException("Content is null.");
            }

            var secondContent = content.FirstOrDefault(c => !c.IsMain);
            var chunks = secondContent?.Chunks;
            if (chunks == null || chunks.Count == 0)
            {
                throw new AssertionException("Chunks is null.");
            }

            Assert.That(chunks.Count, Is.EqualTo(1));
            var firstChunk = chunks.First();
            Assert.That(firstChunk.ChunkName, Is.EqualTo($"{secondContent.ContentName}-{0:00000000}"));
            Assert.That(firstChunk.Length, Is.EqualTo(this.imageContent.Length));

            var existingChunkData = await this.blobHelper.DownloadAndDecryptBlobAsync($"{secondContent.ContentName}-{"00000000"}");
            Assert.That(existingChunkData.Length, Is.EqualTo(this.imageContent.Length));

            var existingChunkBytes = new byte[existingChunkData.Length];
            existingChunkData.Read(existingChunkBytes, 0, (int)existingChunkData.Length);

            for (var pos = 0; pos < existingChunkBytes.Length; pos++)
            {
                Assert.That(existingChunkBytes[pos], Is.EqualTo(this.imageContent[pos]));
            }
        }

        [Test]
        public async Task AddChunkAndGetDataSunshine()
        {
            // [GIVEN] A secret with name 'x' and some main content.
            var objectMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(DefaultSecretName));
            if (objectMetadata == null)
            {
                throw new AssertionException($"Metadata for secret is null.");
            }

            var mainContent = objectMetadata.Content.FirstOrDefault(c => c.IsMain);
            if (mainContent == null)
            {
                throw new AssertionException($"Main content for secret is null.");
            }

            // [GIVEN] An attachment is uploaded.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var request2 = TestFactory.CreateHttpRequestData("post");
            var creationInput = new ContentMetadataCreationInput()
            {
                ContentType = "image/jpeg",
                FileName = this.imageContentFileName
            };

            request2.SetBodyAsJson(creationInput);

            var response2 = await this.secretContentMeta.Run(request2, DefaultSecretName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult2 = response2 as TestHttpResponseData;

            Assert.That(okObjectResult2, Is.Not.Null);
            Assert.That(okObjectResult2?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var attachmentContent = objectMetadata.Content.FirstOrDefault(c => !c.IsMain);
            if (attachmentContent == null)
            {
                throw new AssertionException($"Attachment content for secret is null.");
            }

            var (chunkMetadata, headers) = await this.UploadDataAsync(objectMetadata.ObjectName, attachmentContent.ContentName, this.imageContent.Length);

            // [WHEN] A request is made to download secret data.
            var getRequest = TestFactory.CreateHttpRequestData("get");

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error.
            var getResponse = await this.secretStream.Run(getRequest, DefaultSecretName, attachmentContent.ContentName, chunkMetadata.ChunkName, claimsPrincipal, this.logger);
            var fileStreamResult = getResponse as TestHttpResponseData;

            var dataStream = fileStreamResult?.Body;
            if (dataStream == null)
            {
                throw new AssertionException($"Data stream for secret is null.");
            }

            dataStream.Seek(0, SeekOrigin.Begin); // should only be needed in tests

            var dataBuffer = new byte[this.imageContent.Length * 2];
            var bytesRead = await dataStream.ReadAsync(dataBuffer, 0, dataBuffer.Length);

            Assert.That(bytesRead, Is.EqualTo(this.imageContent.Length));
            for (var pos = 0; pos < this.imageContent.Length; pos++)
            {
                Assert.That(dataBuffer[pos], Is.EqualTo(this.imageContent[pos]));
            }
        }

        [Test]
        public async Task ChunksClearedOnAccessTicketTimeout()
        {
            // [GIVEN] A secret with name 'x' and started main content upload.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            var objectMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(DefaultSecretName));
            if (objectMetadata == null)
            {
                throw new AssertionException($"Metadata for secret is null.");
            }

            var mainContent = objectMetadata.Content.FirstOrDefault(c => c.IsMain);
            if (mainContent == null)
            {
                throw new AssertionException($"Main content for secret is null.");
            }

            var request1 = TestFactory.CreateHttpRequestData("post");
            request1.Headers.Add(SafeExchangeSecretStream.OperationTypeHeaderName, SafeExchangeSecretStream.InterimOperationType);
            request1.SetBodyAsStream(new ByteArrayContent(this.imageContent_part_1).ReadAsStream());

            var response1 = await this.secretStream.Run(request1, DefaultSecretName, mainContent.ContentName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult1 = response1 as TestHttpResponseData;

            Assert.That(okObjectResult1, Is.Not.Null);
            Assert.That(okObjectResult1?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var blob = this.blobHelper as TestBlobHelper;
            Assert.That(blob?.Blobs.ContainsKey($"{mainContent.ContentName}-{0:00000000}"), Is.True);

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromMinutes(15);

            // [WHEN] A request is made to download secret data after access tikcet expiration.
            var getRequest = TestFactory.CreateHttpRequestData("get");

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error.
            var getResponse = await this.secretStream.Run(getRequest, DefaultSecretName, mainContent.ContentName, $"{0:00000000}", claimsPrincipal, this.logger);
            var unprocessableEntityObjectResult = getResponse as TestHttpResponseData;
            if (unprocessableEntityObjectResult == null)
            {
                throw new AssertionException($"UnprocessableEntityObjectResult result is null.");
            }

            Assert.That(unprocessableEntityObjectResult, Is.Not.Null);
            Assert.That(unprocessableEntityObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));

            var responseResult = unprocessableEntityObjectResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("unprocessable"));
            Assert.That(responseResult?.Error, Is.Not.Null);
            Assert.That(responseResult?.Result, Is.Null);

            Assert.That(blob?.Blobs.ContainsKey($"{mainContent.ContentName}-{0:00000000}"), Is.False);
        }

        [Test]
        public async Task AddChunkAndDropAfterwards()
        {
            // [GIVEN] A secret with name 'x' and uploaded main content.
            var objectMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(DefaultSecretName));
            if (objectMetadata == null)
            {
                throw new AssertionException($"Metadata for secret is null.");
            }

            var mainContent = objectMetadata.Content.FirstOrDefault(c => c.IsMain);
            if (mainContent == null)
            {
                throw new AssertionException($"Main content for secret is null.");
            }

            var expectedLength = 1953; // main content is processed by HtmlSanitizer and it's length changes.
            var (chunkMetadata, headers) = await this.UploadDataAsync(objectMetadata.ObjectName, mainContent.ContentName, expectedLength);

            // [WHEN] A request is made to drop secret content.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var dropRequest = TestFactory.CreateHttpRequestData("patch");

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error. Chunk list is empty.
            var dropResponse = await this.secretContentMeta.RunDrop(dropRequest, DefaultSecretName, mainContent.ContentName, claimsPrincipal, this.logger);
            var okObjectResult = dropResponse as TestHttpResponseData;

            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectResult?.ReadBodyAsJson<BaseResponseObject<ContentMetadataOutput>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            var metadata = responseResult?.Result;
            Assert.That(metadata?.Chunks.Count, Is.EqualTo(0));
        }

        private async Task<(ChunkCreationOutput, HttpHeadersCollection?)> UploadDataAsync(string secretName, string contentName, int expectedContentLength)
        {
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var request = TestFactory.CreateHttpRequestData("post");
            request.SetBodyAsStream(new ByteArrayContent(this.imageContent).ReadAsStream());

            var response = await this.secretStream.Run(request, secretName, contentName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult = response as TestHttpResponseData;

            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectResult?.ReadBodyAsJson<BaseResponseObject<ChunkCreationOutput>>();
            Assert.That(responseResult, Is.Not.Null);
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            var chunkMetadata = responseResult?.Result;
            if (chunkMetadata == null)
            {
                throw new AssertionException($"Chunk metadata is null.");
            }

            Assert.That(chunkMetadata.ChunkName, Is.EqualTo($"{contentName}-{0:00000000}"));
            Assert.That(chunkMetadata.Length, Is.EqualTo(expectedContentLength));
            Assert.That(string.IsNullOrEmpty(chunkMetadata.AccessTicket), Is.True);

            return (chunkMetadata, okObjectResult?.Headers);
        }

        [Test]
        public async Task AddTwoChunksSunshine()
        {
            // [GIVEN] A secret with name 'x'.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            var objectMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(DefaultSecretName));
            var mainContent = objectMetadata?.Content.FirstOrDefault(c => c.IsMain);
            if (mainContent == null)
            {
                throw new AssertionException($"Main content for secret is null.");
            }

            // [GIVEN] First part of main content data was uploaded.
            var request1 = TestFactory.CreateHttpRequestData("post");
            request1.Headers.Add(SafeExchangeSecretStream.OperationTypeHeaderName, SafeExchangeSecretStream.InterimOperationType);
            request1.SetBodyAsStream(new ByteArrayContent(this.mainContent_part_1).ReadAsStream());

            var response1 = await this.secretStream.Run(request1, DefaultSecretName, mainContent.ContentName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult1 = response1 as TestHttpResponseData;

            Assert.That(okObjectResult1, Is.Not.Null);
            Assert.That(okObjectResult1?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult1 = okObjectResult1?.ReadBodyAsJson<BaseResponseObject<ChunkCreationOutput>>();
            Assert.That(responseResult1, Is.Not.Null);
            Assert.That(responseResult1?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult1?.Error, Is.Null);

            var chunkMetadata1 = responseResult1?.Result;
            Assert.That(chunkMetadata1, Is.Not.Null);
            Assert.That(chunkMetadata1?.ChunkName, Is.EqualTo($"{mainContent.ContentName}-{0:00000000}"));
            Assert.That(chunkMetadata1?.Length, Is.EqualTo(this.mainContent_part_1.Length));

            // [GIVEN] After first upload an access ticket was returned.
            var accessTicket1 = chunkMetadata1?.AccessTicket;
            Assert.That(string.IsNullOrEmpty(accessTicket1), Is.False);

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromMinutes(1);

            // [WHEN] A request is made to upload second and final part of content data.
            var request2 = TestFactory.CreateHttpRequestData("post");
            request2.Headers.Add(SafeExchangeSecretStream.AccessTicketHeaderName, accessTicket1);

            request2.SetBodyAsStream(new ByteArrayContent(this.mainContent_part_2).ReadAsStream());

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error.
            var response2 = await this.secretStream.Run(request2, DefaultSecretName, mainContent.ContentName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult2 = response2 as TestHttpResponseData;

            Assert.That(okObjectResult2, Is.Not.Null);
            Assert.That(okObjectResult2?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            response2.Headers.TryGetValues(SafeExchangeSecretStream.AccessTicketHeaderName, out var ticketResponseHeader2);
            var accessTicket2 = ticketResponseHeader2?.FirstOrDefault();
            Assert.That(string.IsNullOrEmpty(accessTicket2), Is.True);

            var responseResult2 = okObjectResult2?.ReadBodyAsJson<BaseResponseObject<ChunkCreationOutput>>();
            Assert.That(responseResult2, Is.Not.Null);
            Assert.That(responseResult2?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult2?.Error, Is.Null);

            var chunkMetadata2 = responseResult2?.Result;
            Assert.That(chunkMetadata2, Is.Not.Null);
            Assert.That(chunkMetadata2?.ChunkName, Is.EqualTo($"{mainContent.ContentName}-{1:00000000}"));
            Assert.That(chunkMetadata2?.Length, Is.EqualTo(this.mainContent_part_2.Length));
            Assert.That(string.IsNullOrEmpty(chunkMetadata2?.AccessTicket), Is.True);

            // [THEN] Two blobs are created with uploaded data
            Assert.That(await this.blobHelper.BlobExistsAsync($"{mainContent.ContentName}-{"00000000"}"), Is.True);
            Assert.That(await this.blobHelper.BlobExistsAsync($"{mainContent.ContentName}-{"00000001"}"), Is.True);
        }

        [Test]
        public async Task AddTwoContentChunksSunshine()
        {
            // [GIVEN] A secret with name 'x'.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            var objectMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(DefaultSecretName));
            var mainContent = objectMetadata?.Content.FirstOrDefault(c => c.IsMain);
            if (mainContent == null)
            {
                throw new AssertionException($"Main content for secret is null.");
            }

            // [GIVEN] First part of main content data was uploaded.
            var request1 = TestFactory.CreateHttpRequestData("post");
            request1.Headers.Add(SafeExchangeSecretStream.OperationTypeHeaderName, SafeExchangeSecretStream.InterimOperationType);
            request1.SetBodyAsStream(new ByteArrayContent(this.imageContent).ReadAsStream());

            var response1 = await this.secretStream.Run(request1, DefaultSecretName, mainContent.ContentName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult1 = response1 as TestHttpResponseData;

            Assert.That(okObjectResult1, Is.Not.Null);
            Assert.That(okObjectResult1?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // [GIVEN] A second content is added to the secret.
            var request2 = TestFactory.CreateHttpRequestData("post");
            var creationInput = new ContentMetadataCreationInput()
            {
                ContentType = "image/jpeg",
                FileName = this.imageContentFileName
            };

            request2.SetBodyAsJson(creationInput);

            var response2 = await this.secretContentMeta.Run(request2, DefaultSecretName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult2 = response2 as TestHttpResponseData;

            Assert.That(okObjectResult2, Is.Not.Null);
            Assert.That(okObjectResult2?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var content2 = okObjectResult2?.ReadBodyAsJson<BaseResponseObject<ContentMetadataOutput>>();
            var content2Name = content2?.Result?.ContentName;
            Assert.That(string.IsNullOrEmpty(content2Name), Is.False);

            // [WHEN] A request is made to upload data for second content.
            var request3 = TestFactory.CreateHttpRequestData("post");
            request3.SetBodyAsStream(new ByteArrayContent(this.imageContent).ReadAsStream());

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error.
            var response3 = await this.secretStream.Run(request3, DefaultSecretName, content2Name, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult3 = response3 as TestHttpResponseData;

            Assert.That(okObjectResult3, Is.Not.Null);
            Assert.That(okObjectResult3?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult3 = okObjectResult3?.ReadBodyAsJson<BaseResponseObject<ChunkCreationOutput>>();
            Assert.That(responseResult3, Is.Not.Null);
            Assert.That(responseResult3?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult3?.Error, Is.Null);

            var chunkMetadata2 = responseResult3?.Result;
            Assert.That(chunkMetadata2, Is.Not.Null);
            Assert.That(chunkMetadata2?.ChunkName, Is.EqualTo($"{content2Name}-{0:00000000}"));
            Assert.That(chunkMetadata2?.Length, Is.EqualTo(this.imageContent.Length));
            Assert.That(string.IsNullOrEmpty(chunkMetadata2?.AccessTicket), Is.True);

            Assert.That(await this.blobHelper.BlobExistsAsync($"{mainContent.ContentName}-{0:00000000}"), Is.True);
            Assert.That(await this.blobHelper.BlobExistsAsync($"{content2Name}-{0:00000000}"), Is.True);
        }

        [Test]
        public async Task CannotUploadInBetweenTwoChunks()
        {
            // [GIVEN] A secret with name 'x'.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            var objectMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(DefaultSecretName));
            var mainContent = objectMetadata?.Content.FirstOrDefault(c => c.IsMain);
            if (mainContent == null)
            {
                throw new AssertionException($"Main content for secret is null.");
            }

            // [GIVEN] First part of main content data was uploaded.
            var request1 = TestFactory.CreateHttpRequestData("post");
            request1.Headers.Add(SafeExchangeSecretStream.OperationTypeHeaderName, SafeExchangeSecretStream.InterimOperationType);
            request1.SetBodyAsStream(new ByteArrayContent(this.imageContent_part_1).ReadAsStream());

            var response1 = await this.secretStream.Run(request1, DefaultSecretName, mainContent.ContentName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult1 = response1 as TestHttpResponseData;

            Assert.That(okObjectResult1, Is.Not.Null);
            Assert.That(okObjectResult1?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult1 = okObjectResult1?.ReadBodyAsJson<BaseResponseObject<ChunkCreationOutput>>();
            Assert.That(responseResult1, Is.Not.Null);
            Assert.That(responseResult1?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult1?.Error, Is.Null);

            // [GIVEN] After first upload an access ticket was returned.
            var accessTicket1 = responseResult1?.Result?.AccessTicket;
            Assert.That(string.IsNullOrEmpty(accessTicket1), Is.False);

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromMinutes(1);

            // [WHEN] Another user is trying to upload content data.
            var request2 = TestFactory.CreateHttpRequestData("post");
            request2.SetBodyAsStream(new ByteArrayContent(this.imageContent_part_1).ReadAsStream());

            // [THEN] UnprocessableEntityObjectResult is returned with Status = 'unprocessable', null Result and non-null Error.
            var response2 = await this.secretStream.Run(request2, DefaultSecretName, mainContent.ContentName, string.Empty, claimsPrincipal, this.logger);
            var unprocessableEntityObjectResult = response2 as TestHttpResponseData;

            Assert.That(unprocessableEntityObjectResult, Is.Not.Null);
            Assert.That(unprocessableEntityObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));

            var responseResult2 = unprocessableEntityObjectResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(unprocessableEntityObjectResult, Is.Not.Null);
            Assert.That(responseResult2?.Status, Is.EqualTo("unprocessable"));
            Assert.That(responseResult2?.Error, Is.Not.Null);
            Assert.That(responseResult2?.Result, Is.Null);
        }

        [Test]
        public async Task CannotDownloadInBetweenTwoChunks()
        {
            // [GIVEN] A secret with name 'x'.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            var objectMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(DefaultSecretName));
            var mainContent = objectMetadata?.Content.FirstOrDefault(c => c.IsMain);
            if (mainContent == null)
            {
                throw new AssertionException($"Main content for secret is null.");
            }

            // [GIVEN] First part of main content data was uploaded.
            var request1 = TestFactory.CreateHttpRequestData("post");
            request1.Headers.Add(SafeExchangeSecretStream.OperationTypeHeaderName, SafeExchangeSecretStream.InterimOperationType);
            request1.SetBodyAsStream(new ByteArrayContent(this.imageContent_part_1).ReadAsStream());

            var response1 = await this.secretStream.Run(request1, DefaultSecretName, mainContent.ContentName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult1 = response1 as TestHttpResponseData;

            Assert.That(okObjectResult1, Is.Not.Null);
            Assert.That(okObjectResult1?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult1 = okObjectResult1?.ReadBodyAsJson<BaseResponseObject<ChunkCreationOutput>>();
            Assert.That(responseResult1, Is.Not.Null);
            Assert.That(responseResult1?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult1?.Error, Is.Null);

            // [GIVEN] After first upload an access ticket was returned.
            var accessTicket1 = responseResult1?.Result?.AccessTicket;
            Assert.That(string.IsNullOrEmpty(accessTicket1), Is.False);

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromMinutes(1);

            // [WHEN] Another user is trying to download content data.
            var request2 = TestFactory.CreateHttpRequestData("get");

            // [THEN] UnprocessableEntityObjectResult is returned with Status = 'unprocessable', null Result and non-null Error.
            var response2 = await this.secretStream.Run(request2, DefaultSecretName, mainContent.ContentName, $"{0:00000000}", claimsPrincipal, this.logger);
            var unprocessableEntityObjectResult = response2 as TestHttpResponseData;

            Assert.That(unprocessableEntityObjectResult, Is.Not.Null);
            Assert.That(unprocessableEntityObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));

            var responseResult2 = unprocessableEntityObjectResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(unprocessableEntityObjectResult, Is.Not.Null);
            Assert.That(responseResult2?.Status, Is.EqualTo("unprocessable"));
            Assert.That(responseResult2?.Error, Is.Not.Null);
            Assert.That(responseResult2?.Result, Is.Null);
        }

        [Test]
        public async Task CannotDropInBetweenTwoChunks()
        {
            // [GIVEN] A secret with name 'x'.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            var objectMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(DefaultSecretName));
            var mainContent = objectMetadata?.Content.FirstOrDefault(c => c.IsMain);
            if (mainContent == null)
            {
                throw new AssertionException($"Main content for secret is null.");
            }

            // [GIVEN] First part of main content data was uploaded.
            var request1 = TestFactory.CreateHttpRequestData("post");
            request1.Headers.Add(SafeExchangeSecretStream.OperationTypeHeaderName, SafeExchangeSecretStream.InterimOperationType);
            request1.SetBodyAsStream(new ByteArrayContent(this.imageContent_part_1).ReadAsStream());

            var response1 = await this.secretStream.Run(request1, DefaultSecretName, mainContent.ContentName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult1 = response1 as TestHttpResponseData;

            Assert.That(okObjectResult1, Is.Not.Null);
            Assert.That(okObjectResult1?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult1 = okObjectResult1?.ReadBodyAsJson<BaseResponseObject<ChunkCreationOutput>>();
            Assert.That(responseResult1, Is.Not.Null);
            Assert.That(responseResult1?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult1?.Error, Is.Null);

            // [GIVEN] After first upload an access ticket was returned.
            var accessTicket1 = responseResult1?.Result?.AccessTicket;
            Assert.That(string.IsNullOrEmpty(accessTicket1), Is.False);

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromMinutes(1);

            // [WHEN] Another user is trying to drop content data.
            var request2 = TestFactory.CreateHttpRequestData("patch");

            // [THEN] UnprocessableEntityObjectResult is returned with Status = 'unprocessable', null Result and non-null Error.
            var response2 = await this.secretContentMeta.RunDrop(request2, DefaultSecretName, mainContent.ContentName, claimsPrincipal, this.logger);
            var unprocessableEntityObjectResult = response2 as TestHttpResponseData;

            Assert.That(unprocessableEntityObjectResult, Is.Not.Null);
            Assert.That(unprocessableEntityObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));

            var responseResult2 = unprocessableEntityObjectResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(unprocessableEntityObjectResult, Is.Not.Null);
            Assert.That(responseResult2?.Status, Is.EqualTo("unprocessable"));
            Assert.That(responseResult2?.Error, Is.Not.Null);
            Assert.That(responseResult2?.Result, Is.Null);
        }

        [Test]
        public async Task CanDropAfterAccessTicketTimeout()
        {
            // [GIVEN] A secret with name 'x'.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            var objectMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(DefaultSecretName));
            var mainContent = objectMetadata?.Content.FirstOrDefault(c => c.IsMain);
            if (mainContent == null)
            {
                throw new AssertionException($"Main content for secret is null.");
            }

            // [GIVEN] First part of main content data was uploaded.
            var request1 = TestFactory.CreateHttpRequestData("post");
            request1.Headers.Add(SafeExchangeSecretStream.OperationTypeHeaderName, SafeExchangeSecretStream.InterimOperationType);
            request1.SetBodyAsStream(new ByteArrayContent(this.imageContent_part_1).ReadAsStream());

            var response1 = await this.secretStream.Run(request1, DefaultSecretName, mainContent.ContentName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult1 = response1 as TestHttpResponseData;

            Assert.That(okObjectResult1, Is.Not.Null);
            Assert.That(okObjectResult1?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult1 = okObjectResult1?.ReadBodyAsJson<BaseResponseObject<ChunkCreationOutput>>();
            Assert.That(responseResult1, Is.Not.Null);
            Assert.That(responseResult1?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult1?.Error, Is.Null);

            // [GIVEN] After first upload an access ticket was returned.
            var accessTicket1 = responseResult1?.Result?.AccessTicket;
            Assert.That(string.IsNullOrEmpty(accessTicket1), Is.False);

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromMinutes(15);

            // [WHEN] Another user is trying to drop content data after access ticket timeout.
            var request2 = TestFactory.CreateHttpRequestData("patch");

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error.
            var response2 = await this.secretContentMeta.RunDrop(request2, DefaultSecretName, mainContent.ContentName, claimsPrincipal, this.logger);
            var okObjectResult2 = response2 as TestHttpResponseData;

            Assert.That(okObjectResult2, Is.Not.Null);
            Assert.That(okObjectResult2?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult2 = okObjectResult2?.ReadBodyAsJson<BaseResponseObject<ContentMetadataOutput>>();
            Assert.That(responseResult2, Is.Not.Null);
            Assert.That(responseResult2?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult2?.Error, Is.Null);

            var chunks = responseResult2?.Result?.Chunks;
            Assert.That(chunks?.Count, Is.EqualTo(0));
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
