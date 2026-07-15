/// <summary>
/// EffectivePermissionsTests
///
/// Covers the "Proposition - SafeExchange 001" backend changes:
///  1. Group-authorization telemetry no longer contains the caller's raw user identifier
///     (it uses the pseudonymous telemetry id, matching the direct-authorization path).
///  2. PermissionsManager exposes the caller's *effective* permissions on a secret (the union of
///     direct and group-derived grants) and the set of effectively-readable secrets with both the
///     actual direct grant and the effective grant — which the secret list and the access endpoint
///     surface (actual permissions for display, effective permissions for capability checks).
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
        private const string GroupC = "cccc3333-cccc-3333-cccc-3333cccc3333";

        private const string MemberUpn = "member@test.test";
        private const string ApplicationId = "app-display-name";

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
        private SafeExchangeAccess secretAccess = null!;

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

            this.secretAccess = new SafeExchangeAccess(
                this.dbContext, this.groupsManager, this.tokenHelper,
                this.globalFilters, this.purger, this.permissionsManager,
                new NoOpAuditWriter(), Mock.Of<IOrphanedSecretManager>());
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
        public async Task GetEffectivePermissions_DirectOnly_ReturnsDirect()
        {
            const string secret = "eff-direct-only";
            this.SeedMember(consentRequired: false, GroupA);
            this.SeedDirectUserPermission(secret, MemberUpn, PermissionType.Read | PermissionType.Write);

            var effective = await this.permissionsManager.GetEffectivePermissionsAsync(SubjectType.User, MemberUpn, secret);

            Assert.That(effective, Is.EqualTo(PermissionType.Read | PermissionType.Write));
        }

        [Test]
        public async Task GetEffectivePermissions_DirectReadPlusGroupWrite_UnionsToReadWrite()
        {
            const string secret = "eff-union";
            this.SeedMember(consentRequired: false, GroupA);
            this.SeedDirectUserPermission(secret, MemberUpn, PermissionType.Read);
            this.SeedGroupPermission(secret, GroupA, PermissionType.Write);

            var effective = await this.permissionsManager.GetEffectivePermissionsAsync(SubjectType.User, MemberUpn, secret);

            Assert.That(effective, Is.EqualTo(PermissionType.Read | PermissionType.Write));
        }

        [Test]
        public async Task GetEffectivePermissions_GroupOnly_ReturnsGroupPermissions()
        {
            const string secret = "eff-group-only";
            this.SeedMember(consentRequired: false, GroupA);
            this.SeedGroupPermission(secret, GroupA, PermissionType.Read | PermissionType.GrantAccess);

            var effective = await this.permissionsManager.GetEffectivePermissionsAsync(SubjectType.User, MemberUpn, secret);

            Assert.That(effective, Is.EqualTo(PermissionType.Read | PermissionType.GrantAccess));
        }

        [Test]
        public async Task GetEffectivePermissions_MultipleGroups_UnionsPermissions()
        {
            const string secret = "eff-multi-group";
            this.SeedMember(consentRequired: false, GroupA, GroupB);
            this.SeedGroupPermission(secret, GroupA, PermissionType.Read);
            this.SeedGroupPermission(secret, GroupB, PermissionType.Read | PermissionType.GrantAccess);

            var effective = await this.permissionsManager.GetEffectivePermissionsAsync(SubjectType.User, MemberUpn, secret);

            Assert.That(effective, Is.EqualTo(PermissionType.Read | PermissionType.GrantAccess));
        }

        [Test]
        public async Task GetEffectivePermissions_ConsentRequired_ExcludesGroupPermissions()
        {
            const string secret = "eff-consent";
            this.SeedMember(consentRequired: true, GroupA);
            this.SeedGroupPermission(secret, GroupA, PermissionType.Read | PermissionType.Write);

            var effective = await this.permissionsManager.GetEffectivePermissionsAsync(SubjectType.User, MemberUpn, secret);

            Assert.That(effective, Is.EqualTo(PermissionType.None));
        }

        [Test]
        public async Task GetEffectivePermissions_GroupsFeatureDisabled_ReturnsDirectOnly()
        {
            const string secret = "eff-groups-off";
            this.SeedMember(consentRequired: false, GroupA);
            this.SeedDirectUserPermission(secret, MemberUpn, PermissionType.Read);
            this.SeedGroupPermission(secret, GroupA, PermissionType.Write);

            var noGroupsManager = this.CreatePermissionsManager(useGroupsAuthorization: false);
            var effective = await noGroupsManager.GetEffectivePermissionsAsync(SubjectType.User, MemberUpn, secret);

            Assert.That(effective, Is.EqualTo(PermissionType.Read));
        }

        [Test]
        public async Task GetEffectivePermissions_ApplicationCaller_UsesDirectOnly()
        {
            const string secret = "eff-app";
            this.SeedPermission(secret, SubjectType.Application, ApplicationId, ApplicationId, PermissionType.Read | PermissionType.Write);
            this.SeedGroupPermission(secret, GroupA, PermissionType.GrantAccess);

            var effective = await this.permissionsManager.GetEffectivePermissionsAsync(SubjectType.Application, ApplicationId, secret);

            Assert.That(effective, Is.EqualTo(PermissionType.Read | PermissionType.Write));
        }

        [Test]
        public async Task GetEffectivePermissions_GrantToUnrelatedGroup_IsExcluded()
        {
            const string secret = "eff-unrelated-group";
            this.SeedMember(consentRequired: false, GroupA);
            this.SeedDirectUserPermission(secret, MemberUpn, PermissionType.Read);
            this.SeedGroupPermission(secret, GroupB, PermissionType.Write); // member is not in GroupB

            var effective = await this.permissionsManager.GetEffectivePermissionsAsync(SubjectType.User, MemberUpn, secret);

            Assert.That(effective, Is.EqualTo(PermissionType.Read));
        }

        // ---- Readable secrets: actual (Direct) vs Effective -------------------------------

        [Test]
        public async Task GetReadableSecrets_DirectReadOnly_DirectEqualsEffective()
        {
            const string secret = "read-direct";
            this.SeedMember(consentRequired: false, GroupA);
            this.SeedDirectUserPermission(secret, MemberUpn, PermissionType.Read | PermissionType.Write);

            var readable = await this.permissionsManager.GetReadableSecretsAsync(SubjectType.User, MemberUpn);

            var item = readable.Single(e => e.SecretName == secret);
            Assert.That(item.Direct, Is.EqualTo(PermissionType.Read | PermissionType.Write));
            Assert.That(item.Effective, Is.EqualTo(PermissionType.Read | PermissionType.Write));
        }

        [Test]
        public async Task GetReadableSecrets_GroupOnlyRead_DirectEmpty_EffectiveFromGroup()
        {
            const string secret = "read-group-only";
            this.SeedMember(consentRequired: false, GroupA);
            this.SeedGroupPermission(secret, GroupA, PermissionType.Read | PermissionType.Write);

            var readable = await this.permissionsManager.GetReadableSecretsAsync(SubjectType.User, MemberUpn);

            var item = readable.Single(e => e.SecretName == secret);
            Assert.That(item.Direct, Is.EqualTo(PermissionType.None));
            Assert.That(item.Effective, Is.EqualTo(PermissionType.Read | PermissionType.Write));
        }

        [Test]
        public async Task GetReadableSecrets_DirectReadPlusGroupWrite_SplitsDirectAndEffective()
        {
            const string secret = "read-split";
            this.SeedMember(consentRequired: false, GroupA);
            this.SeedDirectUserPermission(secret, MemberUpn, PermissionType.Read);
            this.SeedGroupPermission(secret, GroupA, PermissionType.Write);

            var readable = await this.permissionsManager.GetReadableSecretsAsync(SubjectType.User, MemberUpn);

            var item = readable.Single(e => e.SecretName == secret);
            Assert.That(item.Direct, Is.EqualTo(PermissionType.Read));
            Assert.That(item.Effective, Is.EqualTo(PermissionType.Read | PermissionType.Write));
        }

        [Test]
        public async Task GetReadableSecrets_DirectAndGroupSamePermission_NoDuplicate()
        {
            const string secret = "read-dedupe";
            this.SeedMember(consentRequired: false, GroupA);
            this.SeedDirectUserPermission(secret, MemberUpn, PermissionType.Read);
            this.SeedGroupPermission(secret, GroupA, PermissionType.Read);

            var readable = await this.permissionsManager.GetReadableSecretsAsync(SubjectType.User, MemberUpn);

            Assert.That(readable.Count(e => e.SecretName == secret), Is.EqualTo(1));
        }

        [Test]
        public async Task GetReadableSecrets_GroupWriteWithoutRead_NotListed()
        {
            const string secret = "read-write-no-read";
            this.SeedMember(consentRequired: false, GroupA);
            this.SeedGroupPermission(secret, GroupA, PermissionType.Write);

            var readable = await this.permissionsManager.GetReadableSecretsAsync(SubjectType.User, MemberUpn);

            Assert.That(readable.Any(e => e.SecretName == secret), Is.False);
        }

        [Test]
        public async Task GetReadableSecrets_ConsentRequired_ExcludesGroupSecrets()
        {
            const string groupSecret = "read-consent-group";
            const string directSecret = "read-consent-direct";
            this.SeedMember(consentRequired: true, GroupA);
            this.SeedGroupPermission(groupSecret, GroupA, PermissionType.Read);
            this.SeedDirectUserPermission(directSecret, MemberUpn, PermissionType.Read);

            var readable = await this.permissionsManager.GetReadableSecretsAsync(SubjectType.User, MemberUpn);

            Assert.That(readable.Any(e => e.SecretName == directSecret), Is.True);
            Assert.That(readable.Any(e => e.SecretName == groupSecret), Is.False);
        }

        [Test]
        public async Task GetReadableSecrets_ApplicationCaller_DirectOnly()
        {
            const string directSecret = "read-app-direct";
            const string groupSecret = "read-app-group";
            this.SeedPermission(directSecret, SubjectType.Application, ApplicationId, ApplicationId, PermissionType.Read);
            this.SeedGroupPermission(groupSecret, GroupA, PermissionType.Read);

            var readable = await this.permissionsManager.GetReadableSecretsAsync(SubjectType.Application, ApplicationId);

            Assert.That(readable.Select(e => e.SecretName), Is.EqualTo(new[] { directSecret }));
        }

        [Test]
        public async Task GetReadableSecrets_MultipleGroupsAndSecrets_AllIncluded()
        {
            const string secretA = "read-multi-a";
            const string secretB = "read-multi-b";
            this.SeedMember(consentRequired: false, GroupA, GroupB);
            this.SeedGroupPermission(secretA, GroupA, PermissionType.Read);
            this.SeedGroupPermission(secretB, GroupB, PermissionType.Read | PermissionType.Write);

            var readable = await this.permissionsManager.GetReadableSecretsAsync(SubjectType.User, MemberUpn);

            Assert.That(readable.Single(e => e.SecretName == secretA).Effective, Is.EqualTo(PermissionType.Read));
            Assert.That(readable.Single(e => e.SecretName == secretB).Effective, Is.EqualTo(PermissionType.Read | PermissionType.Write));
        }

        // ---- Telemetry scrubbing -----------------------------------------------------------

        [Test]
        public async Task GroupAuthorization_Telemetry_UsesPseudonym_NotRawUserId()
        {
            const string secret = "telemetry-scrub";
            const string tid = "tid-eff-001";
            this.SeedMember(consentRequired: false, GroupA);
            this.SeedGroupPermission(secret, GroupA, PermissionType.Read);
            TelemetryContext.Current = tid;

            var authorized = await this.permissionsManager.IsAuthorizedAsync(SubjectType.User, MemberUpn, secret, PermissionType.Read);
            _ = await this.permissionsManager.IsAuthorizedAsync(SubjectType.User, MemberUpn, secret, PermissionType.RevokeAccess);

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

            Assert.That(this.permissionsLogger.Messages.Any(m => m.Contains(MemberUpn, StringComparison.OrdinalIgnoreCase)), Is.False);
        }

        // ---- End-to-end through the secret endpoints --------------------------------------

        [Test]
        public async Task RunList_GroupOnlyReadableSecret_ActualEmpty_EffectiveSet()
        {
            const string secret = "e2e-grouponly";
            await this.CreateSecret(this.ownerIdentity, secret);
            this.SeedMember(consentRequired: false, GroupA);
            this.SeedGroupPermission(secret, GroupA, PermissionType.Read | PermissionType.Write);

            var item = await this.ListSecretAsync(this.memberIdentity, secret);

            Assert.That(item, Is.Not.Null, "Group-only readable secret must appear in the caller's list.");
            Assert.That(item!.CanRead, Is.False, "Actual direct permissions are empty for a group-only secret.");
            Assert.That(item.CanWrite, Is.False);
            Assert.That(item.CallerEffectivePermissions.CanRead, Is.True);
            Assert.That(item.CallerEffectivePermissions.CanWrite, Is.True);
        }

        [Test]
        public async Task RunList_DirectReadPlusGroupWrite_ActualReadOnly_EffectiveReadWrite()
        {
            const string secret = "e2e-split";
            await this.CreateSecret(this.ownerIdentity, secret);
            this.SeedMember(consentRequired: false, GroupA);
            this.SeedDirectUserPermission(secret, MemberUpn, PermissionType.Read);
            this.SeedGroupPermission(secret, GroupA, PermissionType.Write);

            var item = await this.ListSecretAsync(this.memberIdentity, secret);

            Assert.That(item, Is.Not.Null);
            Assert.That(item!.CanRead, Is.True);
            Assert.That(item.CanWrite, Is.False, "Actual direct permissions do not include the group-derived Write.");
            Assert.That(item.CallerEffectivePermissions.CanWrite, Is.True, "Effective permissions include the group-derived Write.");
        }

        [Test]
        public async Task RunList_DirectOnlySecret_ActualEqualsEffective()
        {
            const string secret = "e2e-direct";
            await this.CreateSecret(this.ownerIdentity, secret);

            var item = await this.ListSecretAsync(this.ownerIdentity, secret);

            Assert.That(item, Is.Not.Null);
            Assert.That(item!.CanWrite, Is.True);
            Assert.That(item.CallerEffectivePermissions.CanWrite, Is.EqualTo(item.CanWrite));
            Assert.That(item.CallerEffectivePermissions.CanRead, Is.EqualTo(item.CanRead));
        }

        [Test]
        public async Task GetAccessList_ReturnsCallerEffectivePermissions_AndActualAccessList()
        {
            const string secret = "e2e-access-eff";
            await this.CreateSecret(this.ownerIdentity, secret);
            this.SeedMember(consentRequired: false, GroupA);
            this.SeedDirectUserPermission(secret, MemberUpn, PermissionType.Read);
            this.SeedGroupPermission(secret, GroupA, PermissionType.Write);

            var request = TestFactory.CreateHttpRequestData("get");
            var response = await this.secretAccess.Run(request, secret, new ClaimsPrincipal(this.memberIdentity), this.logger) as TestHttpResponseData;

            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var result = response.ReadBodyAsJson<BaseResponseObject<AccessListOutput>>()?.Result;
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.CallerEffectivePermissions.CanRead, Is.True);
            Assert.That(result.CallerEffectivePermissions.CanWrite, Is.True, "Group-derived Write must be reflected in the caller's effective permissions.");
            Assert.That(result.CallerEffectivePermissions.CanGrantAccess, Is.False);

            var memberRow = result.AccessList.SingleOrDefault(a => a.SubjectId == MemberUpn);
            Assert.That(memberRow, Is.Not.Null, "The access list reports each subject's actual permissions.");
            Assert.That(memberRow!.CanRead, Is.True);
            Assert.That(memberRow.CanWrite, Is.False, "The access list keeps the member's actual direct grant, not the effective one.");
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

        private PermissionsManager CreatePermissionsManager(bool useGroupsAuthorization)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    { "Features:UseNotifications", "False" },
                    { "Features:UseGroupsAuthorization", useGroupsAuthorization ? "True" : "False" }
                })
                .Build();

            return new PermissionsManager(configuration, this.dbContext, this.permissionsLogger);
        }

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

        private async Task<SecretListItemOutput?> ListSecretAsync(CaseSensitiveClaimsIdentity identity, string secretName)
        {
            var request = TestFactory.CreateHttpRequestData("get");
            var response = await this.secretMeta.RunList(request, new ClaimsPrincipal(identity), this.logger) as TestHttpResponseData;

            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var body = response.ReadBodyAsJson<BaseResponseObject<List<SecretListItemOutput>>>();
            return body?.Result?.SingleOrDefault(p => p.ObjectName == secretName);
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
