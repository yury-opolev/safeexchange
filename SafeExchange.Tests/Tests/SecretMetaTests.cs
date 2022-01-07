/// <summary>
/// SecretMetaTests
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
    using System.Linq;
    using System.Net.Http;
    using System.Security.Claims;
    using System.Text.Json;
    using System.Threading.Tasks;

    [TestFixture]
    public class SecretMetaTests
    {
        private ILogger logger;

        private SafeExchangeSecretMeta secretMeta;

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

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            var builder = new ConfigurationBuilder().AddUserSecrets<SecretMetaTests>();
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
                .UseCosmos(secretConfiguration.GetConnectionString("CosmosDb"), databaseName: $"{nameof(SecretMetaTests)}Database")
                .Options;

            this.dbContext = new SafeExchangeDbContext(dbContextOptions);
            this.dbContext.Database.EnsureCreated();

            this.tokenHelper = new TestTokenHelper();
            this.graphDataProvider = new TestGraphDataProvider();
            this.globalFilters = new GlobalFilters(this.testConfiguration, this.tokenHelper, this.graphDataProvider, TestFactory.CreateLogger<GlobalFilters>());

            this.blobHelper = new TestBlobHelper();
            this.purger = new PurgeManager(this.testConfiguration, this.blobHelper, TestFactory.CreateLogger<PurgeManager>());

            this.permissionsManager = new PermissionsManager(this.testConfiguration, this.dbContext, TestFactory.CreateLogger<PermissionsManager>());

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
        }

        [TearDown]
        public void Cleanup()
        {
            this.graphDataProvider.GroupMemberships.Clear();

            this.dbContext.Users.RemoveRange(this.dbContext.Users.ToList());
            this.dbContext.Objects.RemoveRange(this.dbContext.Objects.ToList());
            this.dbContext.Permissions.RemoveRange(this.dbContext.Permissions.ToList());
            this.dbContext.AccessRequests.RemoveRange(this.dbContext.AccessRequests.ToList());
            this.dbContext.NotificationSubscriptions.RemoveRange(this.dbContext.NotificationSubscriptions.ToList());
            this.dbContext.GroupDictionary.RemoveRange(this.dbContext.GroupDictionary.ToList());
            this.dbContext.SaveChanges();
        }

        [Test]
        public async Task GetSecret_NotFound()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request to get secret metadata is made for nonexistent secret.
            var request = TestFactory.CreateHttpRequest("get");
            var response = await this.secretMeta.Run(request, "dummy", claimsPrincipal, this.logger);
            var notFoundObjectResult = response as NotFoundObjectResult;

            // [THEN] NotFoundObjectResult is returned with Status = 'not_found', Error message and null Result
            Assert.IsNotNull(notFoundObjectResult);
            Assert.AreEqual(404, notFoundObjectResult?.StatusCode);

            var responseResult = notFoundObjectResult?.Value as BaseResponseObject<object>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("not_found", responseResult?.Status);
            Assert.IsNotNull(responseResult?.Error);
            Assert.IsNull(responseResult?.Result);
        }

        [Test]
        public async Task UpdateSecret_NotFound()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request to get secret metadata is made for nonexistent secret.
            var request = TestFactory.CreateHttpRequest("patch");
            var response = await this.secretMeta.Run(request, "dummy", claimsPrincipal, this.logger);
            var notFoundObjectResult = response as NotFoundObjectResult;

            // [THEN] NotFoundObjectResult is returned with Status = 'not_found', Error message and null Result
            Assert.IsNotNull(notFoundObjectResult);
            Assert.AreEqual(404, notFoundObjectResult?.StatusCode);

            var responseResult = notFoundObjectResult?.Value as BaseResponseObject<object>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("not_found", responseResult?.Status);
            Assert.IsNotNull(responseResult?.Error);
            Assert.IsNull(responseResult?.Result);
        }

        [Test]
        public async Task DeleteSecret_NotFound()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request to get secret metadata is made for nonexistent secret.
            var request = TestFactory.CreateHttpRequest("delete");
            var response = await this.secretMeta.Run(request, "dummy", claimsPrincipal, this.logger);
            var notFoundObjectResult = response as NotFoundObjectResult;

            // [THEN] NotFoundObjectResult is returned with Status = 'not_found', Error message and null Result
            Assert.IsNotNull(notFoundObjectResult);
            Assert.AreEqual(404, notFoundObjectResult?.StatusCode);

            var responseResult = notFoundObjectResult?.Value as BaseResponseObject<object>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("not_found", responseResult?.Status);
            Assert.IsNotNull(responseResult?.Error);
            Assert.IsNull(responseResult?.Result);
        }

        [Test]
        public async Task CreateSecretWithNoIdProvided()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request to create secret is made, without secret id specified.
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

            var response = await this.secretMeta.Run(request, string.Empty, claimsPrincipal, this.logger);
            var badRequestObjectResult = response as BadRequestObjectResult;

            // [THEN] BadRequestObjectResult is returned with Status = 'error', null Result and non-null Error.
            Assert.IsNotNull(badRequestObjectResult);
            Assert.AreEqual(400, badRequestObjectResult?.StatusCode);

            var responseResult = badRequestObjectResult?.Value as BaseResponseObject<object>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("error", responseResult?.Status);
            Assert.IsNotNull(responseResult?.Error);
            Assert.IsNull(responseResult?.Result);
        }

        [Test]
        public async Task CreateSecretWithNoDataProvided()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request to create secret is made, without body specified.
            var request = TestFactory.CreateHttpRequest("post");
            var response = await this.secretMeta.Run(request, "badrequest", claimsPrincipal, this.logger);
            var badRequestObjectResult = response as BadRequestObjectResult;

            // [THEN] BadRequestObjectResult is returned with Status = 'error', null Result and non-null Error.
            Assert.IsNotNull(badRequestObjectResult);
            Assert.AreEqual(400, badRequestObjectResult?.StatusCode);

            var responseResult = badRequestObjectResult?.Value as BaseResponseObject<object>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("error", responseResult?.Status);
            Assert.IsNotNull(responseResult?.Error);
            Assert.IsNull(responseResult?.Result);
        }

        [Test]
        public async Task UpdateSecretWithNoDataProvided()
        {
            // [GIVEN] A secret with name 'x' exists.
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
            var response = await this.secretMeta.Run(request, "badrequest2", claimsPrincipal, this.logger);
            var okObjectResult = response as OkObjectResult;
            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            // [WHEN] A request to update secret 'x' is made, without body specified.
            request = TestFactory.CreateHttpRequest("patch");
            response = await this.secretMeta.Run(request, "badrequest2", claimsPrincipal, this.logger);
            var badRequestObjectResult = response as BadRequestObjectResult;

            // [THEN] BadRequestObjectResult is returned with Status = 'error', null Result and non-null Error.
            Assert.IsNotNull(badRequestObjectResult);
            Assert.AreEqual(400, badRequestObjectResult?.StatusCode);

            var responseResult = badRequestObjectResult?.Value as BaseResponseObject<object>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("error", responseResult?.Status);
            Assert.IsNotNull(responseResult?.Error);
            Assert.IsNull(responseResult?.Result);
        }

        [Test]
        public async Task CreateSecretWithNoExpirationSettingsProvided()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request to create secret is made, without body specified.
            var request = TestFactory.CreateHttpRequest("post");
            var creationInputString = DefaultJsonSerializer.Serialize(new MetadataCreationInput());
            request.Body = new StringContent(creationInputString).ReadAsStream();

            var response = await this.secretMeta.Run(request, "badrequest", claimsPrincipal, this.logger);
            var badRequestObjectResult = response as BadRequestObjectResult;

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error.
            Assert.IsNotNull(badRequestObjectResult);
            Assert.AreEqual(400, badRequestObjectResult?.StatusCode);

            var responseResult = badRequestObjectResult?.Value as BaseResponseObject<object>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("error", responseResult?.Status);
            Assert.IsNotNull(responseResult?.Error);
            Assert.IsNull(responseResult?.Result);
        }

        [Test]
        public async Task UpdateSecretWithNoExpirationSettingsProvided()
        {
            // [GIVEN] A secret with name 'x' exists.
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
            var response = await this.secretMeta.Run(request, "badrequest3", claimsPrincipal, this.logger);
            var okObjectResult = response as OkObjectResult;
            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            // [WHEN] A request to update secret 'x' is made, without expiration settings specified.
            request = TestFactory.CreateHttpRequest("patch");
            var patchInput = new MetadataUpdateInput();
            request.Body = new StringContent(DefaultJsonSerializer.Serialize(patchInput)).ReadAsStream();
            response = await this.secretMeta.Run(request, "badrequest3", claimsPrincipal, this.logger);
            var badRequestObjectResult = response as BadRequestObjectResult;

            // [THEN] BadRequestObjectResult is returned with Status = 'error', null Result and non-null Error.
            Assert.IsNotNull(badRequestObjectResult);
            Assert.AreEqual(400, badRequestObjectResult?.StatusCode);

            var responseResult = badRequestObjectResult?.Value as BaseResponseObject<object>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("error", responseResult?.Status);
            Assert.IsNotNull(responseResult?.Error);
            Assert.IsNull(responseResult?.Result);
        }

        [Test]
        public async Task CreateSecretSunshine()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            // [WHEN] A request is made to create secret.
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

            var response = await this.secretMeta.Run(request, "sunshine", claimsPrincipal, this.logger);
            var okObjectResult = response as OkObjectResult;

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error.
            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            var responseResult = okObjectResult?.Value as BaseResponseObject<ObjectMetadataOutput>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);

            var metadata = responseResult?.Result;
            Assert.IsNotNull(metadata);
            Assert.AreEqual("sunshine", metadata?.ObjectName);

            Assert.AreEqual(creationInput.ExpirationSettings.ScheduleExpiration, metadata?.ExpirationSettings?.ScheduleExpiration);
            Assert.AreEqual(creationInput.ExpirationSettings.ExpireAt, metadata?.ExpirationSettings?.ExpireAt);
            Assert.AreEqual(creationInput.ExpirationSettings.ExpireOnIdleTime, metadata?.ExpirationSettings?.ExpireOnIdleTime);
            Assert.AreEqual(creationInput.ExpirationSettings.IdleTimeToExpire, metadata?.ExpirationSettings?.IdleTimeToExpire);

            var contentList = metadata?.Content;
            Assert.IsNotNull(contentList);
            Assert.AreEqual(1, contentList?.Count);

            var mainContent = contentList?[0];
            Assert.AreEqual(string.Empty, mainContent?.ContentType);
            Assert.AreEqual(string.Empty, mainContent?.FileName);
            Assert.IsFalse(string.IsNullOrWhiteSpace(mainContent?.ContentName));
            Assert.IsTrue(mainContent?.IsMain);
            Assert.IsFalse(mainContent?.IsReady);

            var createdSecret = await this.dbContext.Objects.FindAsync("sunshine");
            Assert.AreEqual("first@test.test", createdSecret?.CreatedBy);
            Assert.AreEqual(DateTimeProvider.UtcNow, createdSecret?.CreatedAt);
            Assert.AreEqual(string.Empty, createdSecret?.ModifiedBy);
            Assert.AreEqual(DateTime.MinValue, createdSecret?.ModifiedAt);
        }

        [Test]
        public async Task CreateAndListSecretsSunshine()
        {
            // [GIVEN] A user has several secrets with 'Read' access.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
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

            var secretCount = 10;
            for (int i = 0; i < secretCount; i++)
            {
                var request = TestFactory.CreateHttpRequest("post");
                request.Body = new StringContent(DefaultJsonSerializer.Serialize(creationInput)).ReadAsStream();
                var response = await this.secretMeta.Run(request, $"somesecret{i}", claimsPrincipal, this.logger);
                var okObjectResult = response as OkObjectResult;

                Assert.IsNotNull(okObjectResult);
                Assert.AreEqual(200, okObjectResult?.StatusCode);
            }

            var secrets = await this.dbContext.Objects.Where(o => o.ObjectName.StartsWith("somesecret")).ToListAsync();
            Assert.AreEqual(secretCount, secrets.Count);

            var getRequest = TestFactory.CreateHttpRequest("get");
            var getResponse = await this.secretMeta.RunList(getRequest, claimsPrincipal, this.logger);
            var okObjectListResult = getResponse as OkObjectResult;

            Assert.IsNotNull(okObjectListResult);
            Assert.AreEqual(200, okObjectListResult?.StatusCode);

            var listResponseResult = okObjectListResult?.Value as BaseResponseObject<List<SubjectPermissionsOutput>>;
            Assert.IsNotNull(listResponseResult);
            Assert.AreEqual("ok", listResponseResult?.Status);
            Assert.IsNull(listResponseResult?.Error);

            var list = listResponseResult?.Result;
            Assert.IsNotNull(list);
            Assert.AreEqual(secretCount, list?.Count);
        }

        [Test]
        public async Task CreateSecretTwice()
        {
            // [GIVEN] A user with valid credentials.
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

            var response = await this.secretMeta.Run(request, "twice", claimsPrincipal, this.logger);
            var okObjectResult = response as OkObjectResult;

            // [GIVEN] A secret 'x' exists.
            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            // [WHEN] A request is made to create secret with then same name 'x'.
            request = TestFactory.CreateHttpRequest("post");
            request.Body = new StringContent(DefaultJsonSerializer.Serialize(creationInput)).ReadAsStream();
            response = await this.secretMeta.Run(request, "twice", claimsPrincipal, this.logger);
            var conflictObjectResult = response as ConflictObjectResult;

            // [THEN] ConflictObjectResult is returned with Status = 'conflict', null Result and non-null Error.
            Assert.IsNotNull(conflictObjectResult);
            Assert.AreEqual(409, conflictObjectResult?.StatusCode);

            var responseResult = conflictObjectResult?.Value as BaseResponseObject<object>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("conflict", responseResult?.Status);
            Assert.IsNotNull(responseResult?.Error);
            Assert.IsNull(responseResult?.Result);
        }

        [Test]
        public async Task CreateAndGetSecretSunshine()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            var postRequest = TestFactory.CreateHttpRequest("post");
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

            postRequest.Body = new StringContent(DefaultJsonSerializer.Serialize(creationInput)).ReadAsStream();

            var postResponse = await this.secretMeta.Run(postRequest, "sunshine2", claimsPrincipal, this.logger);
            var okObjectResult = postResponse as OkObjectResult;

            // [GIVEN] Secret is created with name 'x'.
            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromHours(1);

            // [WHEN] The same user is making a request to get secret 'x'.
            var getRequest = TestFactory.CreateHttpRequest("get");
            var getResponse = await this.secretMeta.Run(getRequest, "sunshine2", claimsPrincipal, this.logger);

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error.
            okObjectResult = getResponse as OkObjectResult;
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            var responseResult = okObjectResult?.Value as BaseResponseObject<ObjectMetadataOutput>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);

            var metadata = responseResult?.Result;
            Assert.IsNotNull(metadata);
            Assert.AreEqual("sunshine2", metadata?.ObjectName);

            Assert.AreEqual(creationInput.ExpirationSettings.ScheduleExpiration, metadata?.ExpirationSettings?.ScheduleExpiration);
            Assert.AreEqual(creationInput.ExpirationSettings.ExpireAt, metadata?.ExpirationSettings?.ExpireAt);
            Assert.AreEqual(creationInput.ExpirationSettings.ExpireOnIdleTime, metadata?.ExpirationSettings?.ExpireOnIdleTime);
            Assert.AreEqual(creationInput.ExpirationSettings.IdleTimeToExpire, metadata?.ExpirationSettings?.IdleTimeToExpire);

            var contentList = metadata?.Content;
            Assert.IsNotNull(contentList);
            Assert.AreEqual(1, contentList?.Count);

            var mainContent = contentList?[0];
            Assert.AreEqual(string.Empty, mainContent?.ContentType);
            Assert.AreEqual(string.Empty, mainContent?.FileName);
            Assert.IsFalse(string.IsNullOrWhiteSpace(mainContent?.ContentName));
            Assert.IsTrue(mainContent?.IsMain);
            Assert.IsFalse(mainContent?.IsReady);
        }

        [Test]
        public async Task CreateAndUpdateSecretSunshine()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            var postRequest = TestFactory.CreateHttpRequest("post");
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

            postRequest.Body = new StringContent(DefaultJsonSerializer.Serialize(creationInput)).ReadAsStream();

            var postResponse = await this.secretMeta.Run(postRequest, "sunshine3", claimsPrincipal, this.logger);
            var okObjectResult = postResponse as OkObjectResult;

            // [GIVEN] Secret is created with name 'x'.
            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromHours(1);

            // [WHEN] The same user is making a request to get secret 'x'.
            var patchRequest = TestFactory.CreateHttpRequest("patch");
            var updateInput = new MetadataCreationInput()
            {
                ExpirationSettings = new ExpirationSettingsInput()
                {
                    ScheduleExpiration = true,
                    ExpireAt = DateTimeProvider.UtcNow + TimeSpan.FromDays(180),
                    ExpireOnIdleTime = false,
                    IdleTimeToExpire = TimeSpan.FromDays(180)
                }
            };

            patchRequest.Body = new StringContent(DefaultJsonSerializer.Serialize(updateInput)).ReadAsStream();
            var patchResponse = await this.secretMeta.Run(patchRequest, "sunshine3", claimsPrincipal, this.logger);

            // [THEN] OkObjectResult is returned with Status = 'ok', non-null Result and null Error.
            okObjectResult = patchResponse as OkObjectResult;
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            var responseResult = okObjectResult?.Value as BaseResponseObject<ObjectMetadataOutput>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);

            var metadata = responseResult?.Result;
            Assert.IsNotNull(metadata);
            Assert.AreEqual("sunshine3", metadata?.ObjectName);

            Assert.AreEqual(updateInput.ExpirationSettings.ScheduleExpiration, metadata?.ExpirationSettings?.ScheduleExpiration);
            Assert.AreEqual(updateInput.ExpirationSettings.ExpireAt, metadata?.ExpirationSettings?.ExpireAt);
            Assert.AreEqual(updateInput.ExpirationSettings.ExpireOnIdleTime, metadata?.ExpirationSettings?.ExpireOnIdleTime);
            Assert.AreEqual(updateInput.ExpirationSettings.IdleTimeToExpire, metadata?.ExpirationSettings?.IdleTimeToExpire);

            var contentList = metadata?.Content;
            Assert.IsNotNull(contentList);
            Assert.AreEqual(1, contentList?.Count);

            var mainContent = contentList?[0];
            Assert.AreEqual(string.Empty, mainContent?.ContentType);
            Assert.AreEqual(string.Empty, mainContent?.FileName);
            Assert.IsFalse(string.IsNullOrWhiteSpace(mainContent?.ContentName));
            Assert.IsTrue(mainContent?.IsMain);
            Assert.IsFalse(mainContent?.IsReady);
        }

        [Test]
        public async Task CreateAndDeleteSecretSunshine()
        {
            // [GIVEN] A user with valid credentials.
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);

            var postRequest = TestFactory.CreateHttpRequest("post");
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

            postRequest.Body = new StringContent(DefaultJsonSerializer.Serialize(creationInput)).ReadAsStream();

            var postResponse = await this.secretMeta.Run(postRequest, "sunshine4", claimsPrincipal, this.logger);
            var okObjectResult = postResponse as OkObjectResult;

            // [GIVEN] Secret is created with name 'x'.
            Assert.IsNotNull(okObjectResult);
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            DateTimeProvider.SpecifiedDateTime += TimeSpan.FromHours(1);

            // [WHEN] The same user is making a request to delete secret 'x'.
            var deleteRequest = TestFactory.CreateHttpRequest("delete");
            var deleteResponse = await this.secretMeta.Run(deleteRequest, "sunshine4", claimsPrincipal, this.logger);

            // [THEN] OkObjectResult is returned with Status = 'ok', Result = 'ok' and null Error.
            okObjectResult = deleteResponse as OkObjectResult;
            Assert.AreEqual(200, okObjectResult?.StatusCode);

            var responseResult = okObjectResult?.Value as BaseResponseObject<string>;
            Assert.IsNotNull(responseResult);
            Assert.AreEqual("ok", responseResult?.Status);
            Assert.IsNull(responseResult?.Error);
            Assert.AreEqual("ok", responseResult?.Result);

            var deletedSecret = await this.dbContext.Objects.FindAsync("sunshine4");
            Assert.IsNull(deletedSecret);
        }
    }
}