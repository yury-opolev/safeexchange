/// <summary>
/// SecF2_SecretStreamReadNoPurgeTests
///
/// Characterization test for Hyenas finding #030-family (CWE-285, "destructive read path"):
/// the claim that a read-authorized content download can trigger irreversible data erasure.
///
/// In SafeExchangeSecretStream the only erasure happens in TryGetAccessTicketAsync, and only
/// when a content's access ticket has EXPIRED (an abandoned in-progress upload lock). That
/// branch is exercised by SecretStreamTests.AccessTicketExpiry_ClearsRunningHashState /
/// ChunksClearedOnAccessTicketTimeout. This test pins the complementary direction that the
/// finding actually alleges: a plain read of content that has NO access ticket must NOT erase
/// anything, even after the access-ticket timeout has elapsed. Erasure is reclamation of a
/// stale upload lock, not an attacker-reachable destructive read.
///
/// Cosmos-free: EF Core InMemory provider.
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
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
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
    public class SecF2_SecretStreamReadNoPurgeTests
    {
        private const string SecretName = "secf2-secret";
        private const string ContentName = "secf2-content";

        private ILogger logger;
        private IConfiguration testConfiguration;
        private SafeExchangeDbContext dbContext;
        private ITokenHelper tokenHelper;
        private GlobalFilters globalFilters;
        private TestBlobHelper blobHelper;
        private PurgeManager purger;
        private PermissionsManager permissionsManager;

        private SafeExchangeSecretMeta secretMeta;
        private SafeExchangeSecretStream secretStream;

        private CaseSensitiveClaimsIdentity ownerIdentity;

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            this.logger = TestFactory.CreateLogger();

            var configurationValues = new Dictionary<string, string>
            {
                { "Features:UseNotifications", "False" },
                { "Features:UseGroupsAuthorization", "True" },
                { "AccessTickets:AccessTicketTimeout", "00:10:00.000" }
            };

            this.testConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(configurationValues)
                .Build();

            var dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseInMemoryDatabase($"{nameof(SecF2_SecretStreamReadNoPurgeTests)}-{Guid.NewGuid()}")
                .Options;

            this.dbContext = new SafeExchangeDbContext(dbContextOptions);

            this.tokenHelper = new TestTokenHelper();

            var gagc = new GloballyAllowedGroupsConfiguration();
            var groupsConfiguration = Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(x => x.CurrentValue == gagc);

            var ac = new AdminConfiguration();
            var adminConfiguration = Mock.Of<IOptionsMonitor<AdminConfiguration>>(x => x.CurrentValue == ac);

            this.globalFilters = new GlobalFilters(groupsConfiguration, adminConfiguration, this.tokenHelper, TestFactory.CreateLogger<GlobalFilters>());

            this.blobHelper = new TestBlobHelper();
            this.purger = new PurgeManager(this.testConfiguration, this.blobHelper, TestFactory.CreateLogger<PurgeManager>());
            this.permissionsManager = new PermissionsManager(this.testConfiguration, this.dbContext, TestFactory.CreateLogger<PermissionsManager>());

            this.ownerIdentity = new CaseSensitiveClaimsIdentity(new List<Claim>()
            {
                new Claim("upn", "owner@test.test"),
                new Claim("displayname", "Owner User"),
                new Claim("oid", "00000000-0000-0000-0000-0000000000b1"),
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
            this.dbContext.Dispose();
        }

        [SetUp]
        public void Setup()
        {
            DateTimeProvider.SpecifiedDateTime = DateTime.UtcNow;

            this.secretMeta = new SafeExchangeSecretMeta(
                this.testConfiguration, this.dbContext, this.tokenHelper,
                this.globalFilters, this.purger, this.permissionsManager, new NoOpAuditWriter());

            this.secretStream = new SafeExchangeSecretStream(
                this.testConfiguration, this.dbContext, this.tokenHelper,
                this.globalFilters, this.purger, this.blobHelper, this.permissionsManager, new NoOpAuditWriter());
        }

        [TearDown]
        public void Cleanup()
        {
            this.dbContext.ChangeTracker.Clear();
            this.dbContext.Users.RemoveRange(this.dbContext.Users.ToList());
            this.dbContext.Objects.RemoveRange(this.dbContext.Objects.ToList());
            this.dbContext.Permissions.RemoveRange(this.dbContext.Permissions.ToList());
            this.dbContext.SaveChanges();
        }

        [Test]
        public async Task ReadOfContentWithoutAccessTicket_DoesNotPurge_EvenAfterTimeout()
        {
            // [GIVEN] The owner creates a secret and we seed content that has chunks and a
            // running-hash state, but NO access ticket (i.e. not an abandoned upload lock).
            await this.CreateSecret(this.ownerIdentity, SecretName);

            this.dbContext.ChangeTracker.Clear();
            var seeded = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(SecretName));
            Assert.That(seeded, Is.Not.Null);
            seeded!.KeepInStorage = true;

            var content = new ContentMetadata
            {
                ContentName = ContentName,
                ContentType = "application/octet-stream",
                FileName = "f.bin",
                Status = ContentStatus.Updating,
                AccessTicket = string.Empty,
                AccessTicketSetAt = DateTime.MinValue,
                RunningHashState = new byte[] { 1, 2, 3, 4 },
            };
            content.Chunks.Add(new ChunkMetadata { ChunkName = $"{ContentName}-{0:00000000}", Hash = "deadbeef", Length = 10 });
            seeded.Content.Add(content);
            await this.dbContext.SaveChangesAsync();

            // Advance the virtual clock well past the configured AccessTicketTimeout (00:10:00)
            // to prove time alone never triggers erasure without an outstanding ticket.
            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromMinutes(30);

            // [WHEN] The owner (Read-authorized) issues a chunk download.
            var getRequest = TestFactory.CreateHttpRequestData("get");
            var claimsPrincipal = new ClaimsPrincipal(this.ownerIdentity);
            var response = await this.secretStream.Run(
                getRequest, SecretName, ContentName, $"{0:00000000}", claimsPrincipal, this.logger);
            var result = response as TestHttpResponseData;

            // [THEN] The read is refused because the content is not Ready - but crucially nothing
            // is erased: chunks, running-hash state and status survive the read untouched.
            Assert.That(result, Is.Not.Null);
            Assert.That(result?.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));

            this.dbContext.ChangeTracker.Clear();
            var reloaded = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(SecretName));
            var reloadedContent = reloaded?.Content.FirstOrDefault(c => c.ContentName.Equals(ContentName));

            Assert.That(reloadedContent, Is.Not.Null);
            Assert.That(reloadedContent!.Status, Is.EqualTo(ContentStatus.Updating), "Status must be unchanged by a read.");
            Assert.That(reloadedContent.Chunks.Count, Is.EqualTo(1), "Chunks must not be erased by a read.");
            Assert.That(reloadedContent.RunningHashState, Is.Not.Null, "Running-hash state must not be erased by a read.");
            Assert.That(reloadedContent.AccessTicket, Is.EqualTo(string.Empty));
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
            var result = response as TestHttpResponseData;

            Assert.That(result, Is.Not.Null);
            Assert.That(result?.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
    }
}
