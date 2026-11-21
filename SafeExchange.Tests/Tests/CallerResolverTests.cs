/// <summary>
/// CallerResolverTests
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Model;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    [TestFixture]
    public class CallerResolverTests
    {
        private const string TenantId = "00000000-0000-0000-0000-000000000001";

        private const string UserUpn = "user@resolver.test";

        private const string AgentClientId = "00000000-0000-0000-0000-0000000000c1";

        private const string AgentDisplayName = "resolver-agent";

        private const string AllowedGroup = "00000000-0000-0000-0000-0000000000d1";

        private const string OtherGroup = "00000000-0000-0000-0000-0000000000d2";

        private SafeExchangeDbContext dbContext = null!;

        private GloballyAllowedGroupsConfiguration groups = null!;

        private CallerResolver resolver = null!;

        [SetUp]
        public void SetUp()
        {
            var options = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseInMemoryDatabase($"{nameof(CallerResolverTests)}-{Guid.NewGuid()}")
                .Options;
            this.dbContext = new SafeExchangeDbContext(options);
            this.groups = new GloballyAllowedGroupsConfiguration { AllowedGroups = string.Empty };

            var tokenHelper = new TokenHelper(new ConfigurationBuilder().Build(), NullLogger<TokenHelper>.Instance);
            var groupsMonitor = new Mock<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>();
            groupsMonitor.Setup(m => m.CurrentValue).Returns(() => this.groups);
            this.resolver = new CallerResolver(groupsMonitor.Object, tokenHelper, this.dbContext, NullLogger<CallerResolver>.Instance);
        }

        [TearDown]
        public void TearDown()
        {
            this.dbContext.Database.EnsureDeleted();
            this.dbContext.Dispose();
        }

        [Test]
        public async Task User_ResolvesToUserCaller_WithTokenDisplayName()
        {
            var resolution = await this.resolver.ResolveAsync(UserPrincipal(UserUpn, "Resolver User"), CallerChannels.Rest, null);

            AssertCaller(resolution, SubjectType.User, UserUpn, "Resolver User", CallerChannels.Rest, null);
        }

        [Test]
        public async Task User_WithoutNameClaim_DisplayNameFallsBackToUpn()
        {
            var resolution = await this.resolver.ResolveAsync(UserPrincipal(UserUpn), CallerChannels.Mcp, "copilot-cli/1.0.114");

            AssertCaller(resolution, SubjectType.User, UserUpn, UserUpn, CallerChannels.Mcp, "copilot-cli/1.0.114");
        }

        [Test]
        public async Task RegisteredApplication_ResolvesToApplicationCaller_NamedAfterTheRegistration()
        {
            await this.SeedApplicationAsync(enabled: true);

            var resolution = await this.resolver.ResolveAsync(AppPrincipal(AgentClientId), CallerChannels.Mcp, "agent/2.0");

            AssertCaller(resolution, SubjectType.Application, AgentDisplayName, AgentDisplayName, CallerChannels.Mcp, "agent/2.0");
        }

        [Test]
        public async Task UnregisteredApplication_IsDenied()
        {
            var resolution = await this.resolver.ResolveAsync(AppPrincipal(AgentClientId), CallerChannels.Rest, null);

            AssertDenied(resolution, "Application is not registered or disabled.");
        }

        [Test]
        public async Task DisabledApplication_IsDenied()
        {
            await this.SeedApplicationAsync(enabled: false);

            var resolution = await this.resolver.ResolveAsync(AppPrincipal(AgentClientId), CallerChannels.Mcp, null);

            AssertDenied(resolution, "Application is not registered or disabled.");
        }

        [Test]
        public async Task GroupConfigured_MemberUser_Resolves()
        {
            this.groups.AllowedGroups = AllowedGroup;
            await this.SeedUserAsync(OtherGroup, AllowedGroup);

            var resolution = await this.resolver.ResolveAsync(UserPrincipal(UserUpn), CallerChannels.Rest, null);

            AssertCaller(resolution, SubjectType.User, UserUpn, UserUpn, CallerChannels.Rest, null);
        }

        [Test]
        public async Task GroupConfigured_NonMemberUser_IsDenied()
        {
            this.groups.AllowedGroups = AllowedGroup;
            await this.SeedUserAsync(OtherGroup);

            var resolution = await this.resolver.ResolveAsync(UserPrincipal(UserUpn), CallerChannels.Rest, null);

            AssertDenied(resolution, "Not a member of a global group.");
        }

        [Test]
        public async Task GroupConfigured_UserNotInUsers_IsDenied()
        {
            this.groups.AllowedGroups = AllowedGroup;

            var resolution = await this.resolver.ResolveAsync(UserPrincipal(UserUpn), CallerChannels.Rest, null);

            AssertDenied(resolution, "Not a member of a global group.");
        }

        [Test]
        public async Task GroupConfigured_RegisteredApplication_IsDeniedByTheGroupGate()
        {
            await this.SeedApplicationAsync(enabled: true);
            this.groups.AllowedGroups = AllowedGroup;

            var resolution = await this.resolver.ResolveAsync(AppPrincipal(AgentClientId), CallerChannels.Mcp, null);

            AssertDenied(resolution, "Not a member of a global group.");
        }

        [Test]
        public async Task GroupConfigured_UnregisteredApplication_GroupGateRunsFirst()
        {
            this.groups.AllowedGroups = AllowedGroup;

            var resolution = await this.resolver.ResolveAsync(AppPrincipal(AgentClientId), CallerChannels.Rest, null);

            AssertDenied(resolution, "Not a member of a global group.");
        }

        [Test]
        public async Task GroupConfiguration_IsReadOnEveryCall()
        {
            Assert.That((await this.resolver.ResolveAsync(UserPrincipal(UserUpn), CallerChannels.Rest, null)).IsAllowed, Is.True);

            this.groups = new GloballyAllowedGroupsConfiguration { AllowedGroups = AllowedGroup };

            AssertDenied(await this.resolver.ResolveAsync(UserPrincipal(UserUpn), CallerChannels.Rest, null), "Not a member of a global group.");
        }

        [Test]
        public void ConfigureServices_RegistersCallerResolverAsScoped()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "CosmosDb:CosmosDbEndpoint", "https://localhost:8081/" },
                    { "CosmosDb:DatabaseName", "startup-test" },
                })
                .Build();
            var services = new ServiceCollection();

            SafeExchangeStartup.ConfigureServices(configuration, services);

            var registrations = services
                .Where(d => d.ServiceType == typeof(ICallerResolver))
                .Select(d => $"{d.Lifetime} {d.ImplementationType?.Name}")
                .ToList();
            Assert.That(registrations, Is.EqualTo(new[] { "Scoped CallerResolver" }));

            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging();
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            using var scope = provider.CreateScope();
            Assert.That(scope.ServiceProvider.GetRequiredService<ICallerResolver>(), Is.InstanceOf<CallerResolver>());
        }

        private static void AssertCaller(CallerResolution resolution, SubjectType subjectType, string subjectId, string displayName, string channel, string? clientLabel)
        {
            Assert.That(resolution.IsAllowed, Is.True, $"Denied: {resolution.Denial}");
            Assert.That(resolution.Denial, Is.Null);
            var caller = resolution.Caller!;
            Assert.That(caller.SubjectType, Is.EqualTo(subjectType));
            Assert.That(caller.SubjectId, Is.EqualTo(subjectId));
            Assert.That(caller.DisplayName, Is.EqualTo(displayName));
            Assert.That(caller.GroupIds, Is.Empty);
            Assert.That(caller.Channel, Is.EqualTo(channel));
            Assert.That(caller.ClientLabel, Is.EqualTo(clientLabel));
        }

        private static void AssertDenied(CallerResolution resolution, string error)
        {
            Assert.That(resolution.IsAllowed, Is.False);
            Assert.That(resolution.Caller, Is.Null);
            Assert.That(resolution.Denial, Is.EqualTo(new CallerDenial(HttpStatusCode.Forbidden, "forbidden", error, string.Empty)));
        }

        private static ClaimsPrincipal UserPrincipal(string upn, string? name = null)
        {
            var claims = new List<Claim>
            {
                new("upn", upn),
                new("oid", "00000000-0000-0000-0000-0000000000a1"),
                new("tid", TenantId),
            };
            if (name is not null)
            {
                claims.Add(new Claim("name", name));
            }

            return new ClaimsPrincipal(new ClaimsIdentity(claims));
        }

        // App-only (client-credentials) v2 token shape: azp + azpacr and no scope claim.
        private static ClaimsPrincipal AppPrincipal(string clientId)
            => new(new ClaimsIdentity(new[]
            {
                new Claim("azp", clientId),
                new Claim("azpacr", "1"),
                new Claim("oid", "00000000-0000-0000-0000-0000000000b1"),
                new Claim("tid", TenantId),
            }));

        private async Task SeedApplicationAsync(bool enabled)
        {
            this.dbContext.Applications.Add(new Application
            {
                Id = Guid.NewGuid().ToString(),
                PartitionKey = Application.DefaultPartitionKey,
                Enabled = enabled,
                DisplayName = AgentDisplayName,
                ContactEmail = "agent-owner@resolver.test",
                AadClientId = AgentClientId,
                AadTenantId = TenantId,
                CreatedAt = DateTime.UnixEpoch,
                CreatedBy = "resolver-test",
                ModifiedAt = DateTime.MinValue,
                ModifiedBy = string.Empty,
            });
            await this.dbContext.SaveChangesAsync();
        }

        private async Task SeedUserAsync(params string[] groupIds)
        {
            this.dbContext.Users.Add(new User("Resolver User", "00000000-0000-0000-0000-0000000000a1", TenantId, UserUpn, UserUpn)
            {
                Groups = groupIds.Select(g => new UserGroup { AadGroupId = g }).ToList(),
            });
            await this.dbContext.SaveChangesAsync();
        }
    }
}
