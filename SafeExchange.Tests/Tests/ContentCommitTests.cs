/// <summary>
/// ContentCommitTests
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
    using SafeExchange.Core.Crypto;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Model;
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
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Tasks;

    [TestFixture]
    public class ContentCommitTests
    {
        private static readonly string DefaultSecretName = "defaultsecret";

        private ILogger logger;

        private SafeExchangeSecretMeta secretMeta;
        private SafeExchangeSecretContentMeta secretContentMeta;
        private SafeExchangeContentCommit contentCommit;

        private IConfiguration testConfiguration;

        private SafeExchangeDbContext dbContext;

        private ITokenHelper tokenHelper;

        private TestGraphDataProvider graphDataProvider;

        private GlobalFilters globalFilters;

        private IBlobHelper blobHelper;

        private IPurger purger;

        private IPermissionsManager permissionsManager;

        private CaseSensitiveClaimsIdentity firstIdentity;

        [OneTimeSetUp]
        public async Task OneTimeSetup()
        {
            var builder = new ConfigurationBuilder().AddUserSecrets<ContentCommitTests>();
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
                .UseCosmos(secretConfiguration.GetConnectionString("CosmosDb"), databaseName: $"{nameof(ContentCommitTests)}Database", CosmosTestOptions.UseGateway)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CosmosEventId.SyncNotSupported))
                .Options;

            this.dbContext = new SafeExchangeDbContext(dbContextOptions);
            await this.dbContext.Database.EnsureCreatedAsync();

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

            this.secretContentMeta = new SafeExchangeSecretContentMeta(
                this.testConfiguration, this.dbContext, this.tokenHelper,
                this.globalFilters, this.purger, this.permissionsManager);

            this.contentCommit = new SafeExchangeContentCommit(
                this.testConfiguration, this.dbContext, this.tokenHelper,
                this.globalFilters, this.purger, this.permissionsManager);

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
        public async Task Commit_HashMatches_ReturnsOk_AndFlipsReady()
        {
            // [GIVEN] An attachment seeded with a running hash state from streaming known bytes,
            //         status Updating, access ticket set.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var attachment = await this.CreateAttachmentAsync(claimsPrincipal);
            var bytes = Encoding.UTF8.GetBytes("hello-world-payload");
            var (accessTicket, expectedHash) = await this.SeedHashedUploadInProgressAsync(attachment.ContentName, bytes);

            // [WHEN] Commit is called with the matching hex.
            var request = TestFactory.CreateHttpRequestData("patch");
            request.Headers.Add(SafeExchangeSecretStream.AccessTicketHeaderName, accessTicket);
            request.SetBodyAsJson(new { hash = expectedHash });

            var response = await this.contentCommit.Run(request, DefaultSecretName, attachment.ContentName, claimsPrincipal, this.logger);
            var okResult = response as TestHttpResponseData;

            // [THEN] 200 OK; content hash set, status flipped to Ready,
            //        running state cleared, access ticket cleared.
            Assert.That(okResult, Is.Not.Null);
            Assert.That(okResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var result = okResult?.ReadBodyAsJson<BaseResponseObject<ContentCommitOutput>>();
            Assert.That(result?.Status, Is.EqualTo("ok"));
            Assert.That(result?.Result?.ContentName, Is.EqualTo(attachment.ContentName));
            Assert.That(result?.Result?.Hash, Is.EqualTo(expectedHash));

            this.dbContext.ChangeTracker.Clear();
            var reloaded = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(DefaultSecretName));
            var reloadedContent = reloaded?.Content.FirstOrDefault(c => c.ContentName.Equals(attachment.ContentName));

            Assert.That(reloadedContent, Is.Not.Null);
            Assert.That(reloadedContent!.Hash, Is.EqualTo(expectedHash));
            Assert.That(reloadedContent.Status, Is.EqualTo(ContentStatus.Ready));
            Assert.That(reloadedContent.RunningHashState, Is.Null);
            Assert.That(reloadedContent.AccessTicket, Is.EqualTo(string.Empty));
        }

        [Test]
        public async Task Commit_HashMismatch_Returns422_AndPreservesState()
        {
            // [GIVEN] An attachment seeded with a running hash state.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var attachment = await this.CreateAttachmentAsync(claimsPrincipal);
            var bytes = Encoding.UTF8.GetBytes("server-known-payload");
            var (accessTicket, expectedHash) = await this.SeedHashedUploadInProgressAsync(attachment.ContentName, bytes);

            var clientHash = new string('0', 64);

            // [WHEN] Commit is called with a different (bogus) hex.
            var request = TestFactory.CreateHttpRequestData("patch");
            request.Headers.Add(SafeExchangeSecretStream.AccessTicketHeaderName, accessTicket);
            request.SetBodyAsJson(new { hash = clientHash });

            var response = await this.contentCommit.Run(request, DefaultSecretName, attachment.ContentName, claimsPrincipal, this.logger);
            var errorResult = response as TestHttpResponseData;

            // [THEN] 422 hash_mismatch with Expected/Actual populated; state untouched.
            Assert.That(errorResult, Is.Not.Null);
            Assert.That(errorResult?.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));

            var result = errorResult?.ReadBodyAsJson<BaseResponseObject<ChunkHashMismatch>>();
            Assert.That(result?.Status, Is.EqualTo("hash_mismatch"));
            Assert.That(result?.Result, Is.Not.Null);
            Assert.That(result!.Result!.Expected, Is.EqualTo(clientHash));
            Assert.That(result.Result.Actual, Is.EqualTo(expectedHash));

            this.dbContext.ChangeTracker.Clear();
            var reloaded = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(DefaultSecretName));
            var reloadedContent = reloaded?.Content.FirstOrDefault(c => c.ContentName.Equals(attachment.ContentName));

            Assert.That(reloadedContent, Is.Not.Null);
            Assert.That(reloadedContent!.Hash, Is.Null.Or.Empty);
            Assert.That(reloadedContent.Status, Is.EqualTo(ContentStatus.Updating));
            Assert.That(reloadedContent.RunningHashState, Is.Not.Null);
            Assert.That(reloadedContent.AccessTicket, Is.EqualTo(accessTicket));
        }

        [Test]
        public async Task Commit_NoRunningState_Returns422_NoUploadState()
        {
            // [GIVEN] An attachment with no running hash state but a valid access ticket.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var attachment = await this.CreateAttachmentAsync(claimsPrincipal);
            var accessTicket = await this.SeedAccessTicketOnlyAsync(attachment.ContentName);

            // [WHEN] Commit is called with any hex.
            var request = TestFactory.CreateHttpRequestData("patch");
            request.Headers.Add(SafeExchangeSecretStream.AccessTicketHeaderName, accessTicket);
            request.SetBodyAsJson(new { hash = new string('a', 64) });

            var response = await this.contentCommit.Run(request, DefaultSecretName, attachment.ContentName, claimsPrincipal, this.logger);
            var errorResult = response as TestHttpResponseData;

            // [THEN] 422 no_upload_state.
            Assert.That(errorResult, Is.Not.Null);
            Assert.That(errorResult?.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));

            var result = errorResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(result?.Status, Is.EqualTo("no_upload_state"));
            Assert.That(result?.Error, Is.Not.Null);
        }

        [Test]
        public async Task Commit_MainContent_Returns422_Unprocessable()
        {
            // [GIVEN] Main content seeded with IsMain=true, a RunningHashState and an access ticket.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var mainContentName = await this.SeedMainContentWithRunningStateAsync();
            var accessTicket = await this.GetAccessTicketForContentAsync(mainContentName);

            // [WHEN] Commit is called on the main content.
            var request = TestFactory.CreateHttpRequestData("patch");
            request.Headers.Add(SafeExchangeSecretStream.AccessTicketHeaderName, accessTicket);
            request.SetBodyAsJson(new { hash = new string('a', 64) });

            var response = await this.contentCommit.Run(request, DefaultSecretName, mainContentName, claimsPrincipal, this.logger);
            var errorResult = response as TestHttpResponseData;

            // [THEN] 422 unprocessable (main content does not support explicit commit).
            Assert.That(errorResult, Is.Not.Null);
            Assert.That(errorResult?.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));

            var result = errorResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(result?.Status, Is.EqualTo("unprocessable"));
            Assert.That(result?.Error, Is.Not.Null);
        }

        [Test]
        public async Task Commit_BadHashFormat_Returns400()
        {
            // [GIVEN] An attachment with a running hash state.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var attachment = await this.CreateAttachmentAsync(claimsPrincipal);
            var bytes = Encoding.UTF8.GetBytes("payload");
            var (accessTicket, _) = await this.SeedHashedUploadInProgressAsync(attachment.ContentName, bytes);

            // [WHEN] Commit is called with a short/invalid hash.
            var request = TestFactory.CreateHttpRequestData("patch");
            request.Headers.Add(SafeExchangeSecretStream.AccessTicketHeaderName, accessTicket);
            request.SetBodyAsJson(new { hash = "abc" });

            var response = await this.contentCommit.Run(request, DefaultSecretName, attachment.ContentName, claimsPrincipal, this.logger);
            var badResult = response as TestHttpResponseData;

            // [THEN] 400 bad_request.
            Assert.That(badResult, Is.Not.Null);
            Assert.That(badResult?.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

            var result = badResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(result?.Status, Is.EqualTo("bad_request"));
            Assert.That(result?.Error, Is.Not.Null);
        }

        [Test]
        public async Task Commit_MissingAccessTicket_ReturnsUnauthorized()
        {
            // [GIVEN] An attachment with a running hash state.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var attachment = await this.CreateAttachmentAsync(claimsPrincipal);
            var bytes = Encoding.UTF8.GetBytes("payload");
            await this.SeedHashedUploadInProgressAsync(attachment.ContentName, bytes);

            // [WHEN] Commit is called without X-SafeExchange-Ticket.
            var request = TestFactory.CreateHttpRequestData("patch");
            request.SetBodyAsJson(new { hash = new string('a', 64) });

            var response = await this.contentCommit.Run(request, DefaultSecretName, attachment.ContentName, claimsPrincipal, this.logger);
            var unauthorizedResult = response as TestHttpResponseData;

            // [THEN] 401 unauthorized.
            Assert.That(unauthorizedResult, Is.Not.Null);
            Assert.That(unauthorizedResult?.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

            var result = unauthorizedResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(result?.Status, Is.EqualTo("unauthorized"));
            Assert.That(result?.Error, Is.Not.Null);
        }

        [Test]
        public async Task Commit_WrongAccessTicket_ReturnsUnauthorized()
        {
            // [GIVEN] An attachment with a running hash state.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var attachment = await this.CreateAttachmentAsync(claimsPrincipal);
            var bytes = Encoding.UTF8.GetBytes("payload");
            await this.SeedHashedUploadInProgressAsync(attachment.ContentName, bytes);

            // [WHEN] Commit is called with a non-matching ticket.
            var request = TestFactory.CreateHttpRequestData("patch");
            request.Headers.Add(SafeExchangeSecretStream.AccessTicketHeaderName, "not-the-real-ticket");
            request.SetBodyAsJson(new { hash = new string('a', 64) });

            var response = await this.contentCommit.Run(request, DefaultSecretName, attachment.ContentName, claimsPrincipal, this.logger);
            var unauthorizedResult = response as TestHttpResponseData;

            // [THEN] 401 unauthorized.
            Assert.That(unauthorizedResult, Is.Not.Null);
            Assert.That(unauthorizedResult?.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

            var result = unauthorizedResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(result?.Status, Is.EqualTo("unauthorized"));
            Assert.That(result?.Error, Is.Not.Null);
        }

        [Test]
        public async Task Commit_SecondCall_Returns422_NoUploadState()
        {
            // [GIVEN] An attachment with a running hash state; first commit succeeds.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var attachment = await this.CreateAttachmentAsync(claimsPrincipal);
            var bytes = Encoding.UTF8.GetBytes("payload-for-two-commits");
            var (accessTicket, expectedHash) = await this.SeedHashedUploadInProgressAsync(attachment.ContentName, bytes);

            var firstRequest = TestFactory.CreateHttpRequestData("patch");
            firstRequest.Headers.Add(SafeExchangeSecretStream.AccessTicketHeaderName, accessTicket);
            firstRequest.SetBodyAsJson(new { hash = expectedHash });

            var firstResponse = await this.contentCommit.Run(firstRequest, DefaultSecretName, attachment.ContentName, claimsPrincipal, this.logger);
            var firstOk = firstResponse as TestHttpResponseData;
            Assert.That(firstOk?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // [WHEN] A second commit is called with the same hash.
            var secondRequest = TestFactory.CreateHttpRequestData("patch");
            secondRequest.Headers.Add(SafeExchangeSecretStream.AccessTicketHeaderName, accessTicket);
            secondRequest.SetBodyAsJson(new { hash = expectedHash });

            var secondResponse = await this.contentCommit.Run(secondRequest, DefaultSecretName, attachment.ContentName, claimsPrincipal, this.logger);
            var secondResult = secondResponse as TestHttpResponseData;

            // [THEN] After successful first commit the access-ticket is cleared,
            //        so the second commit fails at the ticket gate with 401 unauthorized.
            Assert.That(secondResult, Is.Not.Null);
            Assert.That(secondResult?.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

            var result = secondResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(result?.Status, Is.EqualTo("unauthorized"));
        }

        private async Task<ContentMetadata> CreateAttachmentAsync(ClaimsPrincipal claimsPrincipal)
        {
            var request = TestFactory.CreateHttpRequestData("post");
            var creationInput = new ContentMetadataCreationInput()
            {
                ContentType = "image/jpeg",
                FileName = "any.jpg"
            };

            request.SetBodyAsJson(creationInput);

            var response = await this.secretContentMeta.Run(request, DefaultSecretName, string.Empty, claimsPrincipal, this.logger);
            var okResult = response as TestHttpResponseData;
            Assert.That(okResult, Is.Not.Null);
            Assert.That(okResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            this.dbContext.ChangeTracker.Clear();
            var reloaded = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(DefaultSecretName));
            var attachment = reloaded?.Content.FirstOrDefault(c => !c.IsMain);
            if (attachment == null)
            {
                throw new AssertionException("Attachment content was not created.");
            }

            return attachment;
        }

        private async Task<(string accessTicket, string expectedHash)> SeedHashedUploadInProgressAsync(string contentName, byte[] bytes)
        {
            var running = new SerializableSha256();
            running.Append(bytes);

            var finishCopy = SerializableSha256.Restore(running.SaveState());
            var expectedHash = Convert.ToHexString(finishCopy.Finish()).ToLowerInvariant();

            this.dbContext.ChangeTracker.Clear();
            var reloaded = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(DefaultSecretName));
            var content = reloaded!.Content.First(c => c.ContentName.Equals(contentName));

            var accessTicket = $"{Guid.NewGuid()}-{Random.Shared.NextInt64():00000000}";
            content.Chunks.Add(new ChunkMetadata
            {
                ChunkName = $"{contentName}-{0:00000000}",
                Hash = expectedHash,
                Length = bytes.Length,
            });
            content.RunningHashState = running.SaveState();
            content.Status = ContentStatus.Updating;
            content.AccessTicket = accessTicket;
            content.AccessTicketSetAt = DateTimeProvider.UtcNow;
            await this.dbContext.SaveChangesAsync();

            return (accessTicket, expectedHash);
        }

        private async Task<string> SeedAccessTicketOnlyAsync(string contentName)
        {
            this.dbContext.ChangeTracker.Clear();
            var reloaded = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(DefaultSecretName));
            var content = reloaded!.Content.First(c => c.ContentName.Equals(contentName));

            var accessTicket = $"{Guid.NewGuid()}-{Random.Shared.NextInt64():00000000}";
            content.RunningHashState = null;
            content.Status = ContentStatus.Updating;
            content.AccessTicket = accessTicket;
            content.AccessTicketSetAt = DateTimeProvider.UtcNow;
            await this.dbContext.SaveChangesAsync();

            return accessTicket;
        }

        private async Task<string> SeedMainContentWithRunningStateAsync()
        {
            this.dbContext.ChangeTracker.Clear();
            var reloaded = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(DefaultSecretName));
            var mainContent = reloaded!.Content.First(c => c.IsMain);

            var running = new SerializableSha256();
            running.Append(Encoding.UTF8.GetBytes("doesn't-matter"));

            var accessTicket = $"{Guid.NewGuid()}-{Random.Shared.NextInt64():00000000}";
            mainContent.RunningHashState = running.SaveState();
            mainContent.Status = ContentStatus.Updating;
            mainContent.AccessTicket = accessTicket;
            mainContent.AccessTicketSetAt = DateTimeProvider.UtcNow;
            await this.dbContext.SaveChangesAsync();

            return mainContent.ContentName;
        }

        private async Task<string> GetAccessTicketForContentAsync(string contentName)
        {
            this.dbContext.ChangeTracker.Clear();
            var reloaded = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(DefaultSecretName));
            var content = reloaded!.Content.First(c => c.ContentName.Equals(contentName));
            return content.AccessTicket;
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
