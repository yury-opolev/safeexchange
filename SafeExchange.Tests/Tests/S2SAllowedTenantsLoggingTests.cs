/// <summary>
/// S2SAllowedTenantsLoggingTests — regression tests for the observability of a
/// misconfigured Authentication:S2SAllowedTenants value. Parsing fails closed (empty
/// list, no S2S tenants trusted) which is correct security behavior, but "silently
/// off" is hard to diagnose. Both handlers that parse the allowlist must pass their
/// ILogger through so a malformed value leaves a trace instead of vanishing.
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
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    [TestFixture]
    public class S2SAllowedTenantsLoggingTests
    {
        private const string HomeTenant = "00000000-0000-0000-0000-000000000001";
        private const string SomeClientId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        // A value that is syntactically not a JSON array, so ParseList fails closed and
        // (with a logger) emits an error naming the offending setting.
        private const string MalformedAllowlist = "not json at all";

        private SafeExchangeDbContext dbContext = null!;
        private ITokenHelper tokenHelper = null!;
        private GlobalFilters globalFilters = null!;

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
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
                .UseInMemoryDatabase($"{nameof(S2SAllowedTenantsLoggingTests)}-{Guid.NewGuid()}")
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

        [Test]
        public async Task AllowedTenants_endpoint_logs_error_on_malformed_config()
        {
            var handler = this.CreateHandler(MalformedAllowlist);
            var recording = new RecordingLogger();

            var request = TestFactory.CreateHttpRequestData("get");
            var response = await handler.RunListAllowedTenants(request, Caller(), recording) as TestHttpResponseData;

            // Fails closed to an empty list — the endpoint still succeeds ...
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            // ... but the misconfiguration must leave a diagnostic trace naming the setting.
            Assert.That(recording.Has(LogLevel.Error, "S2SAllowedTenants"), Is.True,
                "malformed S2SAllowedTenants must be logged so an operator can diagnose the empty picker");
        }

        [Test]
        public async Task Register_logs_error_on_malformed_config()
        {
            var handler = this.CreateHandler(MalformedAllowlist);
            var recording = new RecordingLogger();

            var request = TestFactory.CreateHttpRequestData("post");
            request.SetBodyAsJson(new S2SAppRegistrationInput
            {
                DisplayName = "MyDaemonApp",
                AadClientId = SomeClientId,
                AadTenantId = null, // defaults to the caller's home tenant under a fail-closed (empty) allowlist
                AdditionalOwners = new List<S2SAppOwnerInput>
                {
                    new S2SAppOwnerInput { SubjectType = OwnerSubjectType.User, SubjectId = "coowner@test.test", SubjectName = "Co Owner" },
                },
            });

            var response = await handler.RunRegister(request, Caller(), recording) as TestHttpResponseData;

            // Malformed allowlist parses as empty -> legacy home-tenant default -> registration succeeds ...
            Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            // ... but silently disabling the tenant restriction must be traceable.
            Assert.That(recording.Has(LogLevel.Error, "S2SAllowedTenants"), Is.True,
                "malformed S2SAllowedTenants must be logged on the registration path too");
        }
    }
}
