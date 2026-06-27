/// <summary>
/// SecretAccessPatchTests
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
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    [TestFixture]
    public class SecretAccessPatchTests
    {
        private ILogger logger;
        private SafeExchangeSecretMeta secretMeta;
        private SafeExchangeAccess secretAccess;
        private IConfiguration testConfiguration;
        private SafeExchangeDbContext dbContext;
        private IGroupsManager groupsManager;
        private ITokenHelper tokenHelper;
        private GlobalFilters globalFilters;
        private IBlobHelper blobHelper;
        private IPurger purger;
        private IPermissionsManager permissionsManager;
        private OrphanedSecretManager orphanedSecretManager;
        private Features features;
        private OrphanedSecretConfiguration orphanConfig;

        private CaseSensitiveClaimsIdentity firstIdentity;
        private CaseSensitiveClaimsIdentity secondIdentity;
        private CaseSensitiveClaimsIdentity thirdIdentity;

        private GroupDictionaryItem existingGroupOne;
        private GroupDictionaryItem existingGroupTwo;

        [OneTimeSetUp]
        public async Task OneTimeSetup()
        {
            var builder = new ConfigurationBuilder().AddUserSecrets<SecretAccessPatchTests>();
            var secretConfiguration = builder.Build();

            this.logger = TestFactory.CreateLogger();

            var configurationValues = new Dictionary<string, string>
            {
                {"Features:UseAccessGiveUp", "True"},
                {"Features:UseGroupsAuthorization", "True"}
            };

            this.testConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(configurationValues!)
                .Build();

            var dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(secretConfiguration.GetConnectionString("CosmosDb"),
                    databaseName: $"{nameof(SecretAccessPatchTests)}Database",
                    CosmosTestOptions.UseGateway)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CosmosEventId.SyncNotSupported))
                .Options;

            this.dbContext = new SafeExchangeDbContext(dbContextOptions);
            await this.dbContext.Database.EnsureCreatedAsync();

            this.groupsManager = new GroupsManager(this.dbContext, Mock.Of<ILogger<GroupsManager>>());
            this.tokenHelper = new TestTokenHelper();

            GloballyAllowedGroupsConfiguration gagc = new GloballyAllowedGroupsConfiguration();
            var groupsConfiguration = Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(x => x.CurrentValue == gagc);

            AdminConfiguration ac = new AdminConfiguration();
            var adminConfiguration = Mock.Of<IOptionsMonitor<AdminConfiguration>>(x => x.CurrentValue == ac);

            this.globalFilters = new GlobalFilters(groupsConfiguration, adminConfiguration, this.tokenHelper, TestFactory.CreateLogger<GlobalFilters>());
            this.blobHelper = new TestBlobHelper();
            this.purger = new PurgeManager(this.testConfiguration, this.blobHelper, TestFactory.CreateLogger<PurgeManager>());
            this.permissionsManager = new PermissionsManager(this.testConfiguration, this.dbContext, TestFactory.CreateLogger<PermissionsManager>());

            this.features = new Features { UseAccessGiveUp = true, UseGroupsAuthorization = true };
            this.orphanConfig = new OrphanedSecretConfiguration { Ownership = OrphanOwnershipMode.UserOrApp, GracePeriod = TimeSpan.FromDays(7) };

            var featuresOptions = Mock.Of<IOptionsMonitor<Features>>(x => x.CurrentValue == this.features);
            var configOptions = Mock.Of<IOptionsMonitor<OrphanedSecretConfiguration>>(x => x.CurrentValue == this.orphanConfig);

            this.orphanedSecretManager = new OrphanedSecretManager(featuresOptions, configOptions, TestFactory.CreateLogger<OrphanedSecretManager>());

            this.secretMeta = new SafeExchangeSecretMeta(
                this.testConfiguration, this.dbContext, this.tokenHelper,
                this.globalFilters, this.purger, this.permissionsManager, new NoOpAuditWriter());

            this.secretAccess = new SafeExchangeAccess(
                this.dbContext, this.groupsManager, this.tokenHelper, this.globalFilters,
                this.purger, this.permissionsManager, new NoOpAuditWriter(), this.orphanedSecretManager);

            this.firstIdentity = new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new Claim("upn", "first@test.test"),
                new Claim("displayname", "First User"),
                new Claim("oid", "00000000-0000-0000-0000-000000000001"),
                new Claim("tid", "00000000-0000-0000-0000-000000000001"),
            }.AsEnumerable());

            this.secondIdentity = new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new Claim("upn", "second@test.test"),
                new Claim("displayname", "Second User"),
                new Claim("oid", "00000000-0000-0000-0000-000000000002"),
                new Claim("tid", "00000000-0000-0000-0000-000000000001"),
            }.AsEnumerable());

            this.thirdIdentity = new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new Claim("upn", "third@test.test"),
                new Claim("displayname", "Third User"),
                new Claim("oid", "00000000-0000-0000-0000-000000000003"),
                new Claim("tid", "00000000-0000-0000-0000-000000000001"),
            }.AsEnumerable());

            this.existingGroupOne = await this.groupsManager.PutGroupAsync(
                "01010101-0101-0101-0101-010101010101",
                new GroupInput { DisplayName = "Existing Test Group One", Mail = "existing.test.group.one@test.test" },
                SubjectType.User, "test@test.test");

            this.existingGroupTwo = await this.groupsManager.PutGroupAsync(
                "02020202-0202-0202-0202-020202020202",
                new GroupInput { DisplayName = "Existing Test Group Two" },
                SubjectType.User, "test@test.test");

            var workerOptions = Options.Create(new WorkerOptions() { Serializer = new JsonObjectSerializer() });
            var serviceProviderMock = new Mock<IServiceProvider>();
            serviceProviderMock
                .Setup(x => x.GetService(typeof(IOptions<WorkerOptions>)))
                .Returns(workerOptions);
            TestFactory.FunctionContext.InstanceServices = serviceProviderMock.Object;
        }

        [OneTimeTearDown]
        public async Task OneTimeTearDown()
        {
            await this.dbContext.Database.EnsureDeletedAsync();
            this.dbContext.Dispose();
        }

        [SetUp]
        public void SetupBeforeTest()
        {
            this.features.UseAccessGiveUp = true;
            this.orphanConfig.Ownership = OrphanOwnershipMode.UserOrApp;
            this.orphanConfig.GracePeriod = TimeSpan.FromDays(7);

            DateTimeProvider.UseSpecifiedDateTime = true;
            DateTimeProvider.SpecifiedDateTime = new DateTime(2026, 5, 6, 9, 0, 0, DateTimeKind.Utc);
        }

        [TearDown]
        public void Cleanup()
        {
            DateTimeProvider.UseSpecifiedDateTime = false;

            this.dbContext.ChangeTracker.Clear();
            this.dbContext.Permissions.RemoveRange(this.dbContext.Permissions.ToList());
            this.dbContext.Objects.RemoveRange(this.dbContext.Objects.ToList());
            this.dbContext.SaveChanges();
        }

        // ---- Tests ----

        [Test]
        public async Task Patch_EmptyBody_Returns400()
        {
            // [GIVEN] A secret exists owned by firstIdentity.
            await CreateSecret(this.firstIdentity, "secret-1");

            // [WHEN] A PATCH request is made with both add and remove empty.
            var request = TestFactory.CreateHttpRequestData("patch");
            request.SetBodyAsJson(new AccessUpdateInput
            {
                Add = new List<SubjectPermissionsInput>(),
                Remove = new List<SubjectPermissionsInput>()
            });
            var response = await this.secretAccess.Run(request, "secret-1",
                new ClaimsPrincipal(this.firstIdentity), this.logger);

            // [THEN] 400 Bad Request is returned.
            Assert.That((response as TestHttpResponseData)?.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        }

        [Test]
        public async Task Patch_AddWithoutGrantAccess_Returns403()
        {
            // [GIVEN] A secret exists; secondIdentity has Read, Write and RevokeAccess — but NOT GrantAccess.
            await CreateSecret(this.firstIdentity, "secret-1");
            this.dbContext.Permissions.Add(new SubjectPermissions("secret-1", SubjectType.User, "second@test.test")
            {
                CanRead = true,
                CanWrite = true,
                CanGrantAccess = false,
                CanRevokeAccess = true
            });
            await this.dbContext.SaveChangesAsync();
            this.dbContext.ChangeTracker.Clear();

            // [WHEN] secondIdentity tries to add thirdIdentity via PATCH.
            var request = TestFactory.CreateHttpRequestData("patch");
            request.SetBodyAsJson(new AccessUpdateInput
            {
                Add = new List<SubjectPermissionsInput>
                {
                    new SubjectPermissionsInput
                    {
                        SubjectType = SubjectTypeInput.User,
                        SubjectName = "third@test.test",
                        SubjectId = "third@test.test",
                        CanRead = true, CanWrite = false,
                        CanGrantAccess = false, CanRevokeAccess = false
                    }
                }
            });
            var response = await this.secretAccess.Run(request, "secret-1",
                new ClaimsPrincipal(this.secondIdentity), this.logger);

            // [THEN] 403 Forbidden is returned.
            Assert.That((response as TestHttpResponseData)?.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        }

        [Test]
        public async Task Patch_RemoveWithoutRevokeAccess_Returns403()
        {
            // [GIVEN] A secret exists; secondIdentity has Read, Write and GrantAccess — but NOT RevokeAccess.
            await CreateSecret(this.firstIdentity, "secret-1");
            this.dbContext.Permissions.Add(new SubjectPermissions("secret-1", SubjectType.User, "second@test.test")
            {
                CanRead = true,
                CanWrite = true,
                CanGrantAccess = true,
                CanRevokeAccess = false
            });
            await this.dbContext.SaveChangesAsync();
            this.dbContext.ChangeTracker.Clear();

            // [WHEN] secondIdentity tries to remove firstIdentity via PATCH.
            var request = TestFactory.CreateHttpRequestData("patch");
            request.SetBodyAsJson(new AccessUpdateInput
            {
                Remove = new List<SubjectPermissionsInput>
                {
                    new SubjectPermissionsInput
                    {
                        SubjectType = SubjectTypeInput.User,
                        SubjectName = "first@test.test",
                        SubjectId = "first@test.test",
                        CanRead = true, CanWrite = true,
                        CanGrantAccess = true, CanRevokeAccess = true
                    }
                }
            });
            var response = await this.secretAccess.Run(request, "secret-1",
                new ClaimsPrincipal(this.secondIdentity), this.logger);

            // [THEN] 403 Forbidden is returned.
            Assert.That((response as TestHttpResponseData)?.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        }

        [Test]
        public async Task Patch_SwapCustodian_NoOrphan()
        {
            // [GIVEN] firstIdentity is the sole custodian (Full permissions).
            await CreateSecret(this.firstIdentity, "secret-1");

            // [WHEN] A PATCH atomically removes firstIdentity and adds thirdIdentity as Full custodian.
            var request = TestFactory.CreateHttpRequestData("patch");
            request.SetBodyAsJson(new AccessUpdateInput
            {
                Remove = new List<SubjectPermissionsInput>
                {
                    new SubjectPermissionsInput
                    {
                        SubjectType = SubjectTypeInput.User,
                        SubjectName = "first@test.test",
                        SubjectId = "first@test.test",
                        CanRead = true, CanWrite = true,
                        CanGrantAccess = true, CanRevokeAccess = true
                    }
                },
                Add = new List<SubjectPermissionsInput>
                {
                    new SubjectPermissionsInput
                    {
                        SubjectType = SubjectTypeInput.User,
                        SubjectName = "third@test.test",
                        SubjectId = "third@test.test",
                        CanRead = true, CanWrite = true,
                        CanGrantAccess = true, CanRevokeAccess = true
                    }
                }
            });
            var response = await this.secretAccess.Run(request, "secret-1",
                new ClaimsPrincipal(this.firstIdentity), this.logger);

            // [THEN] OK is returned.
            Assert.That((response as TestHttpResponseData)?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // [THEN] thirdIdentity has Full permissions.
            this.dbContext.ChangeTracker.Clear();
            var thirdRow = await this.dbContext.Permissions
                .FirstOrDefaultAsync(p => p.SecretName == "secret-1" && p.SubjectId == "third@test.test");
            Assert.That(thirdRow, Is.Not.Null);
            Assert.That(thirdRow!.CanGrantAccess, Is.True);

            // [THEN] firstIdentity row is removed.
            var firstRow = await this.dbContext.Permissions
                .FirstOrDefaultAsync(p => p.SecretName == "secret-1" && p.SubjectId == "first@test.test");
            Assert.That(firstRow, Is.Null);

            // [THEN] No orphan schedule applied because a new custodian was added.
            var metadata = await this.dbContext.Objects.FindAsync("secret-1");
            Assert.That(metadata!.ExpirationMetadata.ScheduleExpiration, Is.False);
        }

        [Test]
        public async Task Patch_SelfRemovalLastCustodian_Orphans()
        {
            // [GIVEN] firstIdentity is the sole custodian.
            await CreateSecret(this.firstIdentity, "secret-1");

            // [WHEN] firstIdentity removes itself via PATCH with no add.
            var request = TestFactory.CreateHttpRequestData("patch");
            request.SetBodyAsJson(new AccessUpdateInput
            {
                Remove = new List<SubjectPermissionsInput>
                {
                    new SubjectPermissionsInput
                    {
                        SubjectType = SubjectTypeInput.User,
                        SubjectName = "first@test.test",
                        SubjectId = "first@test.test",
                        CanRead = true, CanWrite = true,
                        CanGrantAccess = true, CanRevokeAccess = true
                    }
                }
            });
            var response = await this.secretAccess.Run(request, "secret-1",
                new ClaimsPrincipal(this.firstIdentity), this.logger);

            // [THEN] OK is returned.
            Assert.That((response as TestHttpResponseData)?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // [THEN] Expiration schedule is applied to now + grace period.
            this.dbContext.ChangeTracker.Clear();
            var metadata = await this.dbContext.Objects.FindAsync("secret-1");
            Assert.That(metadata!.ExpirationMetadata.ScheduleExpiration, Is.True);
            Assert.That(metadata.ExpirationMetadata.ExpireAt, Is.EqualTo(DateTimeProvider.UtcNow.AddDays(7)));
        }

        [Test]
        public async Task Patch_FeatureFlagOff_RemoveLastCustodian_NoSchedule()
        {
            // [GIVEN] Feature flag is off; firstIdentity is the sole custodian.
            this.features.UseAccessGiveUp = false;
            await CreateSecret(this.firstIdentity, "secret-1");

            // [WHEN] firstIdentity removes itself via PATCH.
            var request = TestFactory.CreateHttpRequestData("patch");
            request.SetBodyAsJson(new AccessUpdateInput
            {
                Remove = new List<SubjectPermissionsInput>
                {
                    new SubjectPermissionsInput
                    {
                        SubjectType = SubjectTypeInput.User,
                        SubjectName = "first@test.test",
                        SubjectId = "first@test.test",
                        CanRead = true, CanWrite = true,
                        CanGrantAccess = true, CanRevokeAccess = true
                    }
                }
            });
            var response = await this.secretAccess.Run(request, "secret-1",
                new ClaimsPrincipal(this.firstIdentity), this.logger);

            // [THEN] OK is returned.
            Assert.That((response as TestHttpResponseData)?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // [THEN] No expiration schedule applied because the feature is off.
            this.dbContext.ChangeTracker.Clear();
            var metadata = await this.dbContext.Objects.FindAsync("secret-1");
            Assert.That(metadata!.ExpirationMetadata.ScheduleExpiration, Is.False);
        }

        [Test]
        public async Task Delete_ExistingEndpoint_FeatureOn_RevokeLastCustodian_Orphans()
        {
            // [GIVEN] Feature flag is on; firstIdentity is the sole custodian.
            await CreateSecret(this.firstIdentity, "secret-1");

            // [WHEN] firstIdentity revokes their own Full access via the DELETE endpoint.
            var deleteRequest = TestFactory.CreateHttpRequestData("delete");
            deleteRequest.SetBodyAsJson(new List<SubjectPermissionsInput>
            {
                new SubjectPermissionsInput
                {
                    SubjectType = SubjectTypeInput.User,
                    SubjectName = "first@test.test",
                    SubjectId = "first@test.test",
                    CanRead = true, CanWrite = true,
                    CanGrantAccess = true, CanRevokeAccess = true
                }
            });
            var response = await this.secretAccess.Run(deleteRequest, "secret-1",
                new ClaimsPrincipal(this.firstIdentity), this.logger);

            // [THEN] OK is returned.
            Assert.That((response as TestHttpResponseData)?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // [THEN] Expiration schedule applied to now + grace period.
            this.dbContext.ChangeTracker.Clear();
            var metadata = await this.dbContext.Objects.FindAsync("secret-1");
            Assert.That(metadata!.ExpirationMetadata.ScheduleExpiration, Is.True);
            Assert.That(metadata.ExpirationMetadata.ExpireAt, Is.EqualTo(DateTimeProvider.UtcNow.AddDays(7)));
        }

        [Test]
        public async Task Delete_ExistingEndpoint_FeatureOff_RevokeLastCustodian_NoOrphan()
        {
            // [GIVEN] Feature flag is off; firstIdentity is the sole custodian.
            this.features.UseAccessGiveUp = false;
            await CreateSecret(this.firstIdentity, "secret-1");

            // [WHEN] firstIdentity revokes their own Full access via the DELETE endpoint.
            var deleteRequest = TestFactory.CreateHttpRequestData("delete");
            deleteRequest.SetBodyAsJson(new List<SubjectPermissionsInput>
            {
                new SubjectPermissionsInput
                {
                    SubjectType = SubjectTypeInput.User,
                    SubjectName = "first@test.test",
                    SubjectId = "first@test.test",
                    CanRead = true, CanWrite = true,
                    CanGrantAccess = true, CanRevokeAccess = true
                }
            });
            var response = await this.secretAccess.Run(deleteRequest, "secret-1",
                new ClaimsPrincipal(this.firstIdentity), this.logger);

            // [THEN] OK is returned.
            Assert.That((response as TestHttpResponseData)?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // [THEN] No expiration schedule applied because the feature is off.
            this.dbContext.ChangeTracker.Clear();
            var metadata = await this.dbContext.Objects.FindAsync("secret-1");
            Assert.That(metadata!.ExpirationMetadata.ScheduleExpiration, Is.False);
        }

        [Test]
        public async Task Patch_RemoveThenAddSameSubject_BroadensPermissions_DoesNotVanish()
        {
            // [GIVEN] firstIdentity owns the secret; secondIdentity has Read only.
            await CreateSecret(this.firstIdentity, "secret-1");
            this.dbContext.Permissions.Add(new SubjectPermissions("secret-1", SubjectType.User, "second@test.test")
            {
                CanRead = true,
                CanWrite = false,
                CanGrantAccess = false,
                CanRevokeAccess = false
            });
            await this.dbContext.SaveChangesAsync();
            this.dbContext.ChangeTracker.Clear();

            // [WHEN] firstIdentity PATCHes: remove second@ (Read) and re-add the SAME second@ with broader
            // Read+Write, in one atomic request — the "delete then re-add to widen permissions" UI flow.
            var request = TestFactory.CreateHttpRequestData("patch");
            request.SetBodyAsJson(new AccessUpdateInput
            {
                Remove = new List<SubjectPermissionsInput>
                {
                    new SubjectPermissionsInput
                    {
                        SubjectType = SubjectTypeInput.User,
                        SubjectName = "second@test.test",
                        SubjectId = "second@test.test",
                        CanRead = true, CanWrite = false,
                        CanGrantAccess = false, CanRevokeAccess = false
                    }
                },
                Add = new List<SubjectPermissionsInput>
                {
                    new SubjectPermissionsInput
                    {
                        SubjectType = SubjectTypeInput.User,
                        SubjectName = "second@test.test",
                        SubjectId = "second@test.test",
                        CanRead = true, CanWrite = true,
                        CanGrantAccess = false, CanRevokeAccess = false
                    }
                }
            });
            var response = await this.secretAccess.Run(request, "secret-1",
                new ClaimsPrincipal(this.firstIdentity), this.logger);

            // [THEN] OK is returned.
            Assert.That((response as TestHttpResponseData)?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // [THEN] second@ is STILL present and now has the broadened Read+Write — it must not vanish.
            this.dbContext.ChangeTracker.Clear();
            var row = await this.dbContext.Permissions
                .FirstOrDefaultAsync(p => p.SecretName == "secret-1" && p.SubjectId == "second@test.test");
            Assert.That(row, Is.Not.Null, "subject must not disappear when removed and re-added in the same PATCH");
            Assert.That(row!.CanRead, Is.True);
            Assert.That(row.CanWrite, Is.True);
            Assert.That(row.CanGrantAccess, Is.False);
            Assert.That(row.CanRevokeAccess, Is.False);
        }

        // ---- PATCH with Application target ----

        [Test]
        public async Task Patch_AddApplication_PersistsPermissionRow()
        {
            // [GIVEN] firstIdentity owns the secret.
            await CreateSecret(this.firstIdentity, "patch-app");

            // [WHEN] firstIdentity adds an application via PATCH.
            var request = TestFactory.CreateHttpRequestData("patch");
            request.SetBodyAsJson(new AccessUpdateInput
            {
                Add = new List<SubjectPermissionsInput>
                {
                    new SubjectPermissionsInput
                    {
                        SubjectType = SubjectTypeInput.Application,
                        SubjectName = "test-app",
                        CanRead = true, CanWrite = true,
                    }
                }
            });
            var response = await this.secretAccess.Run(request, "patch-app", new ClaimsPrincipal(this.firstIdentity), this.logger);

            // [THEN] OK is returned, Application row is persisted.
            Assert.That((response as TestHttpResponseData)?.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var row = (await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("patch-app") && p.SubjectType == SubjectType.Application)
                .ToListAsync()).Single();
            Assert.That(row.SubjectId, Is.EqualTo("test-app"));
            Assert.That(row.CanRead, Is.True);
            Assert.That(row.CanWrite, Is.True);
            Assert.That(row.CanGrantAccess, Is.False);
            Assert.That(row.CanRevokeAccess, Is.False);
        }

        [Test]
        public async Task Patch_RemoveApplication_RemovesPermissionRow()
        {
            // [GIVEN] firstIdentity granted full access to an application.
            await CreateSecret(this.firstIdentity, "patch-app2");
            var postRequest = TestFactory.CreateHttpRequestData("post");
            postRequest.SetBodyAsJson(new List<SubjectPermissionsInput>
            {
                new SubjectPermissionsInput
                {
                    SubjectType = SubjectTypeInput.Application,
                    SubjectName = "test-app",
                    CanRead = true, CanWrite = true, CanGrantAccess = true, CanRevokeAccess = true,
                }
            });
            await this.secretAccess.Run(postRequest, "patch-app2", new ClaimsPrincipal(this.firstIdentity), this.logger);

            // [WHEN] firstIdentity removes the application via PATCH.
            var request = TestFactory.CreateHttpRequestData("patch");
            request.SetBodyAsJson(new AccessUpdateInput
            {
                Remove = new List<SubjectPermissionsInput>
                {
                    new SubjectPermissionsInput
                    {
                        SubjectType = SubjectTypeInput.Application,
                        SubjectName = "test-app",
                        CanRead = true, CanWrite = true, CanGrantAccess = true, CanRevokeAccess = true,
                    }
                }
            });
            var response = await this.secretAccess.Run(request, "patch-app2", new ClaimsPrincipal(this.firstIdentity), this.logger);

            Assert.That((response as TestHttpResponseData)?.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var rows = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("patch-app2") && p.SubjectType == SubjectType.Application)
                .ToListAsync();
            Assert.That(rows, Is.Empty);
        }

        // ---- PATCH with Group target — old-API (mail) and new-API (GUID) ----

        [Test]
        public async Task Patch_AddGroup_OldApi_ByMail_ResolvesToGroupId()
        {
            // [GIVEN] firstIdentity owns the secret. existingGroupOne has a known mail.
            await CreateSecret(this.firstIdentity, "patch-group-mail");

            // [WHEN] firstIdentity adds the group by mail (no SubjectId).
            var request = TestFactory.CreateHttpRequestData("patch");
            request.SetBodyAsJson(new AccessUpdateInput
            {
                Add = new List<SubjectPermissionsInput>
                {
                    new SubjectPermissionsInput
                    {
                        SubjectType = SubjectTypeInput.Group,
                        SubjectName = this.existingGroupOne.GroupMail!,
                        CanRead = true, CanWrite = true,
                    }
                }
            });
            var response = await this.secretAccess.Run(request, "patch-group-mail", new ClaimsPrincipal(this.firstIdentity), this.logger);

            // [THEN] The persisted row is keyed by GroupId (mail unified to GUID).
            Assert.That((response as TestHttpResponseData)?.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var row = (await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("patch-group-mail") && p.SubjectType == SubjectType.Group)
                .ToListAsync()).Single();
            Assert.That(row.SubjectId, Is.EqualTo(this.existingGroupOne.GroupId));
            Assert.That(row.SubjectName, Is.EqualTo(this.existingGroupOne.DisplayName));
            Assert.That(row.CanRead, Is.True);
            Assert.That(row.CanWrite, Is.True);
        }

        [Test]
        public async Task Patch_AddGroup_NewApi_ByGuid_StoresUnderGuid()
        {
            // [GIVEN] firstIdentity owns the secret. existingGroupTwo has only a GUID.
            await CreateSecret(this.firstIdentity, "patch-group-guid");

            // [WHEN] firstIdentity adds the group by GUID.
            var request = TestFactory.CreateHttpRequestData("patch");
            request.SetBodyAsJson(new AccessUpdateInput
            {
                Add = new List<SubjectPermissionsInput>
                {
                    new SubjectPermissionsInput
                    {
                        SubjectType = SubjectTypeInput.Group,
                        SubjectId = this.existingGroupTwo.GroupId,
                        SubjectName = this.existingGroupTwo.DisplayName,
                        CanRead = true, CanWrite = true,
                    }
                }
            });
            var response = await this.secretAccess.Run(request, "patch-group-guid", new ClaimsPrincipal(this.firstIdentity), this.logger);

            Assert.That((response as TestHttpResponseData)?.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var row = (await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("patch-group-guid") && p.SubjectType == SubjectType.Group)
                .ToListAsync()).Single();
            Assert.That(row.SubjectId, Is.EqualTo(this.existingGroupTwo.GroupId));
            Assert.That(row.SubjectName, Is.EqualTo(this.existingGroupTwo.DisplayName));
        }

        [Test]
        public async Task Patch_AddInexistentGroup_OldApi_NoOp()
        {
            // [GIVEN] firstIdentity owns the secret.
            await CreateSecret(this.firstIdentity, "patch-group-noop");

            // [WHEN] firstIdentity adds a non-existent mail-only group.
            var request = TestFactory.CreateHttpRequestData("patch");
            request.SetBodyAsJson(new AccessUpdateInput
            {
                Add = new List<SubjectPermissionsInput>
                {
                    new SubjectPermissionsInput
                    {
                        SubjectType = SubjectTypeInput.Group,
                        SubjectName = "missing.group@test.test",
                        CanRead = true,
                    }
                }
            });
            var response = await this.secretAccess.Run(request, "patch-group-noop", new ClaimsPrincipal(this.firstIdentity), this.logger);

            // [THEN] OK with no group row created.
            Assert.That((response as TestHttpResponseData)?.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var rows = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("patch-group-noop") && p.SubjectType == SubjectType.Group)
                .ToListAsync();
            Assert.That(rows, Is.Empty);
        }

        [Test]
        public async Task Patch_RemoveGroup_NewApi_ByGuid_RemovesRow()
        {
            // [GIVEN] firstIdentity granted access to existingGroupTwo (GUID-based).
            await CreateSecret(this.firstIdentity, "patch-group-rem-guid");
            var post = TestFactory.CreateHttpRequestData("post");
            post.SetBodyAsJson(new List<SubjectPermissionsInput>
            {
                new SubjectPermissionsInput
                {
                    SubjectType = SubjectTypeInput.Group,
                    SubjectId = this.existingGroupTwo.GroupId,
                    SubjectName = this.existingGroupTwo.DisplayName,
                    CanRead = true, CanWrite = true,
                }
            });
            await this.secretAccess.Run(post, "patch-group-rem-guid", new ClaimsPrincipal(this.firstIdentity), this.logger);

            // [WHEN] firstIdentity removes the group via PATCH (using the same GUID flavour).
            var request = TestFactory.CreateHttpRequestData("patch");
            request.SetBodyAsJson(new AccessUpdateInput
            {
                Remove = new List<SubjectPermissionsInput>
                {
                    new SubjectPermissionsInput
                    {
                        SubjectType = SubjectTypeInput.Group,
                        SubjectId = this.existingGroupTwo.GroupId,
                        SubjectName = this.existingGroupTwo.DisplayName,
                        CanRead = true, CanWrite = true,
                    }
                }
            });
            var response = await this.secretAccess.Run(request, "patch-group-rem-guid", new ClaimsPrincipal(this.firstIdentity), this.logger);

            Assert.That((response as TestHttpResponseData)?.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var rows = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("patch-group-rem-guid") && p.SubjectType == SubjectType.Group)
                .ToListAsync();
            Assert.That(rows, Is.Empty);
        }

        [Test]
        public async Task Patch_RemoveGroup_OldApi_ByMail_RemovesRow()
        {
            // [GIVEN] firstIdentity granted access to existingGroupOne by mail (unified to GroupId).
            await CreateSecret(this.firstIdentity, "patch-group-rem-mail");
            var post = TestFactory.CreateHttpRequestData("post");
            post.SetBodyAsJson(new List<SubjectPermissionsInput>
            {
                new SubjectPermissionsInput
                {
                    SubjectType = SubjectTypeInput.Group,
                    SubjectName = this.existingGroupOne.GroupMail!,
                    CanRead = true, CanWrite = true,
                }
            });
            await this.secretAccess.Run(post, "patch-group-rem-mail", new ClaimsPrincipal(this.firstIdentity), this.logger);

            // [WHEN] firstIdentity removes the group via PATCH using the same mail flavour.
            var request = TestFactory.CreateHttpRequestData("patch");
            request.SetBodyAsJson(new AccessUpdateInput
            {
                Remove = new List<SubjectPermissionsInput>
                {
                    new SubjectPermissionsInput
                    {
                        SubjectType = SubjectTypeInput.Group,
                        SubjectName = this.existingGroupOne.GroupMail!,
                        CanRead = true, CanWrite = true,
                    }
                }
            });
            var response = await this.secretAccess.Run(request, "patch-group-rem-mail", new ClaimsPrincipal(this.firstIdentity), this.logger);

            Assert.That((response as TestHttpResponseData)?.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var rows = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("patch-group-rem-mail") && p.SubjectType == SubjectType.Group)
                .ToListAsync();
            Assert.That(rows, Is.Empty);
        }

        // ---- Helpers ----

        private async Task CreateSecret(CaseSensitiveClaimsIdentity identity, string secretName)
        {
            var claimsPrincipal = new ClaimsPrincipal(identity);
            var request = TestFactory.CreateHttpRequestData("post");
            var creationInput = new MetadataCreationInput
            {
                ExpirationSettings = new ExpirationSettingsInput
                {
                    ScheduleExpiration = false,
                    ExpireAt = DateTimeProvider.UtcNow + TimeSpan.FromDays(180),
                    ExpireOnIdleTime = false,
                    IdleTimeToExpire = TimeSpan.FromDays(180)
                }
            };

            request.SetBodyAsJson(creationInput);
            var response = await this.secretMeta.Run(request, secretName, claimsPrincipal, this.logger);
            Assert.That((response as TestHttpResponseData)?.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
    }
}
