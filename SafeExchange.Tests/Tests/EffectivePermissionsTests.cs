/// <summary>
/// EffectivePermissionsTests
///
/// Covers the "Proposition - SafeExchange 001" backend changes:
///  1. Group-authorization telemetry no longer contains the caller's raw user identifier
///     (it uses the pseudonymous telemetry id, matching the direct-authorization path).
///  2. PermissionsManager exposes the caller's *effective* permissions on a secret — the
///     union of direct and group-derived grants — and the set of effectively-readable
///     secrets, which the secret list and single-secret read now surface.
///
/// Cosmos-free: uses the EF Core InMemory provider (same pattern as SecF1_GroupRevokeKeyTests).
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
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Telemetry;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    [TestFixture]
    public class EffectivePermissionsTests
    {
        private const string GroupA = "aaaa1111-aaaa-1111-aaaa-1111aaaa1111";
        private const string GroupB = "bbbb2222-bbbb-2222-bbbb-2222bbbb2222";

        private const string MemberUpn = "member@test.test";

        private ILogger logger = null!;
        private CapturingLogger<PermissionsManager> permissionsLogger = null!;

        private IConfiguration testConfiguration = null!;
        private SafeExchangeDbContext dbContext = null!;

        private IGroupsManager groupsManager = null!;
        private ITokenHelper tokenHelper = null!;
        private GlobalFilters globalFilters = null!;
        private IPurger purger = null!;
        private PermissionsManager permissionsManager = null!;

        private SafeExchangeSecretMeta secretMeta = null!;

        private CaseSensitiveClaimsIdentity ownerIdentity = null!;
        private CaseSensitiveClaimsIdentity memberIdentity = null!;

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            this.logger = TestFactory.CreateLogger();
            this.permissionsLogger = new CapturingLogger<PermissionsManager>();

            var configurationValues = new Dictionary<string, string>
            {
                { "Features:UseNotifications", "False" },
                { "Features:UseGroupsAuthorization", "True" }
            };

            this.testConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(configurationValues)
                .Build();

            var dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseInMemoryDatabase($"{nameof(EffectivePermissionsTests)}-{Guid.NewGuid()}")
                .Options;

            this.dbContext = new SafeExchangeDbContext(dbContextOptions);

            this.groupsManager = new GroupsManager(this.dbContext, Mock.Of<ILogger<GroupsManager>>());
            this.tokenHelper = new TestTokenHelper();

            var gagc = new GloballyAllowedGroupsConfiguration();
            var groupsConfiguration = Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(x => x.CurrentValue == gagc);

            var ac = new AdminConfiguration();
            var adminConfiguration = Mock.Of<IOptionsMonitor<AdminConfiguration>>(x => x.CurrentValue == ac);

            this.globalFilters = new GlobalFilters(groupsConfiguration, adminConfiguration, this.tokenHelper, TestFactory.CreateLogger<GlobalFilters>());

            this.purger = new PurgeManager(this.testConfiguration, new TestBlobHelper(), TestFactory.CreateLogger<PurgeManager>());
            this.permissionsManager = new PermissionsManager(this.testConfiguration, this.dbContext, this.permissionsLogger);

            this.ownerIdentity = Identity("owner@test.test", "Owner User", "00000000-0000-0000-0000-0000000000a1");
            this.memberIdentity = Identity(MemberUpn, "Member User", "00000000-0000-0000-0000-0000000000a2");

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
            this.permissionsLogger.Messages.Clear();
            TelemetryContext.Current = null;

            this.secretMeta = new SafeExchangeSecretMeta(
                this.testConfiguration, this.dbContext, this.tokenHelper,
                this.globalFilters, this.purger, this.permissionsManager, new NoOpAuditWriter());
        }

        [TearDown]
        public void Cleanup()
        {
            TelemetryContext.Current = null;
            this.dbContext.ChangeTracker.Clear();
            this.dbContext.Users.RemoveRange(this.dbContext.Users.ToList());
            this.dbContext.Objects.RemoveRange(this.dbContext.Objects.ToList());
            this.dbContext.Permissions.RemoveRange(this.dbContext.Permissions.ToList());
            this.dbContext.GroupDictionary.RemoveRange(this.dbContext.GroupDictionary.ToList());
            this.dbContext.SaveChanges();
        }

        // ---- Effective permission calculation ---------------------------------------------

        [Test]
        public async Task GetEffectivePermissions_DirectReadPlusGroupWrite_UnionsToReadWrite()
        {
            // [GIVEN] The caller has a direct Read grant and a group with Write on the same secret.
            const string secret = "eff-union";
            this.SeedMember(consentRequired: false, GroupA);
            this.SeedDirectUserPermission(secret, MemberUpn, PermissionType.Read);
            this.SeedGroupPermission(secret, GroupA, PermissionType.Write);

            // [WHEN] Effective permissions are calculated.
            var effective = await this.permissionsManager.GetEffectivePermissionsAsync(SubjectType.User, MemberUpn, secret);

            // [THEN] The result is the union: Read | Write.
            Assert.That(effective, Is.EqualTo(PermissionType.Read | PermissionType.Write));
        }

        [Test]
        public async Task GetEffectivePermissions_MultipleGroups_UnionsPermissions()
        {
            // [GIVEN] Two groups the caller belongs to grant different permissions on one secret.
            const string secret = "eff-multi-group";
            this.SeedMember(consentRequired: false, GroupA, GroupB);
            this.SeedGroupPermission(secret, GroupA, PermissionType.Read);
            this.SeedGroupPermission(secret, GroupB, PermissionType.Read | PermissionType.GrantAccess);

            // [WHEN] Effective permissions are calculated.
            var effective = await this.permissionsManager.GetEffectivePermissionsAsync(SubjectType.User, MemberUpn, secret);

            // [THEN] The result is the union of both group grants.
            Assert.That(effective, Is.EqualTo(PermissionType.Read | PermissionType.GrantAccess));
        }

        [Test]
        public async Task GetEffectivePermissions_ConsentRequired_ExcludesGroupPermissions()
        {
            // [GIVEN] The caller needs to consent, so group memberships must not be trusted.
            const string secret = "eff-consent";
            this.SeedMember(consentRequired: true, GroupA);
            this.SeedGroupPermission(secret, GroupA, PermissionType.Read | PermissionType.Write);

            // [WHEN] Effective permissions are calculated.
            var effective = await this.permissionsManager.GetEffectivePermissionsAsync(SubjectType.User, MemberUpn, secret);

            // [THEN] No group-derived permission is granted.
            Assert.That(effective, Is.EqualTo(PermissionType.None));
        }

        [Test]
        public async Task GetReadableSecrets_GroupOnlyRead_IncludesSecret()
        {
            // [GIVEN] A secret shared only through a group the caller belongs to.
            const string secret = "grouponly-readable";
            this.SeedMember(consentRequired: false, GroupA);
            this.SeedGroupPermission(secret, GroupA, PermissionType.Read | PermissionType.Write);

            // [WHEN] The caller's readable secrets are listed.
            var readable = await this.permissionsManager.GetReadableSecretsAsync(SubjectType.User, MemberUpn);

            // [THEN] The group-only secret is present with the group-derived effective permissions.
            Assert.That(readable.Count, Is.EqualTo(1));
            Assert.That(readable[0].SecretName, Is.EqualTo(secret));
            Assert.That(readable[0].Permissions, Is.EqualTo(PermissionType.Read | PermissionType.Write));
        }

        [Test]
        public async Task GetReadableSecrets_DirectAndGroupSamePermission_NoDuplicate()
        {
            // [GIVEN] The caller has a direct grant AND a group grant on the same secret.
            const string secret = "dedupe-me";
            this.SeedMember(consentRequired: false, GroupA);
            this.SeedDirectUserPermission(secret, MemberUpn, PermissionType.Read);
            this.SeedGroupPermission(secret, GroupA, PermissionType.Read);

            // [WHEN] The caller's readable secrets are listed.
            var readable = await this.permissionsManager.GetReadableSecretsAsync(SubjectType.User, MemberUpn);

            // [THEN] Exactly one aggregated result is returned for the secret.
            Assert.That(readable.Count(e => e.SecretName == secret), Is.EqualTo(1));
        }

        [Test]
        public async Task GetReadableSecrets_GroupWriteWithoutRead_NotListedButNotReadable()
        {
            // [GIVEN] A group grants only Write (no Read) on a secret.
            const string secret = "write-no-read";
            this.SeedMember(consentRequired: false, GroupA);
            this.SeedGroupPermission(secret, GroupA, PermissionType.Write);

            // [WHEN] The caller's readable secrets are listed.
            var readable = await this.permissionsManager.GetReadableSecretsAsync(SubjectType.User, MemberUpn);

            // [THEN] The secret is not surfaced in the readable list (no effective Read).
            Assert.That(readable.Any(e => e.SecretName == secret), Is.False);
        }

        // ---- Telemetry scrubbing -----------------------------------------------------------

        [Test]
        public async Task GroupAuthorization_Telemetry_UsesPseudonym_NotRawUserId()
        {
            // [GIVEN] A caller authorized via a group, with a known pseudonymous telemetry id set.
            const string secret = "telemetry-scrub";
            const string tid = "tid-eff-001";
            this.SeedMember(consentRequired: false, GroupA);
            this.SeedGroupPermission(secret, GroupA, PermissionType.Read);
            TelemetryContext.Current = tid;

            // [WHEN] Authorization runs down the group path (success) and a failing path.
            var authorized = await this.permissionsManager.IsAuthorizedAsync(SubjectType.User, MemberUpn, secret, PermissionType.Read);
            _ = await this.permissionsManager.IsAuthorizedAsync(SubjectType.User, MemberUpn, secret, PermissionType.RevokeAccess);

            // [THEN] The group was matched, the pseudonym appears in telemetry, and the raw UPN never does.
            Assert.That(authorized, Is.True);
            Assert.That(this.permissionsLogger.Messages, Is.Not.Empty);
            Assert.That(this.permissionsLogger.Messages.Any(m => m.Contains(tid)), Is.True,
                "Group-authorization telemetry must carry the pseudonymous id.");
            Assert.That(this.permissionsLogger.Messages.Any(m => m.Contains(MemberUpn, StringComparison.OrdinalIgnoreCase)), Is.False,
                "Group-authorization telemetry must not contain the caller's raw user identifier.");
        }

        [Test]
        public async Task GroupAuthorization_ConsentRequiredAndNoGroups_DoNotLogRawUserId()
        {
            // [GIVEN] A consent-required caller (drives the consent branch), then a no-groups caller.
            const string secret = "telemetry-branches";
            TelemetryContext.Current = "tid-branch";

            this.SeedMember(consentRequired: true, GroupA);
            this.SeedGroupPermission(secret, GroupA, PermissionType.Read);
            _ = await this.permissionsManager.IsAuthorizedAsync(SubjectType.User, MemberUpn, secret, PermissionType.Read);

            this.dbContext.ChangeTracker.Clear();
            this.dbContext.Users.RemoveRange(this.dbContext.Users.ToList());
            this.dbContext.SaveChanges();
            this.SeedMember(consentRequired: false); // no groups
            _ = await this.permissionsManager.IsAuthorizedAsync(SubjectType.User, MemberUpn, secret, PermissionType.Read);

            // [THEN] Neither branch leaked the raw UPN.
            Assert.That(this.permissionsLogger.Messages.Any(m => m.Contains(MemberUpn, StringComparison.OrdinalIgnoreCase)), Is.False);
        }

        // ---- End-to-end through the secret endpoints --------------------------------------

        [Test]
        public async Task RunList_GroupOnlyReadableSecret_AppearsWithEffectivePermissions()
        {
            // [GIVEN] The owner created a secret; it is shared only with a group the member belongs to.
            const string secret = "e2e-grouponly";
            await this.CreateSecret(this.ownerIdentity, secret);
            this.SeedMember(consentRequired: false, GroupA);
            this.SeedGroupPermission(secret, GroupA, PermissionType.Read | PermissionType.Write);

            // [WHEN] The member lists their secrets.
            var request = TestFactory.CreateHttpRequestData("get");
            var response = await this.secretMeta.RunList(request, new ClaimsPrincipal(this.memberIdentity), this.logger) as TestHttpResponseData;

            // [THEN] The group-only secret is present, with group-derived Write reflected as CanWrite.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var body = response.ReadBodyAsJson<BaseResponseObject<List<SubjectPermissionsOutput>>>();
            var item = body?.Result?.SingleOrDefault(p => p.ObjectName == secret);
            Assert.That(item, Is.Not.Null, "Group-only readable secret must appear in the caller's list.");
            Assert.That(item!.CanRead, Is.True);
            Assert.That(item.CanWrite, Is.True);
        }

        [Test]
        public async Task RunSecretRead_ReturnsCallerEffectivePermissions_IncludingGroupWrite()
        {
            // [GIVEN] The member has a direct Read grant and group-derived Write on a secret.
            const string secret = "e2e-effread";
            await this.CreateSecret(this.ownerIdentity, secret);
            this.SeedMember(consentRequired: false, GroupA);
            this.SeedDirectUserPermission(secret, MemberUpn, PermissionType.Read);
            this.SeedGroupPermission(secret, GroupA, PermissionType.Write);

            // [WHEN] The member reads the single secret.
            var request = TestFactory.CreateHttpRequestData("get");
            var response = await this.secretMeta.Run(request, secret, new ClaimsPrincipal(this.memberIdentity), this.logger) as TestHttpResponseData;

            // [THEN] The metadata carries the caller's effective permissions: Read + Write.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var body = response.ReadBodyAsJson<BaseResponseObject<ObjectMetadataOutput>>();
            var caller = body?.Result?.CallerPermissions;
            Assert.That(caller, Is.Not.Null, "Single-secret read must expose the caller's effective permissions.");
            Assert.That(caller!.CanRead, Is.True);
            Assert.That(caller.CanWrite, Is.True, "Group-derived Write must be reflected in effective permissions.");
            Assert.That(caller.CanGrantAccess, Is.False);
        }

        // ---- Helpers -----------------------------------------------------------------------

        private static CaseSensitiveClaimsIdentity Identity(string upn, string displayName, string oid)
            => new CaseSensitiveClaimsIdentity(new List<Claim>()
            {
                new Claim("upn", upn),
                new Claim("displayname", displayName),
                new Claim("oid", oid),
                new Claim("tid", "00000000-0000-0000-0000-000000000001"),
            }.AsEnumerable());

        private void SeedMember(bool consentRequired, params string[] groupIds)
        {
            var user = new User("Member User", "00000000-0000-0000-0000-0000000000a2", "00000000-0000-0000-0000-000000000001", MemberUpn, string.Empty)
            {
                ConsentRequired = consentRequired,
                Groups = groupIds.Select(id => new UserGroup { AadGroupId = id }).ToList()
            };
            this.dbContext.Users.Add(user);
            this.dbContext.SaveChanges();
        }

        private void SeedDirectUserPermission(string secret, string upn, PermissionType permission)
            => this.SeedPermission(secret, SubjectType.User, upn, upn, permission);

        private void SeedGroupPermission(string secret, string groupId, PermissionType permission)
            => this.SeedPermission(secret, SubjectType.Group, $"Group {groupId}", groupId, permission);

        private void SeedPermission(string secret, SubjectType subjectType, string subjectName, string subjectId, PermissionType permission)
        {
            this.dbContext.Permissions.Add(new SubjectPermissions(secret, subjectType, subjectName, subjectId)
            {
                CanRead = (permission & PermissionType.Read) == PermissionType.Read,
                CanWrite = (permission & PermissionType.Write) == PermissionType.Write,
                CanGrantAccess = (permission & PermissionType.GrantAccess) == PermissionType.GrantAccess,
                CanRevokeAccess = (permission & PermissionType.RevokeAccess) == PermissionType.RevokeAccess,
            });
            this.dbContext.SaveChanges();
        }

        private async Task CreateSecret(CaseSensitiveClaimsIdentity identity, string secretName)
        {
            var request = TestFactory.CreateHttpRequestData("post");
            request.SetBodyAsJson(new MetadataCreationInput()
            {
                ExpirationSettings = new ExpirationSettingsInput()
                {
                    ScheduleExpiration = false,
                    ExpireAt = DateTimeProvider.UtcNow + TimeSpan.FromDays(180),
                    ExpireOnIdleTime = false,
                    IdleTimeToExpire = TimeSpan.FromDays(180)
                }
            });

            var response = await this.secretMeta.Run(request, secretName, new ClaimsPrincipal(identity), this.logger) as TestHttpResponseData;
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        private sealed class CapturingLogger<T> : ILogger<T>
        {
            public List<string> Messages { get; } = new();

            public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => this.Messages.Add(formatter(state, exception));
        }
    }
}
