/// <summary>
/// ApplicationSearchTests — end-to-end (EF InMemory) tests for POST /v2/application-search,
/// the backend behind the access-list application picker. Verifies name matching, that only
/// enabled apps are returned, that disambiguation fields are populated, that the search string
/// is validated, and that application callers are forbidden (apps must not enumerate apps).
/// </summary>

namespace SafeExchange.Tests
{
    using Azure.Core.Serialization;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using Microsoft.EntityFrameworkCore;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    [TestFixture]
    public class ApplicationSearchTests
    {
        private ILogger logger = null!;
        private SafeExchangeDbContext dbContext = null!;
        private ITokenHelper userTokenHelper = null!;
        private GlobalFilters globalFilters = null!;
        private SafeExchangeApplicationSearch handler = null!;

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            this.logger = TestFactory.CreateLogger();

            var dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseInMemoryDatabase($"{nameof(ApplicationSearchTests)}-{Guid.NewGuid()}")
                .Options;
            this.dbContext = new SafeExchangeDbContext(dbContextOptions);

            this.userTokenHelper = new TestTokenHelper();
            this.globalFilters = CreateGlobalFilters(this.userTokenHelper);

            var workerOptions = Options.Create(new WorkerOptions() { Serializer = new JsonObjectSerializer() });
            var serviceProviderMock = new Mock<IServiceProvider>();
            serviceProviderMock.Setup(x => x.GetService(typeof(IOptions<WorkerOptions>))).Returns(workerOptions);
            TestFactory.FunctionContext.InstanceServices = serviceProviderMock.Object;

            this.SeedApplications();
        }

        [OneTimeTearDown]
        public void OneTimeCleanup()
        {
            this.dbContext.Database.EnsureDeleted();
            this.dbContext.Dispose();
        }

        [SetUp]
        public void Setup()
        {
            this.handler = new SafeExchangeApplicationSearch(this.dbContext, this.userTokenHelper, this.globalFilters);
        }

        private static GlobalFilters CreateGlobalFilters(ITokenHelper tokenHelper)
        {
            var groups = Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(
                x => x.CurrentValue == new GloballyAllowedGroupsConfiguration());
            var admin = Mock.Of<IOptionsMonitor<AdminConfiguration>>(
                x => x.CurrentValue == new AdminConfiguration());
            return new GlobalFilters(groups, admin, tokenHelper, TestFactory.CreateLogger<GlobalFilters>());
        }

        private void SeedApplications()
        {
            App("PaymentService", "11111111-1111-1111-1111-111111111111", "aaaaaaaa-0000-0000-0000-000000000001", enabled: true);
            App("PaymentGateway", "11111111-1111-1111-1111-111111111111", "aaaaaaaa-0000-0000-0000-000000000002", enabled: true);
            App("BillingDaemon", "22222222-2222-2222-2222-222222222222", "aaaaaaaa-0000-0000-0000-000000000003", enabled: true);
            App("RetiredPaymentApp", "11111111-1111-1111-1111-111111111111", "aaaaaaaa-0000-0000-0000-000000000004", enabled: false);
            this.dbContext.SaveChanges();
        }

        private void App(string displayName, string tenantId, string clientId, bool enabled)
            => this.dbContext.Applications.Add(new Application
            {
                Id = Guid.NewGuid().ToString(),
                PartitionKey = Application.DefaultPartitionKey,
                DisplayName = displayName,
                AadTenantId = tenantId,
                AadClientId = clientId,
                ContactEmail = "owner@test.test",
                Enabled = enabled,
                CreatedBy = "User owner@test.test",
                ModifiedBy = string.Empty,
            });

