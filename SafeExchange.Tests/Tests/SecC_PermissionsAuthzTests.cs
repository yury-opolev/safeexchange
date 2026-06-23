/// <summary>
/// SecC_PermissionsAuthzTests
///
/// Cluster C security regression tests (Findings #4 CWE-285 UnsetPermissionAsync,
/// #9 CWE-266 SetPermissionAsync, #5/#7 CWE-285/CWE-20 RevokeAccessAsync).
///
/// Concern: a caller WITHOUT a grant-administering permission (CanGrantAccess /
/// CanRevokeAccess / owner) on a target secret could change another principal's
/// access by reaching PermissionsManager.SetPermissionAsync / UnsetPermissionAsync
/// (and the SafeExchangeAccess.RevokeAccessAsync handler).
///
/// These tests exercise the *real* entry point (SafeExchangeAccess.Run) end-to-end
/// to verify the caller-authorization gate. They use the EF InMemory provider so
/// they do not require a Cosmos emulator (see ApplicationOwnerInvariantTests for the
/// pattern) and avoid Cosmos-only APIs.
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
    using SafeExchange.Core.Groups;
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
    public class SecC_PermissionsAuthzTests
    {
        private ILogger logger = null!;

        private SafeExchangeSecretMeta secretMeta = null!;
        private SafeExchangeAccess secretAccess = null!;

        private IConfiguration testConfiguration = null!;
        private SafeExchangeDbContext dbContext = null!;

        private IGroupsManager groupsManager = null!;
        private ITokenHelper tokenHelper = null!;
        private GlobalFilters globalFilters = null!;
        private IBlobHelper blobHelper = null!;
        private IPurger purger = null!;
        private IPermissionsManager permissionsManager = null!;

        // owner@test.test -> creates the secret (becomes owner with Full permissions).
        private CaseSensitiveClaimsIdentity ownerIdentity = null!;
        // attacker@test.test -> has NO permissions whatsoever on the secret.
        private CaseSensitiveClaimsIdentity attackerIdentity = null!;
        // victim@test.test -> the principal whose access the attacker tries to change.
        private CaseSensitiveClaimsIdentity victimIdentity = null!;

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            this.logger = TestFactory.CreateLogger();

            var configurationValues = new Dictionary<string, string>
            {
                {"Features:UseNotifications", "False"},
                {"Features:UseGroupsAuthorization", "False"}
            };

            this.testConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(configurationValues)
                .Build();

            // InMemory EF — no Cosmos emulator required.
            var dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseInMemoryDatabase($"{nameof(SecC_PermissionsAuthzTests)}-{Guid.NewGuid()}")
                .Options;

            this.dbContext = new SafeExchangeDbContext(dbContextOptions);

            this.groupsManager = new GroupsManager(this.dbContext, Mock.Of<ILogger<GroupsManager>>());
            this.tokenHelper = new TestTokenHelper();

            var gagc = new GloballyAllowedGroupsConfiguration();
            var groupsConfiguration = Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(x => x.CurrentValue == gagc);

            var ac = new AdminConfiguration();
            var adminConfiguration = Mock.Of<IOptionsMonitor<AdminConfiguration>>(x => x.CurrentValue == ac);

            this.globalFilters = new GlobalFilters(groupsConfiguration, adminConfiguration, this.tokenHelper, TestFactory.CreateLogger<GlobalFilters>());

            this.blobHelper = new TestBlobHelper();
            this.purger = new PurgeManager(this.testConfiguration, this.blobHelper, TestFactory.CreateLogger<PurgeManager>());
            this.permissionsManager = new PermissionsManager(this.testConfiguration, this.dbContext, TestFactory.CreateLogger<PermissionsManager>());

            this.ownerIdentity = Identity("owner@test.test", "Owner User", "00000000-0000-0000-0000-0000000000a1");
            this.attackerIdentity = Identity("attacker@test.test", "Attacker User", "00000000-0000-0000-0000-0000000000a2");
            this.victimIdentity = Identity("victim@test.test", "Victim User", "00000000-0000-0000-0000-0000000000a3");

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
                this.globalFilters, this.purger, this.permissionsManager, new NoOpAuditWriter());

            this.secretAccess = new SafeExchangeAccess(
                this.dbContext, this.groupsManager, this.tokenHelper,
                this.globalFilters, this.purger, this.permissionsManager,
                new NoOpAuditWriter(), Mock.Of<IOrphanedSecretManager>());
        }

        [TearDown]
        public void Cleanup()
        {
            this.dbContext.ChangeTracker.Clear();
            this.dbContext.Users.RemoveRange(this.dbContext.Users.ToList());
            this.dbContext.Objects.RemoveRange(this.dbContext.Objects.ToList());
            this.dbContext.Permissions.RemoveRange(this.dbContext.Permissions.ToList());
            this.dbContext.AccessRequests.RemoveRange(this.dbContext.AccessRequests.ToList());
            this.dbContext.GroupDictionary.RemoveRange(this.dbContext.GroupDictionary.ToList());
            this.dbContext.SaveChanges();
        }

        private static CaseSensitiveClaimsIdentity Identity(string upn, string displayName, string oid)
            => new CaseSensitiveClaimsIdentity(new List<Claim>()
            {
                new Claim("upn", upn),
                new Claim("displayname", displayName),
                new Claim("oid", oid),
                new Claim("tid", "00000000-0000-0000-0000-000000000001"),
            }.AsEnumerable());

        // Finding #9 (CWE-266 SetPermissionAsync): an attacker with no grant-admin
        // permission must NOT be able to grant (set) access to another principal.
        [Test]
        public async Task UnauthorizedCaller_CannotGrantAccess_IsForbidden()
        {
            // [GIVEN] An owner created a secret. The attacker has no permission on it.
            await this.CreateSecret(this.ownerIdentity, "topsecret");

            // [WHEN] The attacker POSTs to grant the victim full access.
            var request = TestFactory.CreateHttpRequestData("post");
            request.SetBodyAsJson(new List<SubjectPermissionsInput>()
            {
                new SubjectPermissionsInput()
                {
                    SubjectName = "victim@test.test",
                    CanRead = true, CanWrite = true, CanGrantAccess = true, CanRevokeAccess = true
                }
            });

            var response = await this.secretAccess.Run(
                request, "topsecret", new ClaimsPrincipal(this.attackerIdentity), this.logger) as TestHttpResponseData;

            // [THEN] The request is forbidden and NO permission row was created for the victim.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

            var victimRows = await this.dbContext.Permissions
                .Where(p => p.SecretName == "topsecret" && p.SubjectId == "victim@test.test")
                .ToListAsync();
            Assert.That(victimRows, Is.Empty, "Unauthorized caller must not be able to grant access.");
        }

        // Finding #4 / #5 / #7 (CWE-285 UnsetPermissionAsync, RevokeAccessAsync): an
        // attacker with no grant-admin permission must NOT be able to revoke (unset)
        // another principal's access.
        [Test]
        public async Task UnauthorizedCaller_CannotRevokeAccess_IsForbidden()
        {
            // [GIVEN] An owner created a secret and granted the victim read access.
            await this.CreateSecret(this.ownerIdentity, "topsecret");
            await this.GrantAccess(this.ownerIdentity, "topsecret", "victim@test.test", read: true, write: false, grant: false, revoke: false);

            // [WHEN] The attacker (no permissions) DELETEs the victim's access.
            var request = TestFactory.CreateHttpRequestData("delete");
            request.SetBodyAsJson(new List<SubjectPermissionsInput>()
            {
                new SubjectPermissionsInput()
                {
                    SubjectName = "victim@test.test",
                    CanRead = true, CanWrite = true, CanGrantAccess = true, CanRevokeAccess = true
                }
            });

            var response = await this.secretAccess.Run(
                request, "topsecret", new ClaimsPrincipal(this.attackerIdentity), this.logger) as TestHttpResponseData;

            // [THEN] The request is forbidden and the victim STILL has read access.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

            var victimRow = await this.dbContext.Permissions
                .FirstOrDefaultAsync(p => p.SecretName == "topsecret" && p.SubjectId == "victim@test.test");
            Assert.That(victimRow, Is.Not.Null, "Victim permission row must not be removed by an unauthorized caller.");
            Assert.That(victimRow!.CanRead, Is.True, "Unauthorized caller must not be able to revoke access.");
        }

        // Positive control: an authorized owner (CanGrantAccess + CanRevokeAccess) can
        // grant and then revoke another principal's access successfully.
        [Test]
        public async Task AuthorizedOwner_CanGrantAndRevokeAccess_Succeeds()
        {
            // [GIVEN] An owner created a secret.
            await this.CreateSecret(this.ownerIdentity, "topsecret");

            // [WHEN] The owner grants the victim read access.
            await this.GrantAccess(this.ownerIdentity, "topsecret", "victim@test.test", read: true, write: false, grant: false, revoke: false);

            // [THEN] The grant succeeded.
            var grantedRow = await this.dbContext.Permissions
                .FirstOrDefaultAsync(p => p.SecretName == "topsecret" && p.SubjectId == "victim@test.test");
            Assert.That(grantedRow, Is.Not.Null);
            Assert.That(grantedRow!.CanRead, Is.True);

            // [WHEN] The owner revokes the victim's read access.
            var request = TestFactory.CreateHttpRequestData("delete");
            request.SetBodyAsJson(new List<SubjectPermissionsInput>()
            {
                new SubjectPermissionsInput()
                {
                    SubjectName = "victim@test.test",
                    CanRead = true, CanWrite = false, CanGrantAccess = false, CanRevokeAccess = false
                }
            });

            var response = await this.secretAccess.Run(
                request, "topsecret", new ClaimsPrincipal(this.ownerIdentity), this.logger) as TestHttpResponseData;

            // [THEN] The revoke succeeded and the victim row is gone.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var afterRow = await this.dbContext.Permissions
                .FirstOrDefaultAsync(p => p.SecretName == "topsecret" && p.SubjectId == "victim@test.test");
            Assert.That(afterRow, Is.Null, "Authorized owner should be able to revoke access.");
        }

        private async Task CreateSecret(CaseSensitiveClaimsIdentity identity, string secretName)
        {
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
            var response = await this.secretMeta.Run(request, secretName, new ClaimsPrincipal(identity), this.logger) as TestHttpResponseData;

            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        private async Task GrantAccess(CaseSensitiveClaimsIdentity identity, string secretName, string subjectName, bool read, bool write, bool grant, bool revoke)
        {
            var request = TestFactory.CreateHttpRequestData("post");
            request.SetBodyAsJson(new List<SubjectPermissionsInput>()
            {
                new SubjectPermissionsInput()
                {
                    SubjectType = SubjectTypeInput.User,
                    SubjectName = subjectName,
                    CanRead = read, CanWrite = write, CanGrantAccess = grant, CanRevokeAccess = revoke
                }
            });

            var response = await this.secretAccess.Run(request, secretName, new ClaimsPrincipal(identity), this.logger) as TestHttpResponseData;

            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
    }
}
