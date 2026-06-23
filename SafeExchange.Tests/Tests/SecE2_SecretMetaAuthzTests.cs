/// <summary>
/// SecE2_SecretMetaAuthzTests
///
/// Security hardening tests for Cluster E2:
///  - Finding #30 (CWE-285) TryGetAccessTicketAsync          -> FALSE POSITIVE (within this file)
///  - Finding #32 (CWE-20)  HandleSecretContentMetaUpdate    -> REAL, fixed (ContentType validation)
///  - Finding #31 (CWE-285) HandlePinnedGroupRegistration    -> REAL, fixed (route/body group id mismatch)
///
/// These tests deliberately use the EF Core InMemory provider so they can run
/// without a Cosmos emulator. Run only this class:
///   dotnet test SafeExchange.Tests.csproj --filter "FullyQualifiedName~SecE2_SecretMetaAuthzTests"
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
    using SafeExchange.Core;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Middleware;
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
    public class SecE2_SecretMetaAuthzTests
    {
        private Microsoft.Extensions.Logging.ILogger logger;

        private IConfiguration testConfiguration;

        private SafeExchangeDbContext dbContext;

        private ITokenHelper tokenHelper;

        private GlobalFilters globalFilters;

        private IBlobHelper blobHelper;

        private IPurger purger;

        private IPermissionsManager permissionsManager;

        private SafeExchangeSecretMeta secretMeta;
        private SafeExchangeSecretContentMeta secretContentMeta;
        private SafeExchangePinnedGroups pinnedGroups;

        private CaseSensitiveClaimsIdentity ownerIdentity;
        private CaseSensitiveClaimsIdentity strangerIdentity;

        [SetUp]
        public void Setup()
        {
            this.logger = TestFactory.CreateLogger();

            var configurationValues = new Dictionary<string, string>
            {
                { "Features:UseNotifications", "False" },
                { "Features:UseGroupsAuthorization", "True" },
            };

            this.testConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(configurationValues)
                .Build();

            var dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseInMemoryDatabase($"SecE2-{Guid.NewGuid()}")
                .Options;
            this.dbContext = new SafeExchangeDbContext(dbContextOptions);

            this.tokenHelper = new TestTokenHelper();

            GloballyAllowedGroupsConfiguration gagc = new GloballyAllowedGroupsConfiguration();
            var groupsConfiguration = Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(x => x.CurrentValue == gagc);

            AdminConfiguration ac = new AdminConfiguration();
            var adminConfiguration = Mock.Of<IOptionsMonitor<AdminConfiguration>>(x => x.CurrentValue == ac);

            this.globalFilters = new GlobalFilters(groupsConfiguration, adminConfiguration, this.tokenHelper, TestFactory.CreateLogger<GlobalFilters>());

            this.blobHelper = new TestBlobHelper();
            this.purger = new PurgeManager(this.testConfiguration, this.blobHelper, TestFactory.CreateLogger<PurgeManager>());
            this.permissionsManager = new PermissionsManager(this.testConfiguration, this.dbContext, TestFactory.CreateLogger<PermissionsManager>());

            this.secretMeta = new SafeExchangeSecretMeta(
                this.testConfiguration, this.dbContext, this.tokenHelper,
                this.globalFilters, this.purger, this.permissionsManager, new NoOpAuditWriter());

            this.secretContentMeta = new SafeExchangeSecretContentMeta(
                this.testConfiguration, this.dbContext, this.tokenHelper,
                this.globalFilters, this.purger, this.permissionsManager, new NoOpAuditWriter());

            this.pinnedGroups = new SafeExchangePinnedGroups(this.dbContext, this.tokenHelper, this.globalFilters);

            this.ownerIdentity = new CaseSensitiveClaimsIdentity(new List<Claim>()
            {
                new Claim("upn", "owner@test.test"),
                new Claim("displayname", "Owner User"),
                new Claim("oid", "00000000-0000-0000-0000-000000000001"),
                new Claim("tid", "00000000-0000-0000-0000-000000000001"),
            }.AsEnumerable());

            this.strangerIdentity = new CaseSensitiveClaimsIdentity(new List<Claim>()
            {
                new Claim("upn", "stranger@test.test"),
                new Claim("displayname", "Stranger User"),
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

        [TearDown]
        public void Cleanup()
        {
            DateTimeProvider.UseSpecifiedDateTime = false;
            this.dbContext.Dispose();
        }

        // ----------------------------------------------------------------------------------------
        // Finding #32 (CWE-20) — HandleSecretContentMetaUpdate: ContentType input validation.
        // ----------------------------------------------------------------------------------------

        [Test]
        public async Task Update_RejectsContentTypeWithControlCharacters()
        {
            // [GIVEN] An owner-created secret with a piece of content.
            await this.CreateSecretAsync("secret-32");
            var contentName = await this.CreateContentAsync("secret-32", "image/jpeg", "image.jpg");

            var claimsPrincipal = new ClaimsPrincipal(this.ownerIdentity);

            // [WHEN] The authorized writer updates the content with a ContentType that embeds
            //        CRLF control characters (header-injection / response-splitting payload).
            var patchRequest = TestFactory.CreateHttpRequestData("patch");
            patchRequest.SetBodyAsJson(new ContentMetadataUpdateInput()
            {
                ContentType = "text/plain\r\nX-Injected: evil",
                FileName = "image.jpg",
            });

            var response = await this.secretContentMeta.Run(patchRequest, "secret-32", contentName, claimsPrincipal, this.logger)
                as TestHttpResponseData;

            // [THEN] The request is rejected with a Bad Request and the malicious value is NOT persisted.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
                "Content type containing control characters must be rejected (CWE-20 / CWE-113).");

            this.dbContext.ChangeTracker.Clear();
            var stored = await this.dbContext.Objects.FirstAsync(o => o.ObjectName.Equals("secret-32"));
            var content = stored.Content.First(c => c.ContentName.Equals(contentName));
            Assert.That(content.ContentType, Is.EqualTo("image/jpeg"),
                "Stored content type must remain unchanged when validation rejects the input.");
        }

        [Test]
        public async Task Update_AcceptsValidContentType()
        {
            // [GIVEN] An owner-created secret with a piece of content.
            await this.CreateSecretAsync("secret-32b");
            var contentName = await this.CreateContentAsync("secret-32b", "image/jpeg", "image.jpg");

            var claimsPrincipal = new ClaimsPrincipal(this.ownerIdentity);

            // [WHEN] The authorized writer updates the content with a legitimate MIME type.
            var patchRequest = TestFactory.CreateHttpRequestData("patch");
            patchRequest.SetBodyAsJson(new ContentMetadataUpdateInput()
            {
                ContentType = "image/bmp",
                FileName = "image.bmp",
            });

            var response = await this.secretContentMeta.Run(patchRequest, "secret-32b", contentName, claimsPrincipal, this.logger)
                as TestHttpResponseData;

            // [THEN] The update succeeds and is persisted.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            this.dbContext.ChangeTracker.Clear();
            var stored = await this.dbContext.Objects.FirstAsync(o => o.ObjectName.Equals("secret-32b"));
            var content = stored.Content.First(c => c.ContentName.Equals(contentName));
            Assert.That(content.ContentType, Is.EqualTo("image/bmp"));
        }

        // ----------------------------------------------------------------------------------------
        // Finding #30 (CWE-285) — TryGetAccessTicketAsync.
        // Within SafeExchangeSecretContentMeta.cs the helper is only reachable from the Drop and
        // Delete handlers, both of which enforce PermissionType.Write before invoking it. This test
        // documents that gate (a stranger is Forbidden), evidencing the finding is a FALSE POSITIVE
        // for this file. The destructive read-path described by the finding lives in
        // SafeExchangeSecretStream.cs, which is out of scope for this change.
        // ----------------------------------------------------------------------------------------

        [Test]
        public async Task Drop_RequiresWrite_StrangerIsForbidden()
        {
            // [GIVEN] An owner-created secret with content.
            await this.CreateSecretAsync("secret-30");
            var contentName = await this.CreateContentAsync("secret-30", "image/jpeg", "image.jpg");

            // [WHEN] A stranger (no permission) attempts to drop the content, which is the only
            //        in-file path that reaches TryGetAccessTicketAsync.
            var strangerPrincipal = new ClaimsPrincipal(this.strangerIdentity);
            var dropRequest = TestFactory.CreateHttpRequestData("patch");
            var response = await this.secretContentMeta.RunDrop(dropRequest, "secret-30", contentName, strangerPrincipal, this.logger)
                as TestHttpResponseData;

            // [THEN] Forbidden — the Write gate protects the destructive helper.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        }

        // ----------------------------------------------------------------------------------------
        // Finding #31 (CWE-285) — HandlePinnedGroupRegistration: the validated route GUID and the
        // body's GroupId must identify the same group. Without this check a caller can pin/persist
        // a group record whose identifier diverges from the registered (and route-validated)
        // dictionary key, a confused-deputy / identifier-confusion gap.
        // ----------------------------------------------------------------------------------------

        [Test]
        public async Task PinnedGroupRegistration_RejectsRouteBodyGroupIdMismatch()
        {
            var routeGroupId = "00000000-0000-0000-0000-0000000000aa";
            var bodyGroupId = "00000000-0000-0000-0000-0000000000bb";
            var userId = "00000000-0000-0000-0000-0000000000c1";

            // [WHEN] The caller registers route group A but supplies body group B.
            var response = await this.RegisterPinnedGroupAsync(routeGroupId, bodyGroupId, "Group", "grp@test.mail", userId)
                as TestHttpResponseData;

            // [THEN] Bad Request, and nothing is persisted for the mismatched identifiers.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

            this.dbContext.ChangeTracker.Clear();
            var dictCount = await this.dbContext.GroupDictionary.CountAsync();
            var pinnedCount = await this.dbContext.PinnedGroups.CountAsync();
            Assert.That(dictCount, Is.EqualTo(0), "No shared group-dictionary record must be created for a mismatched registration.");
            Assert.That(pinnedCount, Is.EqualTo(0), "No pinned-group record must be created for a mismatched registration.");
        }

        [Test]
        public async Task PinnedGroupRegistration_AcceptsMatchingGroupId()
        {
            var groupId = "00000000-0000-0000-0000-0000000000dd";
            var userId = "00000000-0000-0000-0000-0000000000c2";

            // [WHEN] The route GUID and the body GroupId match.
            var response = await this.RegisterPinnedGroupAsync(groupId, groupId, "Group", "grp2@test.mail", userId)
                as TestHttpResponseData;

            // [THEN] Registration succeeds and is persisted.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            this.dbContext.ChangeTracker.Clear();
            var dictItem = await this.dbContext.GroupDictionary.FirstAsync();
            Assert.That(dictItem.GroupId, Is.EqualTo(groupId));
            var pinned = await this.dbContext.PinnedGroups.FirstAsync();
            Assert.That(pinned.GroupItemId, Is.EqualTo(groupId));
        }

        // ----------------------------------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------------------------------

        private async Task CreateSecretAsync(string secretName)
        {
            var claimsPrincipal = new ClaimsPrincipal(this.ownerIdentity);
            var request = TestFactory.CreateHttpRequestData("post");
            request.SetBodyAsJson(new MetadataCreationInput()
            {
                ExpirationSettings = new ExpirationSettingsInput()
                {
                    ScheduleExpiration = false,
                    ExpireAt = DateTimeProvider.UtcNow + TimeSpan.FromDays(180),
                    ExpireOnIdleTime = false,
                    IdleTimeToExpire = TimeSpan.FromDays(180),
                },
            });

            var response = await this.secretMeta.Run(request, secretName, claimsPrincipal, this.logger) as TestHttpResponseData;
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        private async Task<string> CreateContentAsync(string secretName, string contentType, string fileName)
        {
            var claimsPrincipal = new ClaimsPrincipal(this.ownerIdentity);
            var request = TestFactory.CreateHttpRequestData("post");
            request.SetBodyAsJson(new ContentMetadataCreationInput()
            {
                ContentType = contentType,
                FileName = fileName,
            });

            var response = await this.secretContentMeta.Run(request, secretName, string.Empty, claimsPrincipal, this.logger) as TestHttpResponseData;
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var body = response!.ReadBodyAsJson<BaseResponseObject<ContentMetadataOutput>>();
            Assert.That(body?.Result?.ContentName, Is.Not.Null);
            return body!.Result!.ContentName!;
        }

        private async Task<TestHttpResponseData?> RegisterPinnedGroupAsync(string routeGroupId, string bodyGroupId, string displayName, string? groupMail, string userId)
        {
            var request = TestFactory.CreateHttpRequestData("put");
            request.SetBodyAsJson(new PinnedGroupInput()
            {
                GroupId = bodyGroupId,
                GroupDisplayName = displayName,
                GroupMail = groupMail,
            });
            request.FunctionContext.Items[DefaultAuthenticationMiddleware.InvocationContextUserIdKey] = userId;

            var claimsPrincipal = new ClaimsPrincipal(this.ownerIdentity);
            return await this.pinnedGroups.Run(request, routeGroupId, claimsPrincipal, this.logger) as TestHttpResponseData;
        }
    }
}
