/// <summary>
/// SecretAccessTests
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
    public class SecretAccessTests
    {
        private ILogger logger;

        private SafeExchangeSecretMeta secretMeta;

        private SafeExchangeAccess secretAccess;

        private IConfiguration testConfiguration;

        private SafeExchangeDbContext dbContext;

        private IGroupsManager groupsManager;

        private ITokenHelper tokenHelper;

        private TestGraphDataProvider graphDataProvider;

        private GlobalFilters globalFilters;

        private IBlobHelper blobHelper;

        private IPurger purger;

        private IPermissionsManager permissionsManager;

        private CaseSensitiveClaimsIdentity firstIdentity;
        private CaseSensitiveClaimsIdentity secondIdentity;
        private CaseSensitiveClaimsIdentity thirdIdentity;

        private GroupDictionaryItem existingGroupOne;
        private GroupDictionaryItem existingGroupTwo;

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            var builder = new ConfigurationBuilder().AddUserSecrets<SecretAccessTests>();
            var secretConfiguration = builder.Build();

            this.logger = TestFactory.CreateLogger();

            var configurationValues = new Dictionary<string, string>
                {
                    {"Features:UseNotifications", "False"},
                    {"Features:UseGroupsAuthorization", "True"}
                };

            this.testConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(configurationValues)
                .Build();

            var dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(secretConfiguration.GetConnectionString("CosmosDb"), databaseName: $"{nameof(SecretAccessTests)}Database")
                .Options;

            this.dbContext = new SafeExchangeDbContext(dbContextOptions);
            this.dbContext.Database.EnsureCreated();

            this.groupsManager = new GroupsManager(this.dbContext, Mock.Of<ILogger<GroupsManager>>());
            this.tokenHelper = new TestTokenHelper();
            this.graphDataProvider = new TestGraphDataProvider();

            GloballyAllowedGroupsConfiguration gagc = new GloballyAllowedGroupsConfiguration();
            var groupsConfiguration = Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(x => x.CurrentValue == gagc);

            AdminConfiguration ac = new AdminConfiguration();
            var adminConfiguration = Mock.Of<IOptionsMonitor<AdminConfiguration>>(x => x.CurrentValue == ac);

            this.globalFilters = new GlobalFilters(groupsConfiguration, adminConfiguration, this.tokenHelper, TestFactory.CreateLogger<GlobalFilters>());

            this.blobHelper = new TestBlobHelper();
            this.purger = new PurgeManager(this.testConfiguration, this.blobHelper, TestFactory.CreateLogger<PurgeManager>());

            this.permissionsManager = new PermissionsManager(this.testConfiguration, this.dbContext, TestFactory.CreateLogger<PermissionsManager>());

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

            this.thirdIdentity = new CaseSensitiveClaimsIdentity(new List<Claim>()
                {
                    new Claim("upn", "third@test.test"),
                    new Claim("displayname", "Third User"),
                    new Claim("oid", "00000000-0000-0000-0000-000000000003"),
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

            this.secretAccess = new SafeExchangeAccess(
                this.dbContext, this.groupsManager, this.tokenHelper,
                this.globalFilters, this.purger, this.permissionsManager);

            var groupOneId = "01010101-0101-0101-0101-010101010101";
            var groupOneInput = new GroupInput()
            {
                DisplayName = "Existing Test Group One",
                Mail = "existing.test.group.one@test.test"
            };

            this.existingGroupOne = this.groupsManager
                .PutGroupAsync(groupOneId, groupOneInput, SubjectType.User, "test@test.test")
                .GetAwaiter().GetResult();

            var groupTwoId = "02020202-0202-0202-0202-020202020202";
            var groupTwoInput = new GroupInput()
            {
                DisplayName = "Existing Test Group Two"
            };

            this.existingGroupTwo = this.groupsManager
                .PutGroupAsync(groupTwoId, groupTwoInput, SubjectType.User, "test@test.test")
                .GetAwaiter().GetResult();
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
        public async Task TryToReadPermissionsWithNoAccess()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [WHEN] A second user has made a request to get secret access list.
            var accessRequest = TestFactory.CreateHttpRequestData("get");
            var secondClaimsPrincipal = new ClaimsPrincipal(this.secondIdentity);
            var accessResponse = await this.secretAccess.Run(accessRequest, "sunshine", secondClaimsPrincipal, this.logger);

            // [THEN] UnauthorizedObjectResult is returned with Status = 'unauthorized', null Result and non-null Error.
            var forbiddenObjectResult = accessResponse as TestHttpResponseData;

            Assert.That(forbiddenObjectResult, Is.Not.Null);
            Assert.That(forbiddenObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

            var responseResult = forbiddenObjectResult?.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(responseResult?.Status, Is.EqualTo("forbidden"));
            Assert.That(responseResult?.Result, Is.Null);
            Assert.That(responseResult?.Error, Is.Not.Null);
        }

        [Test]
        public async Task GrantReadAccess()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [GIVEN] The user has granted read access for the secret to another user.
            await this.GrantAccess(this.firstIdentity, "sunshine", "second@test.test", true, false, false, false);

            // [WHEN] A second user has made a request to get secret access list.
            var accessRequest = TestFactory.CreateHttpRequestData("get");
            var secondClaimsPrincipal = new ClaimsPrincipal(this.secondIdentity);
            var accessResponse = await this.secretAccess.Run(accessRequest, "sunshine", secondClaimsPrincipal, this.logger);

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result with a list of permissions and null Error.
            var okObjectResult = accessResponse as TestHttpResponseData;

            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectResult?.ReadBodyAsJson<BaseResponseObject<List<SubjectPermissionsOutput>>>();
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            var permissions = responseResult?.Result;
            if (permissions == null)
            {
                throw new AssertionException("Returned permissions are null.");
            }

            Assert.That(permissions.Count, Is.EqualTo(2));
            var firstUserPermissions = permissions.First(p => p.ObjectName.Equals("sunshine") && p.SubjectName.Equals("first@test.test"));
            Assert.That(firstUserPermissions.CanRead, Is.True);
            Assert.That(firstUserPermissions.CanWrite, Is.True);
            Assert.That(firstUserPermissions.CanGrantAccess, Is.True);
            Assert.That(firstUserPermissions.CanRevokeAccess, Is.True);

            var secondUserPermissions = permissions.First(p => p.ObjectName.Equals("sunshine") && p.SubjectName.Equals("second@test.test"));
            Assert.That(secondUserPermissions.CanRead, Is.True);
            Assert.That(secondUserPermissions.CanWrite, Is.False);
            Assert.That(secondUserPermissions.CanGrantAccess, Is.False);
            Assert.That(secondUserPermissions.CanRevokeAccess, Is.False);
        }

        [Test]
        public async Task GrantAllAccessExceptRevoke()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [GIVEN] The user has granted full access except 'revoke permissions' for the secret to another user.
            await this.GrantAccess(this.firstIdentity, "sunshine", "second@test.test", true, true, true, false);

            // [WHEN] A second user has made a request to get secret access list.
            var accessRequest = TestFactory.CreateHttpRequestData("get");
            var secondClaimsPrincipal = new ClaimsPrincipal(this.secondIdentity);
            var accessResponse = await this.secretAccess.Run(accessRequest, "sunshine", secondClaimsPrincipal, this.logger);

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result with a list of permissions and null Error.
            var okObjectResult = accessResponse as TestHttpResponseData;

            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectResult?.ReadBodyAsJson<BaseResponseObject<List<SubjectPermissionsOutput>>>();
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            var permissions = responseResult?.Result;
            if (permissions == null)
            {
                throw new AssertionException("Returned permissions are null.");
            }

            Assert.That(permissions.Count, Is.EqualTo(2));
            var firstUserPermissions = permissions.First(p => p.ObjectName.Equals("sunshine") && p.SubjectName.Equals("first@test.test"));
            Assert.That(firstUserPermissions.CanRead, Is.True);
            Assert.That(firstUserPermissions.CanWrite, Is.True);
            Assert.That(firstUserPermissions.CanGrantAccess, Is.True);
            Assert.That(firstUserPermissions.CanRevokeAccess, Is.True);

            var secondUserPermissions = permissions.First(p => p.ObjectName.Equals("sunshine") && p.SubjectName.Equals("second@test.test"));
            Assert.That(secondUserPermissions.CanRead, Is.True);
            Assert.That(secondUserPermissions.CanWrite, Is.True);
            Assert.That(secondUserPermissions.CanGrantAccess, Is.True);
            Assert.That(secondUserPermissions.CanRevokeAccess, Is.False);
        }

        [Test]
        public async Task CannotGrantRevokeAccessWithoutHavingIt()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [GIVEN] The user has granted full access except 'RevokeAccess' permission for the secret to a second user.
            await this.GrantAccess(this.firstIdentity, "sunshine", "second@test.test", true, true, true, false);

            // [WHEN] The second user is granting full access to a third user.
            var accessRequest = TestFactory.CreateHttpRequestData("post");
            var accessInput = new List<SubjectPermissionsInput>()
            {
                new SubjectPermissionsInput()
                {
                    SubjectName = "third@test.test",
                    CanRead = true,
                    CanWrite = true,
                    CanGrantAccess = true,
                    CanRevokeAccess = true
                }
            };

            accessRequest.SetBodyAsJson(accessInput);

            var claimsPrincipal = new ClaimsPrincipal(this.secondIdentity);
            var accessResponse = await this.secretAccess.Run(accessRequest, "sunshine", claimsPrincipal, this.logger);

            var okObjectAccessResult = accessResponse as TestHttpResponseData;

            Assert.That(okObjectAccessResult, Is.Not.Null);
            Assert.That(okObjectAccessResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // [THEN] OkObjectResult is returned with Status = 'ok', but third user has only 'Read, Write, GrantAccess' permissions.
            var okObjectResult = accessResponse as TestHttpResponseData;

            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectResult?.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);
            Assert.That(responseResult?.Result, Is.EqualTo("ok"));

            var dbPermissions = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("sunshine") && p.SubjectId.Equals("third@test.test"))
                .ToListAsync();

            var thirdUserPermissions = dbPermissions.First();

            Assert.That(thirdUserPermissions.CanRead, Is.True);
            Assert.That(thirdUserPermissions.CanWrite, Is.True);
            Assert.That(thirdUserPermissions.CanGrantAccess, Is.True);
            Assert.That(thirdUserPermissions.CanRevokeAccess, Is.False);
        }

        [Test]
        public async Task CanGrantFullAccess()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [GIVEN] The user has granted full access for the secret to a second user.
            await this.GrantAccess(this.firstIdentity, "sunshine", "second@test.test", true, true, true, true);

            // [WHEN] The second user is granting full access to a third user.
            var accessRequest = TestFactory.CreateHttpRequestData("post");
            var accessInput = new List<SubjectPermissionsInput>()
            {
                new SubjectPermissionsInput()
                {
                    SubjectName = "third@test.test",
                    CanRead = true,
                    CanWrite = true,
                    CanGrantAccess = true,
                    CanRevokeAccess = true
                }
            };

            accessRequest.SetBodyAsJson(accessInput);

            var claimsPrincipal = new ClaimsPrincipal(this.secondIdentity);
            var accessResponse = await this.secretAccess.Run(accessRequest, "sunshine", claimsPrincipal, this.logger);

            var okObjectAccessResult = accessResponse as TestHttpResponseData;

            Assert.That(okObjectAccessResult, Is.Not.Null);
            Assert.That(okObjectAccessResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // [THEN] OkObjectResult is returned with Status = 'ok', third user has full permissions.
            var okObjectResult = accessResponse as TestHttpResponseData;

            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectResult?.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);
            Assert.That(responseResult?.Result, Is.EqualTo("ok"));

            var dbPermissions = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("sunshine") && p.SubjectId.Equals("third@test.test"))
                .ToListAsync();

            var thirdUserPermissions = dbPermissions.First();

            Assert.That(thirdUserPermissions.CanRead, Is.True);
            Assert.That(thirdUserPermissions.CanWrite, Is.True);
            Assert.That(thirdUserPermissions.CanGrantAccess, Is.True);
            Assert.That(thirdUserPermissions.CanRevokeAccess, Is.True);
        }

        [Test]
        public async Task GrantAccessToExistingGroup_OldApi()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [WHEN] The user has granted full access for the secret to an existing group.
            await this.GrantGroupAccess(
                this.firstIdentity, "sunshine",
                null, this.existingGroupOne.GroupMail!,
                true, true, true, true);

            // [THEN] Permissions are added to the database.
            var dbPermissions = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("sunshine"))
                .ToListAsync();

            var allSecretPermissions = dbPermissions.ToList();
            Assert.That (allSecretPermissions.Count, Is.EqualTo(2));

            dbPermissions = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("sunshine") && p.SubjectId.Equals(this.existingGroupOne.GroupId))
                .ToListAsync();

            var groupPermissions = dbPermissions.Single();

            Assert.That(groupPermissions.SubjectType, Is.EqualTo(SubjectType.Group));
            Assert.That(groupPermissions.SubjectId, Is.EqualTo(this.existingGroupOne.GroupId));
            Assert.That(groupPermissions.SubjectName, Is.EqualTo(this.existingGroupOne.DisplayName));

            Assert.That(groupPermissions.CanRead, Is.True);
            Assert.That(groupPermissions.CanWrite, Is.True);
            Assert.That(groupPermissions.CanGrantAccess, Is.True);
            Assert.That(groupPermissions.CanRevokeAccess, Is.True);
        }

        [Test]
        public async Task GrantAccessToInexistentGroup_OldApi()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [WHEN] The user has granted full access for the secret to an inexistent group.
            var accessRequest = TestFactory.CreateHttpRequestData("post");
            var accessInput = new List<SubjectPermissionsInput>()
            {
                new SubjectPermissionsInput()
                {
                    SubjectType = SubjectTypeInput.Group,
                    SubjectName = "some.inexistent.group@test.test",
                    CanRead = true,
                    CanWrite = true,
                    CanGrantAccess = true,
                    CanRevokeAccess = true
                }
            };

            accessRequest.SetBodyAsJson(accessInput);

            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var accessResponse = await this.secretAccess.Run(accessRequest, "sunshine", claimsPrincipal, this.logger);

            // [THEN] OK is returned with Status = 'ok', but permissions for inexistent group are not added.
            var okAccessResult = accessResponse as TestHttpResponseData;

            Assert.That(okAccessResult, Is.Not.Null);
            Assert.That(okAccessResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okAccessResult?.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);
            Assert.That(responseResult?.Result, Is.EqualTo("ok"));

            // [THEN] The secret only has permissions for the user which created it.
            var dbPermissions = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("sunshine"))
                .ToListAsync();

            var existingPermissions = dbPermissions.Single();

            Assert.That(existingPermissions.SubjectType, Is.EqualTo(SubjectType.User));
            Assert.That(existingPermissions.SubjectName, Is.EqualTo("first@test.test"));
            Assert.That(existingPermissions.SubjectId, Is.EqualTo("first@test.test"));
        }

        [Test]
        public async Task GrantAccessToExistingGroup_OldApi_Read()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [GIVEN] The user has granted full access for the secret to an existing group.
            await this.GrantGroupAccess(
                this.firstIdentity, "sunshine",
                null, this.existingGroupOne.GroupMail!,
                true, true, true, true);

            // [WHEN] The user is requesting list of permissions to a secret.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var accessRequest = TestFactory.CreateHttpRequestData("get");
            var accessResponse = await this.secretAccess.Run(accessRequest, "sunshine", claimsPrincipal, this.logger);
            var okObjectAccessResult = accessResponse as TestHttpResponseData;

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error.
            Assert.That(okObjectAccessResult, Is.Not.Null);
            Assert.That(okObjectAccessResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectAccessResult?.ReadBodyAsJson<BaseResponseObject<List<SubjectPermissionsOutput>>>();
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            var permissions = responseResult?.Result;
            if (permissions == null)
            {
                throw new AssertionException("Permissions list is null.");
            }

            Assert.That(permissions.Count, Is.EqualTo(2));

            // [THEN] Rewturned permissions contain the group permissions.
            var permission = permissions.Where(p => p.SubjectId == this.existingGroupOne.GroupId).Single();
            Assert.That(permission.SubjectType, Is.EqualTo(SubjectTypeOutput.Group));
            Assert.That(permission.SubjectName, Is.EqualTo(this.existingGroupOne.DisplayName));

            Assert.That(permission.CanRead, Is.True);
            Assert.That(permission.CanWrite, Is.True);
            Assert.That(permission.CanGrantAccess, Is.True);
            Assert.That(permission.CanRevokeAccess, Is.True);
        }

        [Test]
        public async Task GrantAccessToExistingGroup_NewApi()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [WHEN] The user has granted full access for the secret to an existing group.
            await this.GrantGroupAccess(
                this.firstIdentity, "sunshine",
                this.existingGroupTwo.GroupId, this.existingGroupTwo.DisplayName,
                true, true, true, true);

            // [THEN] Permissions are added to the database.
            var dbPermissions = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("sunshine"))
                .ToListAsync();

            var allSecretPermissions = dbPermissions.ToList();
            Assert.That(allSecretPermissions.Count, Is.EqualTo(2));

            dbPermissions = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("sunshine") && p.SubjectId.Equals(this.existingGroupTwo.GroupId))
                .ToListAsync();

            var groupPermissions = dbPermissions.Single();

            Assert.That(groupPermissions.SubjectType, Is.EqualTo(SubjectType.Group));
            Assert.That(groupPermissions.SubjectId, Is.EqualTo(this.existingGroupTwo.GroupId));
            Assert.That(groupPermissions.SubjectName, Is.EqualTo(this.existingGroupTwo.DisplayName));

            Assert.That(groupPermissions.CanRead, Is.True);
            Assert.That(groupPermissions.CanWrite, Is.True);
            Assert.That(groupPermissions.CanGrantAccess, Is.True);
            Assert.That(groupPermissions.CanRevokeAccess, Is.True);
        }

        [Test]
        public async Task GrantAccessToInexistentGroup_NewApi()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [WHEN] The user has granted full access for the secret to inexistent group.
            var groupId = "00000000-abcd-0000-0000-000300030003";
            var groupDisplayName = "Newly Created Group";
            await this.GrantGroupAccess(
                this.firstIdentity, "sunshine",
                groupId, groupDisplayName,
                true, true, true, true);

            // [THEN] Permissions are added to the database, new group created.
            var dbPermissions = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("sunshine"))
                .ToListAsync();

            var allSecretPermissions = dbPermissions.ToList();
            Assert.That(allSecretPermissions.Count, Is.EqualTo(2));

            dbPermissions = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("sunshine") && p.SubjectId.Equals(groupId))
                .ToListAsync();

            var groupPermissions = dbPermissions.Single();

            Assert.That(groupPermissions.SubjectType, Is.EqualTo(SubjectType.Group));
            Assert.That(groupPermissions.SubjectId, Is.EqualTo(groupId));
            Assert.That(groupPermissions.SubjectName, Is.EqualTo(groupDisplayName));

            Assert.That(groupPermissions.CanRead, Is.True);
            Assert.That(groupPermissions.CanWrite, Is.True);
            Assert.That(groupPermissions.CanGrantAccess, Is.True);
            Assert.That(groupPermissions.CanRevokeAccess, Is.True);

            var groupList = await this.dbContext.GroupDictionary
                .Where(p => p.GroupId.Equals(groupId))
                .ToListAsync();

            Assert.That(groupList.Count, Is.EqualTo(1));
            var group = groupList.Single();

            Assert.That(group.GroupId, Is.EqualTo(groupId));
            Assert.That(group.DisplayName, Is.EqualTo(groupDisplayName));
            Assert.That(group.GroupMail, Is.EqualTo(string.Empty));
        }

        [Test]
        public async Task GrantAndRevokeAccess()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [GIVEN] The user has granted read/write access for the secret to a second user.
            await this.GrantAccess(this.firstIdentity, "sunshine", "second@test.test", true, true, false, false);

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromMinutes(1);

            // [GIVEN] The second user can read the secret metadata.
            var getRequest = TestFactory.CreateHttpRequestData("get");
            var claimsPrincipal = new ClaimsPrincipal(this.secondIdentity);
            var getResponse = await this.secretMeta.Run(getRequest, "sunshine", claimsPrincipal, this.logger);

            var okObjectAccessResult = getResponse as TestHttpResponseData;

            Assert.That(okObjectAccessResult, Is.Not.Null);
            Assert.That(okObjectAccessResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var getResponseResult = okObjectAccessResult?.ReadBodyAsJson<BaseResponseObject<ObjectMetadataOutput>>();
            var mainContent = getResponseResult?.Result?.Content.FirstOrDefault();
            if (mainContent == null)
            {
                throw new AssertionException("Main content is null.");
            }

            Assert.That(mainContent.IsMain, Is.True);
            Assert.That(string.IsNullOrEmpty(mainContent.ContentName), Is.False);

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromMinutes(1);

            // [WHEN] The user has revoked all access for the secret from the second user.
            await this.RevokeAccess(this.firstIdentity, "sunshine", "second@test.test", true, true, true, true);

            // [THEN] The database does not contain any records for second user to access the secret.
            var dbPermissions = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("sunshine") && p.SubjectId.Equals("second@test.test"))
                .ToListAsync();

            Assert.That(dbPermissions.Any(), Is.False);
        }

        [Test]
        public async Task GrantAndRevokeAccessPartially()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [GIVEN] The user has granted read/write access for the secret to a second user.
            await this.GrantAccess(this.firstIdentity, "sunshine", "second@test.test", true, true, false, false);

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromMinutes(1);

            // [GIVEN] The second user can read the secret metadata.
            var getRequest = TestFactory.CreateHttpRequestData("get");
            var claimsPrincipal = new ClaimsPrincipal(this.secondIdentity);
            var getResponse = await this.secretMeta.Run(getRequest, "sunshine", claimsPrincipal, this.logger);

            var okObjectAccessResult = getResponse as TestHttpResponseData;

            Assert.That(okObjectAccessResult, Is.Not.Null);
            Assert.That(okObjectAccessResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var getResponseResult = okObjectAccessResult?.ReadBodyAsJson<BaseResponseObject<ObjectMetadataOutput>>();
            var mainContent = getResponseResult?.Result?.Content.FirstOrDefault();
            if (mainContent == null)
            {
                throw new AssertionException("Main content is null.");
            }

            Assert.That(mainContent.IsMain, Is.True);
            Assert.That(string.IsNullOrEmpty(mainContent.ContentName), Is.False);

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromMinutes(1);

            // [WHEN] The user has revoked write access for the secret from the second user.
            await this.RevokeAccess(this.firstIdentity, "sunshine", "second@test.test", false, true, false, false);

            // [THEN] The database contains 1 record for second user to access the secret, only for read access.
            var dbPermissions = await this.dbContext.Permissions
                .Where(p => p.SecretName.Equals("sunshine") && p.SubjectId.Equals("second@test.test"))
                .ToListAsync();

            Assert.That(dbPermissions?.Count, Is.EqualTo(1));

            Assert.That(dbPermissions?.First().CanRead, Is.True);
            Assert.That(dbPermissions?.First().CanWrite, Is.False);
            Assert.That(dbPermissions?.First().CanGrantAccess, Is.False);
            Assert.That(dbPermissions?.First().CanRevokeAccess, Is.False);
        }

        [Test]
        public async Task PermissionsCreatedCorrectlyOnSecretCreation()
        {
            // [GIVEN] A user with valid credentials.
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

            // [WHEN] A request is made to create a secret.
            var response = await this.secretMeta.Run(request, "sunshine", claimsPrincipal, this.logger);
            var okObjectResult = response as TestHttpResponseData;

            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // [THEN] Database contains persisted permissions only for the initial user to the created secret.
            var permissions = await this.dbContext.Permissions.Where(p => p.SecretName.Equals("sunshine")).ToListAsync();

            Assert.That(permissions.Count, Is.EqualTo(1));

            Assert.That(permissions.First().SubjectName, Is.EqualTo("first@test.test"));
            Assert.That(permissions.First().SubjectId, Is.EqualTo("first@test.test"));
            Assert.That(permissions.First().SecretName, Is.EqualTo("sunshine"));

            Assert.That(permissions.First().CanRead, Is.True);
            Assert.That(permissions.First().CanWrite, Is.True);
            Assert.That(permissions.First().CanGrantAccess, Is.True);
            Assert.That(permissions.First().CanRevokeAccess, Is.True);
        }

        [Test]
        public async Task CreateSecretSunshine()
        {
            // [GIVEN] A user with valid credentials created a secret.
            await this.CreateSecret(this.firstIdentity, "sunshine");

            // [WHEN] A request is made to get secret access list.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var accessRequest = TestFactory.CreateHttpRequestData("get");
            var accessResponse = await this.secretAccess.Run(accessRequest, "sunshine", claimsPrincipal, this.logger);
            var okObjectAccessResult = accessResponse as TestHttpResponseData;

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error.
            Assert.That(okObjectAccessResult, Is.Not.Null);
            Assert.That(okObjectAccessResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectAccessResult?.ReadBodyAsJson<BaseResponseObject<List<SubjectPermissionsOutput>>>();
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);

            var permissions = responseResult?.Result;
            if (permissions == null)
            {
                throw new AssertionException("Permissions list is null.");
            }

            Assert.That(permissions.Count, Is.EqualTo(1));

            Assert.That(permissions.First().SubjectName, Is.EqualTo("first@test.test"));
            Assert.That(permissions.First().ObjectName, Is.EqualTo("sunshine"));

            Assert.That(permissions.First().CanRead, Is.True);
            Assert.That(permissions.First().CanWrite, Is.True);
            Assert.That(permissions.First().CanGrantAccess, Is.True);
            Assert.That(permissions.First().CanRevokeAccess, Is.True);
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
            var okObjectResult = response as TestHttpResponseData;

            Assert.That(okObjectResult, Is.Not.Null);
            Assert.That(okObjectResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        private async Task GrantGroupAccess(CaseSensitiveClaimsIdentity identity, string secretName, string? subjectId, string subjectName, bool read, bool write, bool grantAccess, bool revokeAccess)
            => await this.InternalAccessRequest(identity, "post", secretName, SubjectTypeInput.Group, subjectId, subjectName, read, write, grantAccess, revokeAccess);

        private async Task GrantAccess(CaseSensitiveClaimsIdentity identity, string secretName, string subjectName, bool read, bool write, bool grantAccess, bool revokeAccess)
            => await this.InternalAccessRequest(identity, "post", secretName, subjectName, read, write, grantAccess, revokeAccess);

        private async Task RevokeAccess(CaseSensitiveClaimsIdentity identity, string secretName, string subjectName, bool read, bool write, bool grantAccess, bool revokeAccess)
            => await this.InternalAccessRequest(identity, "delete", secretName, subjectName, read, write, grantAccess, revokeAccess);

        private async Task InternalAccessRequest(CaseSensitiveClaimsIdentity identity, string method, string secretName, string subjectName, bool read, bool write, bool grantAccess, bool revokeAccess)
            => await InternalAccessRequest(identity, method, secretName, SubjectTypeInput.User, null, subjectName, read, write, grantAccess, revokeAccess);

        private async Task InternalAccessRequest(CaseSensitiveClaimsIdentity identity, string method, string secretName, SubjectTypeInput subjectType, string? subjectId, string subjectName, bool read, bool write, bool grantAccess, bool revokeAccess)
        {
            var accessRequest = TestFactory.CreateHttpRequestData(method);
            var accessInput = new List<SubjectPermissionsInput>()
            {
                new SubjectPermissionsInput()
                {
                    SubjectType = subjectType,
                    SubjectId = subjectId!,
                    SubjectName = subjectName,
                    CanRead = read,
                    CanWrite = write,
                    CanGrantAccess = grantAccess,
                    CanRevokeAccess = revokeAccess,
                }
            };

            accessRequest.SetBodyAsJson(accessInput);

            var claimsPrincipal = new ClaimsPrincipal(identity);
            var accessResponse = await this.secretAccess.Run(accessRequest, secretName, claimsPrincipal, this.logger);

            var okObjectAccessResult = accessResponse as TestHttpResponseData;

            Assert.That(okObjectAccessResult, Is.Not.Null);
            Assert.That(okObjectAccessResult?.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var responseResult = okObjectAccessResult?.ReadBodyAsJson<BaseResponseObject<string>>();
            Assert.That(responseResult?.Status, Is.EqualTo("ok"));
            Assert.That(responseResult?.Error, Is.Null);
            Assert.That(responseResult?.Result, Is.EqualTo("ok"));
        }
    }
}
