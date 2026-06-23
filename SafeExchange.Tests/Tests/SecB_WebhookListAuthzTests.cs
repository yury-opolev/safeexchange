/// <summary>
/// SecB_WebhookListAuthzTests — security regression tests for the webhook
/// subscription list admin endpoint (Cluster B, CWE-285 / CWE-200).
///
/// The list handler lives under AdminFunctions but historically delegated to the
/// non-admin GlobalFilters.GetFilterResultAsync path, letting any authenticated
/// non-admin user enumerate every webhook subscription (URLs, event types, ids).
/// These tests assert the handler enforces the admin authorization gate:
///   * a non-admin authenticated caller receives Forbidden (no data),
///   * a configured admin caller still succeeds and gets the data.
///
/// They use EF Core InMemory (no Cosmos emulator) and the AdminGroupFilter the
/// sibling admin handlers already use, following the established admin-authz pattern.
/// </summary>

namespace SafeExchange.Tests
{
    using Azure.Core.Serialization;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Options;
    using Microsoft.Extensions.Logging;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    [TestFixture]
    public class SecB_WebhookListAuthzTests
    {
        private const string AdminOid = "00000321-0000-0000-0000-000000000321";

        private ILogger logger;

        private SafeExchangeDbContext dbContext;

        private GlobalFilters globalFilters;

        private SafeExchangeWebhookSubscriptionsList handler;

        private ClaimsPrincipal adminPrincipal;
        private ClaimsPrincipal nonAdminPrincipal;

        [SetUp]
        public void Setup()
        {
            this.logger = TestFactory.CreateLogger();

            var options = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseInMemoryDatabase($"SecB-WebhookListAuthz-{Guid.NewGuid()}")
                .Options;
            this.dbContext = new SafeExchangeDbContext(options);

            this.adminPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
            {
                new Claim("upn", "admin@test.test"),
                new Claim("displayname", "Admin User"),
                new Claim("oid", AdminOid),
                new Claim("tid", "00000000-0000-0000-0000-000000000001"),
            }));

            this.nonAdminPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
            {
                new Claim("upn", "plain@test.test"),
                new Claim("displayname", "Plain User"),
                new Claim("oid", "00000000-0000-0000-0000-000000000099"),
                new Claim("tid", "00000000-0000-0000-0000-000000000001"),
            }));

            // Empty global groups => the non-admin GlobalAccessFilter would let any
            // authenticated user through; only AdminGroupFilter blocks non-admins.
            var gagc = new GloballyAllowedGroupsConfiguration();
            var groupsConfiguration = Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(
                x => x.CurrentValue == gagc);

            var ac = new AdminConfiguration { AdminUsers = AdminOid };
            var adminConfiguration = Mock.Of<IOptionsMonitor<AdminConfiguration>>(
                x => x.CurrentValue == ac);

            var tokenHelper = new TestTokenHelper();
            this.globalFilters = new GlobalFilters(
                groupsConfiguration, adminConfiguration, tokenHelper,
                TestFactory.CreateLogger<GlobalFilters>());

            this.handler = new SafeExchangeWebhookSubscriptionsList(
                this.dbContext, tokenHelper, this.globalFilters);

            var workerOptions = Options.Create(new WorkerOptions { Serializer = new JsonObjectSerializer() });
            var serviceProviderMock = new Mock<IServiceProvider>();
            serviceProviderMock
                .Setup(x => x.GetService(typeof(IOptions<WorkerOptions>)))
                .Returns(workerOptions);
            TestFactory.FunctionContext.InstanceServices = serviceProviderMock.Object;

            DateTimeProvider.SpecifiedDateTime = DateTime.UtcNow;
            DateTimeProvider.UseSpecifiedDateTime = true;
        }

        [TearDown]
        public void Cleanup()
        {
            DateTimeProvider.UseSpecifiedDateTime = false;
            this.dbContext.Dispose();
        }

        private async Task SeedSubscriptionAsync()
        {
            this.dbContext.WebhookSubscriptions.Add(new WebhookSubscription
            {
                Id = Guid.NewGuid().ToString(),
                PartitionKey = WebhookSubscription.DefaultPartitionKey,
                Enabled = true,
                EventType = WebhookEventType.AccessRequestCreated,
                Url = "https://secret-webhook.contoso.com/hook",
                ContactEmail = "secret-admin@contoso.com",
                CreatedBy = "admin@test.test",
                CreatedAt = DateTimeProvider.UtcNow,
                ModifiedBy = string.Empty,
            });
            await this.dbContext.SaveChangesAsync();
        }

        [Test]
        public async Task RunList_NonAdminPrincipal_IsForbidden_AndNoData()
        {
            // [GIVEN] a webhook subscription exists and an authenticated non-admin caller.
            await this.SeedSubscriptionAsync();
            var request = TestFactory.CreateHttpRequestData("get");

            // [WHEN] the non-admin invokes the admin list endpoint.
            var response = await this.handler.RunList(request, this.nonAdminPrincipal, this.logger)
                as Utilities.TestHttpResponseData;

            // [THEN] the admin gate rejects the caller; no subscription data is disclosed.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

            var rawJson = response.ReadBodyAsString();
            Assert.That(rawJson, Does.Not.Contain("secret-webhook.contoso.com"));
        }

        [Test]
        public async Task RunList_AdminPrincipal_ReturnsData()
        {
            // [GIVEN] a webhook subscription exists and an authenticated admin caller.
            await this.SeedSubscriptionAsync();
            var request = TestFactory.CreateHttpRequestData("get");

            // [WHEN] the admin invokes the admin list endpoint.
            var response = await this.handler.RunList(request, this.adminPrincipal, this.logger)
                as Utilities.TestHttpResponseData;

            // [THEN] the admin succeeds and receives the subscription overview.
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var body = response.ReadBodyAsJson<BaseResponseObject<List<WebhookSubscriptionOverviewOutput>>>();
            Assert.That(body?.Status, Is.EqualTo("ok"));
            Assert.That(body!.Result, Is.Not.Null);
            Assert.That(body.Result!.Count, Is.EqualTo(1));
            Assert.That(body.Result[0].Url, Is.EqualTo("https://secret-webhook.contoso.com/hook"));
        }
    }
}
