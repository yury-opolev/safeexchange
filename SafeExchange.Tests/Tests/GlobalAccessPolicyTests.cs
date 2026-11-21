/// <summary>
/// GlobalAccessPolicyTests
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Model;
    using System;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    [TestFixture]
    public class GlobalAccessPolicyTests
    {
        private const string TenantId = "00000000-0000-0000-0000-000000000001";

        private const string UserUpn = "user@policy.test";

        private const string FirstGroup = "00000000-0000-0000-0000-0000000000d1";

        private const string SecondGroup = "00000000-0000-0000-0000-0000000000d3";

        private const string OtherGroup = "00000000-0000-0000-0000-0000000000d2";

        private SafeExchangeDbContext dbContext = null!;

        private ITokenHelper tokenHelper = null!;

        private RecordingLogger log = null!;

        [SetUp]
        public void SetUp()
        {
            var options = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseInMemoryDatabase($"{nameof(GlobalAccessPolicyTests)}-{Guid.NewGuid()}")
                .Options;
            this.dbContext = new SafeExchangeDbContext(options);
            this.tokenHelper = new TokenHelper(new ConfigurationBuilder().Build(), NullLogger<TokenHelper>.Instance);
            this.log = new RecordingLogger();
        }

        [TearDown]
        public void TearDown()
        {
            this.dbContext.Database.EnsureDeleted();
            this.dbContext.Dispose();
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(",")]
        public async Task NoGroupConfigured_AllowsEveryone_WithoutLogging(string? allowedGroups)
        {
            var denial = await this.Policy(allowedGroups).EvaluateAsync(UserPrincipal(UserUpn), this.dbContext);

            Assert.That(denial, Is.Null);
            Assert.That(this.log.Entries, Is.Empty);
        }

        [Test]
        public async Task GroupConfigured_UserNotInUsers_ForbiddenWithoutLogging()
        {
            var denial = await this.Policy(FirstGroup).EvaluateAsync(UserPrincipal(UserUpn), this.dbContext);

            AssertNotAMember(denial);
            Assert.That(this.log.Entries, Is.Empty);
        }

        [Test]
        public async Task GroupConfigured_UserInSecondAllowedGroup_AllowedAndLogged()
        {
            await this.SeedUserAsync(UserUpn, "tid-member", OtherGroup, SecondGroup);

            var denial = await this.Policy($"{FirstGroup},{SecondGroup}").EvaluateAsync(UserPrincipal(UserUpn), this.dbContext);

            Assert.That(denial, Is.Null);
            Assert.That(this.log.Entries.Select(e => (e.Level, e.Message)), Is.EqualTo(new[]
            {
                (LogLevel.Information, $"User (tid tid-member) is a member of global access group '{SecondGroup}', authorized."),
            }));
        }

        [Test]
        public async Task GroupConfigured_UserWithoutAllowedGroup_ForbiddenAndLogged()
        {
            await this.SeedUserAsync(UserUpn, "tid-outsider", OtherGroup);

            var denial = await this.Policy(FirstGroup).EvaluateAsync(UserPrincipal(UserUpn), this.dbContext);

            AssertNotAMember(denial);
            Assert.That(this.log.Entries.Select(e => (e.Level, e.Message)), Is.EqualTo(new[]
            {
                (LogLevel.Information, "User (tid tid-outsider) is not a member of any global access group, unauthorized."),
            }));
        }

        [Test]
        public async Task GroupConfigured_ApplicationPrincipal_Forbidden()
        {
            await this.SeedUserAsync(UserUpn, "tid-member", FirstGroup);

            var denial = await this.Policy(FirstGroup).EvaluateAsync(AppPrincipal(), this.dbContext);

            AssertNotAMember(denial);
        }

        private static void AssertNotAMember(CallerDenial? denial)
        {
            Assert.That(denial, Is.Not.Null);
            Assert.That(denial!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(denial.Status, Is.EqualTo("forbidden"));
            Assert.That(denial.Error, Is.EqualTo("Not a member of a global group."));
            Assert.That(denial.SubStatus, Is.EqualTo(string.Empty));
        }

        private static ClaimsPrincipal UserPrincipal(string upn)
            => new(new ClaimsIdentity(new[]
            {
                new Claim("upn", upn),
                new Claim("oid", "00000000-0000-0000-0000-0000000000a1"),
                new Claim("tid", TenantId),
            }));

        private static ClaimsPrincipal AppPrincipal()
            => new(new ClaimsIdentity(new[]
            {
                new Claim("azp", "00000000-0000-0000-0000-0000000000c1"),
                new Claim("azpacr", "1"),
                new Claim("oid", "00000000-0000-0000-0000-0000000000b1"),
                new Claim("tid", TenantId),
            }));

        private GlobalAccessPolicy Policy(string? allowedGroups)
            => new(new GloballyAllowedGroupsConfiguration { AllowedGroups = allowedGroups! }, this.tokenHelper, this.log);

        private async Task SeedUserAsync(string upn, string telemetryId, params string[] groupIds)
        {
            this.dbContext.Users.Add(new User("Policy User", "00000000-0000-0000-0000-0000000000a1", TenantId, upn, upn)
            {
                TelemetryId = telemetryId,
                Groups = groupIds.Select(g => new UserGroup { AadGroupId = g }).ToList(),
            });
            await this.dbContext.SaveChangesAsync();
        }
    }
}
