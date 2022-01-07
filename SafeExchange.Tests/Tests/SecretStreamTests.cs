/// <summary>
/// SecretStreamTests
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

        private ClaimsIdentity firstIdentity;
        private ClaimsIdentity secondIdentity;

        private string imageContentFileName = "testimage_small.jpg";
        private byte[] imageContent;

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

            var imageContentLengthPart1 = this.imageContent.Length / 2;
            var imageContentLengthPart2 = this.imageContent.Length - imageContentLengthPart1;

            this.imageContent_part_1 = new byte[imageContentLengthPart1];
            Buffer.BlockCopy(this.imageContent, 0, this.imageContent_part_1, 0, imageContentLengthPart1);

            this.imageContent_part_2 = new byte[imageContentLengthPart2];
            Buffer.BlockCopy(this.imageContent, imageContentLengthPart1, this.imageContent_part_2, 0, imageContentLengthPart2);
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
            this.dbContext.NotificationSubscriptions.RemoveRange(this.dbContext.NotificationSubscriptions.ToList());
            this.dbContext.GroupDictionary.RemoveRange(this.dbContext.GroupDictionary.ToList());
            this.dbContext.SaveChanges();
        }

        [Test]
        public async Task CannotAddChunkIfSecretNotExists()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request is made to upload data to a secret that not exists.
            var request = TestFactory.CreateHttpRequest("post");
            request.Body = new ByteArrayContent(this.imageContent).ReadAsStream();

            var response = await this.secretStream.Run(request, "inexistent", "default", string.Empty, claimsPrincipal, this.logger);
            var notFoundObjectResult = response as NotFoundObjectResult;

            Assert.IsNotNull(notFoundObjectResult);
            Assert.AreEqual(404, notFoundObjectResult?.StatusCode);

            var responseResult = notFoundObjectResult?.Value as BaseResponseObject<object>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("not_found", responseResult?.Status);
            Assert.IsNotNull(responseResult?.Error);
            Assert.IsNull(responseResult?.Result);
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
            var request = TestFactory.CreateHttpRequest("post");
            request.Body = new ByteArrayContent(this.imageContent).ReadAsStream();

            var response = await this.secretStream.Run(request, objectMetadata.ObjectName, "inexistent", string.Empty, claimsPrincipal, this.logger);
            var notFoundObjectResult = response as NotFoundObjectResult;

            Assert.IsNotNull(notFoundObjectResult);
            Assert.AreEqual(404, notFoundObjectResult?.StatusCode);

            var responseResult = notFoundObjectResult?.Value as BaseResponseObject<object>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("not_found", responseResult?.Status);
            Assert.IsNotNull(responseResult?.Error);
            Assert.IsNull(responseResult?.Result);
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
            var request = TestFactory.CreateHttpRequest("post");
            request.Body = new ByteArrayContent(this.imageContent).ReadAsStream();

            var response = await this.secretStream.Run(request, DefaultSecretName, mainContent.ContentName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult = response as OkObjectResult;

            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            var responseResult = okObjectResult?.Value as BaseResponseObject<ChunkCreationOutput>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);

            var chunkMetadata = responseResult?.Result;
            Assert.IsNotNull(chunkMetadata);
            Assert.AreEqual($"{mainContent.ContentName}-{0:00000000}", chunkMetadata?.ChunkName);
            Assert.AreEqual(this.imageContent.Length, chunkMetadata?.Length);
            Assert.IsTrue(string.IsNullOrEmpty(chunkMetadata?.AccessTicket));

            // [THEN] A chunk is created with uploaded data.
            var existingChunkData = await this.blobHelper.DownloadAndDecryptBlobAsync(chunkMetadata?.ChunkName);
            Assert.AreEqual(this.imageContent.Length, existingChunkData.Length);

            var existingChunkBytes = new byte[existingChunkData.Length];
            existingChunkData.Read(existingChunkBytes, 0, (int)existingChunkData.Length);

            for (var pos = 0; pos < existingChunkBytes.Length; pos++)
            {
                Assert.AreEqual(this.imageContent[pos], existingChunkBytes[pos]);
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
            var request = TestFactory.CreateHttpRequest("post");
            request.Body = new ByteArrayContent(this.imageContent).ReadAsStream();

            var response = await this.secretStream.Run(request, DefaultSecretName, mainContent.ContentName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult = response as OkObjectResult;

            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            var responseResult = okObjectResult?.Value as BaseResponseObject<ChunkCreationOutput>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);

            var chunkMetadata = responseResult?.Result;
            Assert.IsNotNull(chunkMetadata);
            Assert.AreEqual($"{mainContent.ContentName}-{0:00000000}", chunkMetadata?.ChunkName);
            Assert.AreEqual(this.imageContent.Length, chunkMetadata?.Length);

            var ticketResponseHeader = request.HttpContext.Response.Headers[SafeExchangeSecretStream.AccessTicketHeaderName];
            var accessTicket = ticketResponseHeader.FirstOrDefault();
            Assert.IsTrue(string.IsNullOrEmpty(accessTicket));

            // [WHEN] A request is made to get secret metadata.
            var getRequest = TestFactory.CreateHttpRequest("get");

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error. Newly added chunk is present.
            var getResponse = await this.secretMeta.Run(getRequest, DefaultSecretName, claimsPrincipal, this.logger);
            var okObjectGetResult = getResponse as OkObjectResult;

            Assert.IsNotNull(okObjectGetResult);
            Assert.AreEqual(200, okObjectGetResult?.StatusCode);

            var responseGetResult = okObjectGetResult?.Value as BaseResponseObject<ObjectMetadataOutput>;
            Assert.IsNotNull(responseGetResult);
            Assert.AreEqual("ok", responseGetResult?.Status);
            Assert.IsNull(responseGetResult?.Error);

            var metadata = responseGetResult?.Result;
            var content = responseGetResult?.Result?.Content;
            if (content == null || content.Count == 0)
            {
                throw new AssertionException("Content is null.");
            }

            var firstContent = content.First();
            var chunks = firstContent.Chunks;
            if (chunks == null || chunks.Count == 0)
            {
                throw new AssertionException("Chunks is null.");
            }

            Assert.AreEqual(1, chunks.Count);
            var firstChunk = chunks.First();
            Assert.AreEqual($"{firstContent.ContentName}-{0:00000000}", firstChunk.ChunkName);
            Assert.AreEqual(this.imageContent.Length, firstChunk.Length);

            var existingChunkData = await this.blobHelper.DownloadAndDecryptBlobAsync($"{firstContent.ContentName}-{"00000000"}");
            Assert.AreEqual(this.imageContent.Length, existingChunkData.Length);

            var existingChunkBytes = new byte[existingChunkData.Length];
            existingChunkData.Read(existingChunkBytes, 0, (int)existingChunkData.Length);

            for (var pos = 0; pos < existingChunkBytes.Length; pos++)
            {
                Assert.AreEqual(this.imageContent[pos], existingChunkBytes[pos]);
            }
        }

        [Test]
        public async Task AddChunkAndGetDataSunshine()
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

            var chunkMetadata = await this.UploadDataAsync(objectMetadata.ObjectName, mainContent.ContentName);

            // [WHEN] A request is made to download secret data.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var getRequest = TestFactory.CreateHttpRequest("get");

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error.
            var getResponse = await this.secretStream.Run(getRequest, DefaultSecretName, mainContent.ContentName, chunkMetadata.ChunkName, claimsPrincipal, this.logger);
            var fileStreamResult = getResponse as FileStreamResult;

            var dataStream = fileStreamResult?.FileStream;
            if (dataStream == null)
            {
                throw new AssertionException($"Data stream for secret is null.");
            }

            var dataBuffer = new byte[this.imageContent.Length * 2];
            var bytesRead = await dataStream.ReadAsync(dataBuffer, 0, dataBuffer.Length);

            Assert.AreEqual(this.imageContent.Length, bytesRead);
            for (var pos = 0; pos < this.imageContent.Length; pos++)
            {
                Assert.AreEqual(this.imageContent[pos], dataBuffer[pos]);
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

            var request1 = TestFactory.CreateHttpRequest("post");
            request1.Headers[SafeExchangeSecretStream.OperationTypeHeaderName] = SafeExchangeSecretStream.InterimOperationType;
            request1.Body = new ByteArrayContent(this.imageContent_part_1).ReadAsStream();

            var response1 = await this.secretStream.Run(request1, DefaultSecretName, mainContent.ContentName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult1 = response1 as OkObjectResult;

            Assert.IsNotNull(okObjectResult1);
            Assert.AreEqual(200, okObjectResult1?.StatusCode);

            var blob = this.blobHelper as TestBlobHelper;
            Assert.IsTrue(blob?.Blobs.ContainsKey($"{mainContent.ContentName}-{0:00000000}"));

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromMinutes(15);

            // [WHEN] A request is made to download secret data after access tikcet expiration.
            var getRequest = TestFactory.CreateHttpRequest("get");

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error.
            var getResponse = await this.secretStream.Run(getRequest, DefaultSecretName, mainContent.ContentName, $"{0:00000000}", claimsPrincipal, this.logger);
            var unprocessableEntityObjectResult = getResponse as UnprocessableEntityObjectResult;
            if (unprocessableEntityObjectResult == null)
            {
                throw new AssertionException($"UnprocessableEntityObjectResult result is null.");
            }

            Assert.IsNotNull(unprocessableEntityObjectResult);
            Assert.AreEqual(422, unprocessableEntityObjectResult?.StatusCode);

            var responseResult = unprocessableEntityObjectResult?.Value as BaseResponseObject<object>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("unprocessable", responseResult?.Status);
            Assert.IsNotNull(responseResult?.Error);
            Assert.IsNull(responseResult?.Result);

            Assert.IsFalse(blob?.Blobs.ContainsKey($"{mainContent.ContentName}-{0:00000000}"));
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

            var chunkMetadata = await this.UploadDataAsync(objectMetadata.ObjectName, mainContent.ContentName);

            // [WHEN] A request is made to drop secret content.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var dropRequest = TestFactory.CreateHttpRequest("patch");

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error. Chunk list is empty.
            var dropResponse = await this.secretContentMeta.RunDrop(dropRequest, DefaultSecretName, mainContent.ContentName, claimsPrincipal, this.logger);
            var okObjectResult = dropResponse as OkObjectResult;

            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            var responseResult = okObjectResult?.Value as BaseResponseObject<ContentMetadataOutput>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);

            var metadata = responseResult?.Result;
            Assert.AreEqual(0, metadata?.Chunks.Count);
        }

        private async Task<ChunkCreationOutput> UploadDataAsync(string secretName, string contentName)
        {
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var request = TestFactory.CreateHttpRequest("post");
            request.Body = new ByteArrayContent(this.imageContent).ReadAsStream();

            var response = await this.secretStream.Run(request, secretName, contentName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult = response as OkObjectResult;

            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            var responseResult = okObjectResult?.Value as BaseResponseObject<ChunkCreationOutput>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);

            var chunkMetadata = responseResult?.Result;
            if (chunkMetadata == null)
            {
                throw new AssertionException($"Chunk metadata is null.");
            }

            Assert.AreEqual($"{contentName}-{0:00000000}", chunkMetadata.ChunkName);
            Assert.AreEqual(this.imageContent.Length, chunkMetadata.Length);
            Assert.IsTrue(string.IsNullOrEmpty(chunkMetadata.AccessTicket));

            return chunkMetadata;
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
            var request1 = TestFactory.CreateHttpRequest("post");
            request1.Headers[SafeExchangeSecretStream.OperationTypeHeaderName] = SafeExchangeSecretStream.InterimOperationType;
            request1.Body = new ByteArrayContent(this.imageContent_part_1).ReadAsStream();

            var response1 = await this.secretStream.Run(request1, DefaultSecretName, mainContent.ContentName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult1 = response1 as OkObjectResult;

            Assert.IsNotNull(okObjectResult1);
            Assert.AreEqual(200, okObjectResult1?.StatusCode);

            var responseResult1 = okObjectResult1?.Value as BaseResponseObject<ChunkCreationOutput>;
            Assert.IsNotNull(responseResult1);
            Assert.AreEqual("ok", responseResult1?.Status);
            Assert.IsNull(responseResult1?.Error);

            var chunkMetadata1 = responseResult1?.Result;
            Assert.IsNotNull(chunkMetadata1);
            Assert.AreEqual($"{mainContent.ContentName}-{0:00000000}", chunkMetadata1?.ChunkName);
            Assert.AreEqual(this.imageContent_part_1.Length, chunkMetadata1?.Length);

            // [GIVEN] After first upload an access ticket was returned.
            var accessTicket1 = chunkMetadata1?.AccessTicket;
            Assert.IsFalse(string.IsNullOrEmpty(accessTicket1));

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromMinutes(1);

            // [WHEN] A request is made to upload second and final part of content data.
            var request2 = TestFactory.CreateHttpRequest("post");
            request2.Headers[SafeExchangeSecretStream.AccessTicketHeaderName] = accessTicket1;

            request2.Body = new ByteArrayContent(this.imageContent_part_2).ReadAsStream();

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error.
            var response2 = await this.secretStream.Run(request2, DefaultSecretName, mainContent.ContentName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult2 = response2 as OkObjectResult;

            Assert.IsNotNull(okObjectResult2);
            Assert.AreEqual(200, okObjectResult2?.StatusCode);

            var ticketResponseHeader2 = request2.HttpContext.Response.Headers[SafeExchangeSecretStream.AccessTicketHeaderName];
            var accessTicket2 = ticketResponseHeader2.FirstOrDefault();
            Assert.IsTrue(string.IsNullOrEmpty(accessTicket2));

            var responseResult2 = okObjectResult2?.Value as BaseResponseObject<ChunkCreationOutput>;
            Assert.IsNotNull(responseResult2);
            Assert.AreEqual("ok", responseResult2?.Status);
            Assert.IsNull(responseResult2?.Error);

            var chunkMetadata2 = responseResult2?.Result;
            Assert.IsNotNull(chunkMetadata2);
            Assert.AreEqual($"{mainContent.ContentName}-{1:00000000}", chunkMetadata2?.ChunkName);
            Assert.AreEqual(this.imageContent_part_2.Length, chunkMetadata2?.Length);
            Assert.IsTrue(string.IsNullOrEmpty(chunkMetadata2?.AccessTicket));

            // [THEN] Two blobs are created with uploaded data
            Assert.IsTrue(await this.blobHelper.BlobExistsAsync($"{mainContent.ContentName}-{"00000000"}"));
            Assert.IsTrue(await this.blobHelper.BlobExistsAsync($"{mainContent.ContentName}-{"00000001"}"));
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
            var request1 = TestFactory.CreateHttpRequest("post");
            request1.Headers[SafeExchangeSecretStream.OperationTypeHeaderName] = SafeExchangeSecretStream.InterimOperationType;
            request1.Body = new ByteArrayContent(this.imageContent).ReadAsStream();

            var response1 = await this.secretStream.Run(request1, DefaultSecretName, mainContent.ContentName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult1 = response1 as OkObjectResult;

            Assert.IsNotNull(okObjectResult1);
            Assert.AreEqual(200, okObjectResult1?.StatusCode);

            // [GIVEN] A second content is added to the secret.
            var request2 = TestFactory.CreateHttpRequest("post");
            var creationInput = new ContentMetadataCreationInput()
            {
                ContentType = "image/jpeg",
                FileName = this.imageContentFileName
            };

            request2.Body = new StringContent(DefaultJsonSerializer.Serialize(creationInput)).ReadAsStream();

            var response2 = await this.secretContentMeta.Run(request2, DefaultSecretName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult2 = response2 as OkObjectResult;

            Assert.IsNotNull(okObjectResult2);
            Assert.AreEqual(200, okObjectResult2?.StatusCode);

            var content2 = okObjectResult2?.Value as BaseResponseObject<ContentMetadataOutput>;
            var content2Name = content2?.Result?.ContentName;
            Assert.IsFalse(string.IsNullOrEmpty(content2Name));

            // [WHEN] A request is made to upload data for second content.
            var request3 = TestFactory.CreateHttpRequest("post");
            request3.Body = new ByteArrayContent(this.imageContent).ReadAsStream();

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error.
            var response3 = await this.secretStream.Run(request3, DefaultSecretName, content2Name, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult3 = response3 as OkObjectResult;

            Assert.IsNotNull(okObjectResult3);
            Assert.AreEqual(200, okObjectResult3?.StatusCode);

            var responseResult3 = okObjectResult3?.Value as BaseResponseObject<ChunkCreationOutput>;
            Assert.IsNotNull(responseResult3);
            Assert.AreEqual("ok", responseResult3?.Status);
            Assert.IsNull(responseResult3?.Error);

            var chunkMetadata2 = responseResult3?.Result;
            Assert.IsNotNull(chunkMetadata2);
            Assert.AreEqual($"{content2Name}-{0:00000000}", chunkMetadata2?.ChunkName);
            Assert.AreEqual(this.imageContent.Length, chunkMetadata2?.Length);
            Assert.IsTrue(string.IsNullOrEmpty(chunkMetadata2?.AccessTicket));

            Assert.IsTrue(await this.blobHelper.BlobExistsAsync($"{mainContent.ContentName}-{0:00000000}"));
            Assert.IsTrue(await this.blobHelper.BlobExistsAsync($"{content2Name}-{0:00000000}"));
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
            var request1 = TestFactory.CreateHttpRequest("post");
            request1.Headers[SafeExchangeSecretStream.OperationTypeHeaderName] = SafeExchangeSecretStream.InterimOperationType;
            request1.Body = new ByteArrayContent(this.imageContent_part_1).ReadAsStream();

            var response1 = await this.secretStream.Run(request1, DefaultSecretName, mainContent.ContentName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult1 = response1 as OkObjectResult;

            Assert.IsNotNull(okObjectResult1);
            Assert.AreEqual(200, okObjectResult1?.StatusCode);

            var responseResult1 = okObjectResult1?.Value as BaseResponseObject<ChunkCreationOutput>;
            Assert.IsNotNull(responseResult1);
            Assert.AreEqual("ok", responseResult1?.Status);
            Assert.IsNull(responseResult1?.Error);

            // [GIVEN] After first upload an access ticket was returned.
            var accessTicket1 = responseResult1?.Result?.AccessTicket;
            Assert.IsFalse(string.IsNullOrEmpty(accessTicket1));

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromMinutes(1);

            // [WHEN] Another user is trying to upload content data.
            var request2 = TestFactory.CreateHttpRequest("post");
            request2.Body = new ByteArrayContent(this.imageContent_part_1).ReadAsStream();

            // [THEN] UnprocessableEntityObjectResult is returned with Status = 'unprocessable', null Result and non-null Error.
            var response2 = await this.secretStream.Run(request2, DefaultSecretName, mainContent.ContentName, string.Empty, claimsPrincipal, this.logger);
            var unprocessableEntityObjectResult = response2 as UnprocessableEntityObjectResult;

            Assert.IsNotNull(unprocessableEntityObjectResult);
            Assert.AreEqual(422, unprocessableEntityObjectResult?.StatusCode);

            var responseResult2 = unprocessableEntityObjectResult?.Value as BaseResponseObject<object>;
            Assert.IsNotNull(unprocessableEntityObjectResult);
            Assert.AreEqual("unprocessable", responseResult2?.Status);
            Assert.IsNotNull(responseResult2?.Error);
            Assert.IsNull(responseResult2?.Result);
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
            var request1 = TestFactory.CreateHttpRequest("post");
            request1.Headers[SafeExchangeSecretStream.OperationTypeHeaderName] = SafeExchangeSecretStream.InterimOperationType;
            request1.Body = new ByteArrayContent(this.imageContent_part_1).ReadAsStream();

            var response1 = await this.secretStream.Run(request1, DefaultSecretName, mainContent.ContentName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult1 = response1 as OkObjectResult;

            Assert.IsNotNull(okObjectResult1);
            Assert.AreEqual(200, okObjectResult1?.StatusCode);

            var responseResult1 = okObjectResult1?.Value as BaseResponseObject<ChunkCreationOutput>;
            Assert.IsNotNull(responseResult1);
            Assert.AreEqual("ok", responseResult1?.Status);
            Assert.IsNull(responseResult1?.Error);

            // [GIVEN] After first upload an access ticket was returned.
            var accessTicket1 = responseResult1?.Result?.AccessTicket;
            Assert.IsFalse(string.IsNullOrEmpty(accessTicket1));

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromMinutes(1);

            // [WHEN] Another user is trying to download content data.
            var request2 = TestFactory.CreateHttpRequest("get");

            // [THEN] UnprocessableEntityObjectResult is returned with Status = 'unprocessable', null Result and non-null Error.
            var response2 = await this.secretStream.Run(request2, DefaultSecretName, mainContent.ContentName, $"{0:00000000}", claimsPrincipal, this.logger);
            var unprocessableEntityObjectResult = response2 as UnprocessableEntityObjectResult;

            Assert.IsNotNull(unprocessableEntityObjectResult);
            Assert.AreEqual(422, unprocessableEntityObjectResult?.StatusCode);

            var responseResult2 = unprocessableEntityObjectResult?.Value as BaseResponseObject<object>;
            Assert.IsNotNull(unprocessableEntityObjectResult);
            Assert.AreEqual("unprocessable", responseResult2?.Status);
            Assert.IsNotNull(responseResult2?.Error);
            Assert.IsNull(responseResult2?.Result);
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
            var request1 = TestFactory.CreateHttpRequest("post");
            request1.Headers[SafeExchangeSecretStream.OperationTypeHeaderName] = SafeExchangeSecretStream.InterimOperationType;
            request1.Body = new ByteArrayContent(this.imageContent_part_1).ReadAsStream();

            var response1 = await this.secretStream.Run(request1, DefaultSecretName, mainContent.ContentName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult1 = response1 as OkObjectResult;

            Assert.IsNotNull(okObjectResult1);
            Assert.AreEqual(200, okObjectResult1?.StatusCode);

            var responseResult1 = okObjectResult1?.Value as BaseResponseObject<ChunkCreationOutput>;
            Assert.IsNotNull(responseResult1);
            Assert.AreEqual("ok", responseResult1?.Status);
            Assert.IsNull(responseResult1?.Error);

            // [GIVEN] After first upload an access ticket was returned.
            var accessTicket1 = responseResult1?.Result?.AccessTicket;
            Assert.IsFalse(string.IsNullOrEmpty(accessTicket1));

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromMinutes(1);

            // [WHEN] Another user is trying to drop content data.
            var request2 = TestFactory.CreateHttpRequest("patch");

            // [THEN] UnprocessableEntityObjectResult is returned with Status = 'unprocessable', null Result and non-null Error.
            var response2 = await this.secretContentMeta.RunDrop(request2, DefaultSecretName, mainContent.ContentName, claimsPrincipal, this.logger);
            var unprocessableEntityObjectResult = response2 as UnprocessableEntityObjectResult;

            Assert.IsNotNull(unprocessableEntityObjectResult);
            Assert.AreEqual(422, unprocessableEntityObjectResult?.StatusCode);

            var responseResult2 = unprocessableEntityObjectResult?.Value as BaseResponseObject<object>;
            Assert.IsNotNull(unprocessableEntityObjectResult);
            Assert.AreEqual("unprocessable", responseResult2?.Status);
            Assert.IsNotNull(responseResult2?.Error);
            Assert.IsNull(responseResult2?.Result);
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
            var request1 = TestFactory.CreateHttpRequest("post");
            request1.Headers[SafeExchangeSecretStream.OperationTypeHeaderName] = SafeExchangeSecretStream.InterimOperationType;
            request1.Body = new ByteArrayContent(this.imageContent_part_1).ReadAsStream();

            var response1 = await this.secretStream.Run(request1, DefaultSecretName, mainContent.ContentName, string.Empty, claimsPrincipal, this.logger);
            var okObjectResult1 = response1 as OkObjectResult;

            Assert.IsNotNull(okObjectResult1);
            Assert.AreEqual(200, okObjectResult1?.StatusCode);

            var responseResult1 = okObjectResult1?.Value as BaseResponseObject<ChunkCreationOutput>;
            Assert.IsNotNull(responseResult1);
            Assert.AreEqual("ok", responseResult1?.Status);
            Assert.IsNull(responseResult1?.Error);

            // [GIVEN] After first upload an access ticket was returned.
            var accessTicket1 = responseResult1?.Result?.AccessTicket;
            Assert.IsFalse(string.IsNullOrEmpty(accessTicket1));

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromMinutes(15);

            // [WHEN] Another user is trying to drop content data after access ticket timeout.
            var request2 = TestFactory.CreateHttpRequest("patch");

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error.
            var response2 = await this.secretContentMeta.RunDrop(request2, DefaultSecretName, mainContent.ContentName, claimsPrincipal, this.logger);
            var okObjectResult2 = response2 as OkObjectResult;

            Assert.IsNotNull(okObjectResult2);
            Assert.AreEqual(200, okObjectResult2?.StatusCode);

            var responseResult2 = okObjectResult2?.Value as BaseResponseObject<ContentMetadataOutput>;
            Assert.IsNotNull(responseResult2);
            Assert.AreEqual("ok", responseResult2?.Status);
            Assert.IsNull(responseResult2?.Error);

            var chunks = responseResult2?.Result?.Chunks;
            Assert.AreEqual(0, chunks?.Count);
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
