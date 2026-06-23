/// <summary>
/// SecF1_GroupRevokeKeyTests
///
/// Characterization tests for Hyenas finding #004 (CWE-285, UnsetPermissionAsync):
/// "group revoke keyed by SubjectName while group grants are stored under the group GUID,
///  so an authorized revoke silently fails and group access becomes irrevocable."
///
/// The reachable revoke path in SafeExchangeAccess.ApplyRevokeAsync now canonicalises the
/// target id via ResolveTargetAndBeforeAsync (introduced by the 2026-05 upstream uptake),
/// resolving a group mail/display name to the stored GroupId before calling
/// UnsetPermissionAsync. These tests pin that behaviour: granting a group by mail (stored
/// under the GroupId GUID) and then revoking by mail must remove the GUID-keyed permission
/// row. If the historic bug regressed, the row would survive the revoke and these tests
/// would fail.
///
/// Cosmos-free: uses EF Core InMemory provider (same pattern as ApplicationOwnerInvariantTests).
/// </summary>

namespace SafeExchange.Tests
{
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
    using Azure.Core.Serialization;

    [TestFixture]
    public class SecF1_GroupRevokeKeyTests
    {
        private const string GroupMail = "secf1.group@test.test";
        private const string GroupId = "0f1f0f1f-0f1f-0f1f-0f1f-0f1f0f1f0f1f";
        private const string SecretName = "secf1-secret";

        private ILogger logger;
        private IConfiguration testConfiguration;
        private SafeExchangeDbContext dbContext;
        private IGroupsManager groupsManager;
        private ITokenHelper tokenHelper;
        private GlobalFilters globalFilters;
        private TestBlobHelper blobHelper;
        private PurgeManager purger;
        private PermissionsManager permissionsManager;

        private SafeExchangeSecretMeta secretMeta;
        private SafeExchangeAccess secretAccess;

        private CaseSensitiveClaimsIdentity ownerIdentity;

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            this.logger = TestFactory.CreateLogger();

            var configurationValues = new Dictionary<string, string>
            {
                { "Features:UseNotifications", "False" },
                { "Features:UseGroupsAuthorization", "True" }
            };

            this.testConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(configurationValues)
                .Build();

            var dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseInMemoryDatabase($"{nameof(SecF1_GroupRevokeKeyTests)}-{Guid.NewGuid()}")
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

            this.ownerIdentity = new CaseSensitiveClaimsIdentity(new List<Claim>()
            {
                new Claim("upn", "owner@test.test"),
                new Claim("displayname", "Owner User"),
                new Claim("oid", "00000000-0000-0000-0000-0000000000a1"),
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

            this.secretAccess = new SafeExchangeAccess(
                this.dbContext, this.groupsManager, this.tokenHelper,
                this.globalFilters, this.purger, this.permissionsManager,
                new NoOpAuditWriter(), Mock.Of<IOrphanedSecretManager>());

            // A registered group that has both a GUID and a mail, mirroring the directory
            // entry SafeExchange resolves group mail -> GroupId against.
            this.groupsManager
                .PutGroupAsync(GroupId, new GroupInput() { DisplayName = "Sec F1 Group", Mail = GroupMail }, SubjectType.User, "test@test.test")
                .GetAwaiter().GetResult();
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

        [Test]
        public async Task GrantGroupByMail_StoresPermissionUnderGroupId()
        {
            // [GIVEN] The owner creates a secret.
            await this.CreateSecret(this.ownerIdentity, SecretName);

            // [WHEN] The owner grants read access to a group identified by mail.
            await this.GroupAccessRequest("post", SecretName, subjectId: null, subjectName: GroupMail, read: true);

            // [THEN] The permission row is keyed by the group's GUID, not its mail.
            var rows = this.dbContext.Permissions.Where(p => p.SecretName == SecretName && p.SubjectType == SubjectType.Group).ToList();
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(rows[0].SubjectId, Is.EqualTo(GroupId));
            Assert.That(rows[0].CanRead, Is.True);
        }

        [Test]
        public async Task RevokeGroupByMail_RemovesGuidKeyedRow()
        {
            // [GIVEN] The owner creates a secret and grants read access to a group by mail
            // (stored under the group GUID).
            await this.CreateSecret(this.ownerIdentity, SecretName);
            await this.GroupAccessRequest("post", SecretName, subjectId: null, subjectName: GroupMail, read: true);

            Assert.That(
                this.dbContext.Permissions.Count(p => p.SecretName == SecretName && p.SubjectType == SubjectType.Group && p.SubjectId == GroupId),
                Is.EqualTo(1),
                "Precondition: group permission must be stored under the GroupId GUID.");

            // [WHEN] The owner revokes that group access using the same mail (finding #004 path).
            await this.GroupAccessRequest("delete", SecretName, subjectId: null, subjectName: GroupMail, read: true);

            // [THEN] The GUID-keyed permission row is gone - the revoke is not silently lost.
            var remaining = this.dbContext.Permissions.Count(p => p.SecretName == SecretName && p.SubjectType == SubjectType.Group);
            Assert.That(remaining, Is.EqualTo(0), "Group access must be revoked, leaving no Group permission rows.");
        }

        [Test]
        public async Task RevokeGroupByGuid_RemovesGuidKeyedRow()
        {
            // [GIVEN] The owner creates a secret and grants read access to a group by GUID.
            await this.CreateSecret(this.ownerIdentity, SecretName);
            await this.GroupAccessRequest("post", SecretName, subjectId: GroupId, subjectName: "Sec F1 Group", read: true);

            Assert.That(
                this.dbContext.Permissions.Count(p => p.SecretName == SecretName && p.SubjectType == SubjectType.Group && p.SubjectId == GroupId),
                Is.EqualTo(1));

            // [WHEN] The owner revokes that group access using the GUID.
            await this.GroupAccessRequest("delete", SecretName, subjectId: GroupId, subjectName: "Sec F1 Group", read: true);

            // [THEN] The permission row is removed.
            var remaining = this.dbContext.Permissions.Count(p => p.SecretName == SecretName && p.SubjectType == SubjectType.Group);
            Assert.That(remaining, Is.EqualTo(0));
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

        private async Task GroupAccessRequest(string method, string secretName, string? subjectId, string subjectName, bool read)
        {
            var accessRequest = TestFactory.CreateHttpRequestData(method);
            var accessInput = new List<SubjectPermissionsInput>()
            {
                new SubjectPermissionsInput()
                {
                    SubjectType = SubjectTypeInput.Group,
                    SubjectId = subjectId!,
                    SubjectName = subjectName,
                    CanRead = read,
                    CanWrite = false,
                    CanGrantAccess = false,
                    CanRevokeAccess = false,
                }
            };

            accessRequest.SetBodyAsJson(accessInput);
            var claimsPrincipal = new ClaimsPrincipal(this.ownerIdentity);
            var response = await this.secretAccess.Run(accessRequest, secretName, claimsPrincipal, this.logger);
            var result = response as TestHttpResponseData;

            Assert.That(result, Is.Not.Null);
            Assert.That(result?.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"{method} access request should succeed.");
        }
    }
}
