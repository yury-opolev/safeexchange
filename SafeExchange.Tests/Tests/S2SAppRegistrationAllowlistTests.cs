/// <summary>
/// S2SAppRegistrationAllowlistTests — verifies the configuration-time S2S tenant
/// allowlist is enforced at registration (the security boundary the owner asked for:
/// users may only register apps under tenants the operator pre-approved) and that the
/// allowed-tenants endpoint surfaces that list. Backward compat: an empty allowlist
/// keeps the legacy "default to the caller's home tenant" behavior.
/// </summary>

namespace SafeExchange.Tests
{
    using Azure.Core.Serialization;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Applications;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    [TestFixture]
    public class S2SAppRegistrationAllowlistTests
    {
        private const string HomeTenant = "00000000-0000-0000-0000-000000000001";
        private const string AllowedTenant = "11111111-1111-1111-1111-111111111111";
        private const string ForeignTenant = "99999999-9999-9999-9999-999999999999";
        private const string SomeClientId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        private ILogger logger = null!;
        private SafeExchangeDbContext dbContext = null!;
        private ITokenHelper tokenHelper = null!;
        private GlobalFilters globalFilters = null!;

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            this.logger = TestFactory.CreateLogger();
            this.tokenHelper = new TestTokenHelper();

            var groups = Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(
                x => x.CurrentValue == new GloballyAllowedGroupsConfiguration());
            var admin = Mock.Of<IOptionsMonitor<AdminConfiguration>>(
                x => x.CurrentValue == new AdminConfiguration());
            this.globalFilters = new GlobalFilters(groups, admin, this.tokenHelper, TestFactory.CreateLogger<GlobalFilters>());

            var workerOptions = Options.Create(new WorkerOptions() { Serializer = new JsonObjectSerializer() });
            var serviceProviderMock = new Mock<IServiceProvider>();
            serviceProviderMock.Setup(x => x.GetService(typeof(IOptions<WorkerOptions>))).Returns(workerOptions);
            TestFactory.FunctionContext.InstanceServices = serviceProviderMock.Object;

            DateTimeProvider.SpecifiedDateTime = DateTime.UtcNow;
            DateTimeProvider.UseSpecifiedDateTime = true;
        }

        [OneTimeTearDown]
        public void OneTimeCleanup() => DateTimeProvider.UseSpecifiedDateTime = false;

        [SetUp]
        public void Setup()
        {
            var dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseInMemoryDatabase($"{nameof(S2SAppRegistrationAllowlistTests)}-{Guid.NewGuid()}")
                .Options;
            this.dbContext = new SafeExchangeDbContext(dbContextOptions);
        }

        [TearDown]
        public void Cleanup()
        {
            this.dbContext.Database.EnsureDeleted();
            this.dbContext.Dispose();
        }

        private SafeExchangeS2SApps CreateHandler(string s2sAllowedTenantsJson)
        {
            var features = Mock.Of<IOptionsMonitor<Features>>(x => x.CurrentValue == new Features { S2SAppsSelfService = true });
            var limits = Mock.Of<IOptionsMonitor<Limits>>(x => x.CurrentValue == new Limits());
            var authConfig = Mock.Of<IOptionsMonitor<AuthenticationConfiguration>>(
                x => x.CurrentValue == new AuthenticationConfiguration { S2SAllowedTenants = s2sAllowedTenantsJson });
            var ownerService = new ApplicationOwnerService(this.dbContext);
            return new SafeExchangeS2SApps(this.dbContext, this.tokenHelper, this.globalFilters, ownerService, features, limits, authConfig);
        }

        private static ClaimsPrincipal Caller()
            => new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
            {
                new Claim("upn", "registrar@test.test"),
                new Claim("oid", "00000000-0000-0000-0000-0000000000d1"),
                new Claim("tid", HomeTenant),
            }));

        private static S2SAppRegistrationInput RegistrationFor(string? tenantId)
            => new S2SAppRegistrationInput
            {
                DisplayName = "MyDaemonApp",
                AadClientId = SomeClientId,
                AadTenantId = tenantId,
                AdditionalOwners = new List<S2SAppOwnerInput>
                {
                    new S2SAppOwnerInput { SubjectType = OwnerSubjectType.User, SubjectId = "coowner@test.test", SubjectName = "Co Owner" },
                },
            };

        private async Task<TestHttpResponseData> RegisterAsync(SafeExchangeS2SApps handler, S2SAppRegistrationInput input)
        {
            var request = TestFactory.CreateHttpRequestData("post");
            request.SetBodyAsJson(input);
            return await handler.RunRegister(request, Caller(), this.logger) as TestHttpResponseData
                ?? throw new InvalidOperationException("null response");
        }

        private static string AllowlistJson(params string[] tenantIds)
            => "[" + string.Join(",", tenantIds.Select(t => $"{{\"tenantId\":\"{t}\",\"displayName\":\"{t}\"}}")) + "]";

        [Test]
        public async Task Register_rejects_tenant_not_in_allowlist()
        {
            var handler = this.CreateHandler(AllowlistJson(AllowedTenant));

            var response = await this.RegisterAsync(handler, RegistrationFor(ForeignTenant));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(await this.dbContext.Applications.CountAsync(), Is.EqualTo(0), "no app must be created for a disallowed tenant");
        }

        [Test]
        public async Task Register_accepts_tenant_in_allowlist()
        {
            var handler = this.CreateHandler(AllowlistJson(AllowedTenant, ForeignTenant.Replace("9", "8")));

            var response = await this.RegisterAsync(handler, RegistrationFor(AllowedTenant));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            var created = await this.dbContext.Applications.SingleAsync();
            Assert.That(created.AadTenantId, Is.EqualTo(AllowedTenant));
        }

        [Test]
        public async Task Register_with_empty_allowlist_defaults_to_home_tenant()
        {
            var handler = this.CreateHandler(string.Empty);

            var response = await this.RegisterAsync(handler, RegistrationFor(null));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            var created = await this.dbContext.Applications.SingleAsync();
            Assert.That(created.AadTenantId, Is.EqualTo(HomeTenant), "empty allowlist keeps the legacy home-tenant default");
        }

        [Test]
        public async Task AllowedTenants_endpoint_returns_configured_list()
        {
            var handler = this.CreateHandler(AllowlistJson(AllowedTenant));

            var request = TestFactory.CreateHttpRequestData("get");
            var response = await handler.RunListAllowedTenants(request, Caller(), this.logger) as TestHttpResponseData;

            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = response.ReadBodyAsJson<BaseResponseObject<List<S2SAllowedTenantOutput>>>();
            Assert.That(body!.Result!.Select(t => t.TenantId), Is.EqualTo(new[] { AllowedTenant }));
        }
    }
}
