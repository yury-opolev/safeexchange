/// <summary>
/// ApplicationSearchScanCapTests — guards the server-side bound on POST /v2/application-search.
/// The endpoint orders and caps results in memory (to avoid a Cosmos composite index for
/// OrderBy), so the DB scan itself must be bounded — otherwise a very short/broad term pulls
/// the entire matching set into memory. The candidate scan must never materialize more than
/// ScanCap rows regardless of how many applications match.
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Model;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Threading.Tasks;

    [TestFixture]
    public class ApplicationSearchScanCapTests
    {
        private SafeExchangeDbContext dbContext = null!;
        private SafeExchangeApplicationSearch handler = null!;

        [SetUp]
        public void Setup()
        {
            var dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseInMemoryDatabase($"{nameof(ApplicationSearchScanCapTests)}-{Guid.NewGuid()}")
                .Options;
            this.dbContext = new SafeExchangeDbContext(dbContextOptions);

            var tokenHelper = new TestTokenHelper();
            var groups = Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(
                x => x.CurrentValue == new GloballyAllowedGroupsConfiguration());
            var admin = Mock.Of<IOptionsMonitor<AdminConfiguration>>(
                x => x.CurrentValue == new AdminConfiguration());
            var globalFilters = new GlobalFilters(groups, admin, tokenHelper, TestFactory.CreateLogger<GlobalFilters>());
            this.handler = new SafeExchangeApplicationSearch(this.dbContext, tokenHelper, globalFilters);
        }

        [TearDown]
        public void Cleanup()
        {
            this.dbContext.Database.EnsureDeleted();
            this.dbContext.Dispose();
        }

        [Test]
        public async Task Candidate_scan_is_bounded_by_ScanCap_even_when_more_match()
        {
            // Seed comfortably more enabled, name-matching apps than the scan cap.
            var total = SafeExchangeApplicationSearch.ScanCap + 50;
            for (var i = 0; i < total; i++)
            {
                this.dbContext.Applications.Add(new Application
                {
                    Id = Guid.NewGuid().ToString(),
                    PartitionKey = Application.DefaultPartitionKey,
                    DisplayName = $"MatchingApp{i:D4}",
                    AadTenantId = "11111111-1111-1111-1111-111111111111",
                    AadClientId = Guid.NewGuid().ToString(),
                    ContactEmail = "owner@test.test",
                    Enabled = true,
                    CreatedBy = "User owner@test.test",
                    ModifiedBy = string.Empty,
                });
            }

            await this.dbContext.SaveChangesAsync();

            // "matchingapp" (lowercased, as RunSearch normalizes) matches every seeded app.
            var scanned = await this.handler.ScanMatchingApplicationsAsync("matchingapp");

            Assert.That(scanned.Count, Is.EqualTo(SafeExchangeApplicationSearch.ScanCap),
                "the DB scan must be capped server-side, not materialize the whole matching set");
        }
    }
}
