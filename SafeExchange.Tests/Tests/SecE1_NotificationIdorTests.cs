/// <summary>
/// SecE1_NotificationIdorTests — security triage for Cluster E1 (CWE-639 IDOR /
/// CWE-285), findings #13/#20/#17/#21 against
/// <see cref="SafeExchange.Core.Functions.SafeExchangeExternalNotificationDetails"/>.
///
/// The findings claim that GET /v1/notificationdetails/{webhookNotificationDataId}
/// looks up a notification by id alone, "without binding it to the caller's
/// subjectId", and is therefore an Insecure Direct Object Reference.
///
/// These tests provide the runtime validation that finding #17 explicitly asked
/// for ("runtime validation is still needed to confirm no unseen middleware or
/// data-layer partitioning restricts records to the caller"). They run against
/// the EF Core InMemory provider (no Cosmos emulator) using Moq for the injected
/// <see cref="IPurger"/> and a configurable fake <see cref="ITokenHelper"/>.
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Purger;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Azure.Core.Serialization;

    [TestFixture]
    public class SecE1_NotificationIdorTests
    {
        private ILogger logger;
        private SafeExchangeDbContext dbContext;
        private GlobalFilters globalFilters;
        private FakeTokenHelper tokenHelper;
        private Mock<IPurger> purgerMock;
        private IConfiguration configuration;
        private SafeExchangeExternalNotificationDetails handler;

        // Reader application B — a registered ExternalNotificationsReader. In the
        // findings' threat model this is the "attacker" application that reads a
        // notification it did not itself originate.
        private const string ReaderTenantId = "00000000-0000-0000-0000-0000000000b1";
        private const string ReaderClientId = "00000000-0000-0000-0000-0000000000b2";

        [SetUp]
        public void Setup()
        {
            this.logger = TestFactory.CreateLogger();

            var options = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseInMemoryDatabase($"SecE1-{Guid.NewGuid()}")
                .Options;
            this.dbContext = new SafeExchangeDbContext(options);

            this.tokenHelper = new FakeTokenHelper();

            // Empty globally-allowed-groups => GlobalAccessFilter passes everyone,
            // so the per-handler authorization is what we are actually exercising.
            var gagc = new GloballyAllowedGroupsConfiguration();
            var groupsConfiguration = Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(x => x.CurrentValue == gagc);
            var ac = new AdminConfiguration { AdminUsers = string.Empty };
            var adminConfiguration = Mock.Of<IOptionsMonitor<AdminConfiguration>>(x => x.CurrentValue == ac);

            this.globalFilters = new GlobalFilters(
                groupsConfiguration, adminConfiguration, this.tokenHelper, TestFactory.CreateLogger<GlobalFilters>());

            this.purgerMock = new Mock<IPurger>();
            this.purgerMock
                .Setup(p => p.PurgeNotificationDataIfNeededAsync(It.IsAny<string>(), It.IsAny<SafeExchangeDbContext>()))
                .ReturnsAsync(false);
            this.purgerMock
                .Setup(p => p.PurgeNotificationDataAsync(It.IsAny<string>(), It.IsAny<SafeExchangeDbContext>()))
                .Returns(Task.CompletedTask);

            this.configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GeneralConfiguration:WebClientBaseUri"] = "https://web.example/"
                })
                .Build();

            DateTimeProvider.SpecifiedDateTime = DateTime.UtcNow;
            DateTimeProvider.UseSpecifiedDateTime = true;

            // The Functions worker resolves an ObjectSerializer from the
            // FunctionContext service provider when writing JSON responses.
            var workerOptions = Options.Create(new WorkerOptions { Serializer = new JsonObjectSerializer() });
            var serviceProviderMock = new Mock<IServiceProvider>();
            serviceProviderMock
                .Setup(x => x.GetService(typeof(IOptions<WorkerOptions>)))
                .Returns(workerOptions);
            TestFactory.FunctionContext.InstanceServices = serviceProviderMock.Object;

            this.handler = new SafeExchangeExternalNotificationDetails(
                this.configuration, this.dbContext, this.purgerMock.Object, this.tokenHelper, this.globalFilters);
        }

        [TearDown]
        public void Cleanup()
        {
            DateTimeProvider.UseSpecifiedDateTime = false;
            this.dbContext.Dispose();
        }

        /// <summary>
        /// Seeds a registered ExternalNotificationsReader application and a complete
        /// notification graph (admin-created webhook subscription -> in-progress
        /// access request with a user recipient -> notification data) and returns
        /// the notification data id.
        /// </summary>
        private async Task<string> SeedNotificationGraphAsync(string recipientUpn)
        {
            var readerApp = new Application
            {
                Id = Guid.NewGuid().ToString(),
                PartitionKey = Application.DefaultPartitionKey,
                Enabled = true,
                ExternalNotificationsReader = true,
                DisplayName = "reader-app-B",
                ContactEmail = "reader-b@test.test",
                AadClientId = ReaderClientId,
                AadTenantId = ReaderTenantId,
                CreatedAt = DateTimeProvider.UtcNow,
                CreatedBy = "admin@test.test",
                ModifiedAt = DateTime.MinValue,
                ModifiedBy = string.Empty,
            };
            this.dbContext.Applications.Add(readerApp);

            // Webhook subscription is created by an admin USER (applications are
            // forbidden from creating subscriptions) and carries no owning-application
            // field — there is no per-application ownership to bind a read against.
            var subscription = new WebhookSubscription
            {
                Id = Guid.NewGuid().ToString(),
                PartitionKey = WebhookSubscription.DefaultPartitionKey,
                Enabled = true,
                EventType = WebhookEventType.AccessRequestCreated,
                Url = "https://other-party.example/webhook",
                Authenticate = false,
                AuthenticationResource = string.Empty,
                WebhookCallDelay = TimeSpan.Zero,
                ContactEmail = "owner-a@test.test",
                CreatedAt = DateTimeProvider.UtcNow,
                CreatedBy = "User admin@test.test",
                ModifiedAt = DateTime.MinValue,
                ModifiedBy = string.Empty,
            };
            this.dbContext.WebhookSubscriptions.Add(subscription);

            var accessRequest = new AccessRequest
            {
                Id = Guid.NewGuid().ToString(),
                PartitionKey = "0001",
                SubjectType = SubjectType.User,
                SubjectName = "requestor@test.test",
                SubjectId = "requestor@test.test",
                ObjectName = "secret-1",
                Permission = SafeExchange.Core.Permissions.PermissionType.Read,
                RequestedAt = DateTimeProvider.UtcNow,
                Status = RequestStatus.InProgress,
                FinishedBy = string.Empty,
                FinishedAt = DateTime.MinValue,
                Recipients = new List<RequestRecipient>(),
            };
            accessRequest.Recipients.Add(new RequestRecipient
            {
                AccessRequestId = accessRequest.Id,
                SubjectType = SubjectType.User,
                SubjectName = recipientUpn,
                SubjectId = recipientUpn,
            });
            this.dbContext.AccessRequests.Add(accessRequest);

            var notificationData = new WebhookNotificationData(
                subscription.Id, WebhookEventType.AccessRequestCreated, accessRequest.Id);
            this.dbContext.WebhookNotificationData.Add(notificationData);

            await this.dbContext.SaveChangesAsync();
            this.dbContext.ChangeTracker.Clear();

            return notificationData.Id;
        }

        private void ActAsReaderApplication()
        {
            this.tokenHelper.IsUser = false;
            this.tokenHelper.TenantId = ReaderTenantId;
            this.tokenHelper.ClientId = ReaderClientId;
        }

        // ---------------------------------------------------------------------
        // Finding premise: the handler looks up the notification by id alone.
        // This documents the *current* runtime behavior — a registered reader
        // application receives full notification details (recipient UPNs) for a
        // notification whose webhook subscription belongs to another party,
        // purely by presenting the notification id. There is NO data-layer
        // partitioning to the caller.
        // ---------------------------------------------------------------------
        [Test]
        public async Task ReaderApplication_ReadsNotificationByIdAlone_ReturnsDetails()
        {
            var notificationId = await this.SeedNotificationGraphAsync("approver@test.test");
            this.ActAsReaderApplication();

            var request = TestFactory.CreateHttpRequestData("get");
            var principal = new ClaimsPrincipal(new ClaimsIdentity());
            var response = await this.handler.Run(notificationId, request, principal, this.logger) as TestHttpResponseData;

            Assert.That(response, Is.Not.Null);
            // The caller is NOT blocked (no Forbidden / NotFound) — the record is
            // disclosed by id alone.
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var body = response.ReadBodyAsJson<BaseResponseObject<NotificationDataOutput>>();
            Assert.That(body?.Status, Is.EqualTo("ok"));
            Assert.That(body!.Result.RecipientUpns, Has.Member("approver@test.test"));
        }

        // ---------------------------------------------------------------------
        // The ACTUAL protection: the endpoint is application-only and requires
        // the ExternalNotificationsReader role. These two tests confirm the
        // OWASP P0 "missing return" fall-through is closed in the current code.
        // ---------------------------------------------------------------------
        [Test]
        public async Task UserPrincipal_IsForbidden()
        {
            var notificationId = await this.SeedNotificationGraphAsync("approver@test.test");

            this.tokenHelper.IsUser = true;
            this.tokenHelper.Upn = "regular.user@test.test";

            var request = TestFactory.CreateHttpRequestData("get");
            var principal = new ClaimsPrincipal(new ClaimsIdentity());
            var response = await this.handler.Run(notificationId, request, principal, this.logger) as TestHttpResponseData;

            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        }

        [Test]
        public async Task NonReaderApplication_IsForbidden()
        {
            var notificationId = await this.SeedNotificationGraphAsync("approver@test.test");

            // A registered application that is NOT an ExternalNotificationsReader.
            var nonReader = new Application
            {
                Id = Guid.NewGuid().ToString(),
                PartitionKey = Application.DefaultPartitionKey,
                Enabled = true,
                ExternalNotificationsReader = false,
                DisplayName = "non-reader-app",
                ContactEmail = "nonreader@test.test",
                AadClientId = "00000000-0000-0000-0000-0000000000c2",
                AadTenantId = "00000000-0000-0000-0000-0000000000c1",
                CreatedAt = DateTimeProvider.UtcNow,
                CreatedBy = "admin@test.test",
                ModifiedAt = DateTime.MinValue,
                ModifiedBy = string.Empty,
            };
            this.dbContext.Applications.Add(nonReader);
            await this.dbContext.SaveChangesAsync();
            this.dbContext.ChangeTracker.Clear();

            this.tokenHelper.IsUser = false;
            this.tokenHelper.TenantId = "00000000-0000-0000-0000-0000000000c1";
            this.tokenHelper.ClientId = "00000000-0000-0000-0000-0000000000c2";

            var request = TestFactory.CreateHttpRequestData("get");
            var principal = new ClaimsPrincipal(new ClaimsIdentity());
            var response = await this.handler.Run(notificationId, request, principal, this.logger) as TestHttpResponseData;

            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        }

        private sealed class FakeTokenHelper : ITokenHelper
        {
            public bool IsUser { get; set; }
            public string Upn { get; set; } = string.Empty;
            public string ClientId { get; set; } = string.Empty;
            public string TenantId { get; set; } = string.Empty;

            public TokenType GetTokenType(ClaimsPrincipal principal) => TokenType.AccessToken;
            public bool IsUserToken(ClaimsPrincipal principal) => this.IsUser;
            public string GetUpn(ClaimsPrincipal principal) => this.Upn;
            public string GetApplicationClientId(ClaimsPrincipal principal) => this.ClientId;
            public string GetDisplayName(ClaimsPrincipal principal) => string.Empty;
            public string? GetObjectId(ClaimsPrincipal? principal) => string.Empty;
            public string? GetTenantId(ClaimsPrincipal? principal) => this.TenantId;
            public AccountIdAndToken GetAccountIdAndToken(HttpRequestData request, ClaimsPrincipal principal)
                => new AccountIdAndToken("account", "token");
        }
    }
}