        private static ClaimsPrincipal User()
            => new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
            {
                new Claim("upn", "searcher@test.test"),
                new Claim("oid", "00000000-0000-0000-0000-0000000000b1"),
                new Claim("tid", "00000000-0000-0000-0000-000000000001"),
            }));

        private async Task<TestHttpResponseData> SearchAsync(ITokenHelper tokenHelper, ClaimsPrincipal principal, string searchString)
        {
            var localHandler = new SafeExchangeApplicationSearch(this.dbContext, tokenHelper, CreateGlobalFilters(tokenHelper));
            var request = TestFactory.CreateHttpRequestData("post");
            request.SetBodyAsJson(new SearchInput { SearchString = searchString });
            return await localHandler.RunSearch(request, principal, this.logger) as TestHttpResponseData
                ?? throw new InvalidOperationException("null response");
        }

        [Test]
        public async Task Search_returns_matching_enabled_apps_with_disambiguation_fields()
        {
            var request = TestFactory.CreateHttpRequestData("post");
            request.SetBodyAsJson(new SearchInput { SearchString = "payment" });

            var response = await this.handler.RunSearch(request, User(), this.logger) as TestHttpResponseData;

            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = response.ReadBodyAsJson<BaseResponseObject<List<ApplicationSearchOutput>>>();
            var names = body!.Result!.Select(a => a.DisplayName).ToList();

            Assert.That(names, Does.Contain("PaymentService"));
            Assert.That(names, Does.Contain("PaymentGateway"));
            Assert.That(names, Does.Not.Contain("BillingDaemon"), "non-matching app must be excluded");
            Assert.That(names, Does.Not.Contain("RetiredPaymentApp"), "disabled app must be excluded");

            var first = body.Result!.First(a => a.DisplayName == "PaymentService");
            Assert.That(first.AadClientId, Is.EqualTo("aaaaaaaa-0000-0000-0000-000000000001"));
            Assert.That(first.AadTenantId, Is.EqualTo("11111111-1111-1111-1111-111111111111"));
        }

        [Test]
        public async Task Search_is_case_insensitive_substring()
        {
            var response = await this.SearchAsync(this.userTokenHelper, User(), "GATEWAY");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = response.ReadBodyAsJson<BaseResponseObject<List<ApplicationSearchOutput>>>();
            Assert.That(body!.Result!.Select(a => a.DisplayName), Is.EqualTo(new[] { "PaymentGateway" }));
        }

        [Test]
        public async Task Search_with_empty_string_is_bad_request()
        {
            var response = await this.SearchAsync(this.userTokenHelper, User(), "   ");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        }

        [Test]
        public async Task Search_with_overlong_string_is_bad_request()
        {
            var response = await this.SearchAsync(this.userTokenHelper, User(), new string('a', 65));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        }

        [Test]
        public async Task Search_with_quote_is_bad_request()
        {
            var response = await this.SearchAsync(this.userTokenHelper, User(), "pay\" or true");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        }

        [Test]
        public async Task Application_caller_is_forbidden()
        {
            var appTokenHelper = new AppOnlyTokenHelper();
            var response = await this.SearchAsync(appTokenHelper, new ClaimsPrincipal(new ClaimsIdentity()), "payment");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        }

        /// <summary>Minimal app-only token helper: classifies the caller as an application.</summary>
        private sealed class AppOnlyTokenHelper : ITokenHelper
        {
            public bool IsUserToken(ClaimsPrincipal principal) => false;
            public TokenType GetTokenType(ClaimsPrincipal principal) => TokenType.AccessToken;
            public string GetApplicationClientId(ClaimsPrincipal principal) => "33333333-3333-3333-3333-333333333333";
            public string? GetTenantId(ClaimsPrincipal? principal) => "44444444-4444-4444-4444-444444444444";
            public string? GetObjectId(ClaimsPrincipal? principal) => "00000000-0000-0000-0000-0000000000c1";
            public string GetUpn(ClaimsPrincipal principal) => string.Empty;
            public string GetDisplayName(ClaimsPrincipal principal) => string.Empty;
            public AccountIdAndToken GetAccountIdAndToken(HttpRequestData request, ClaimsPrincipal principal)
                => new AccountIdAndToken(string.Empty, string.Empty);
        }
    }
}
