/// <summary>
/// ClientRateLimiterTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System;

    [TestFixture]
    public class ClientRateLimiterTests
    {
        private static readonly DateTime Start = new(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);

        [Test]
        public void TheBurst_IsAllowed_ThenTheNextRequestWaitsForTheRefill()
        {
            var limiter = new ClientRateLimiter(10);

            for (var i = 0; i < 3; i++)
            {
                Assert.That(limiter.TryAcquire("a", 60, 3, Start, out _), Is.True, $"request {i + 1}");
            }

            Assert.That(limiter.TryAcquire("a", 60, 3, Start, out var retryAfter), Is.False);
            Assert.That(retryAfter.TotalSeconds, Is.EqualTo(1).Within(0.001));
            Assert.That(limiter.TryAcquire("a", 60, 3, Start.AddSeconds(1), out _), Is.True);
            Assert.That(limiter.TryAcquire("a", 60, 3, Start.AddSeconds(1), out _), Is.False);
        }

        [Test]
        public void AnIdleBucket_RefillsOnlyUpToTheBurst()
        {
            var limiter = new ClientRateLimiter(10);
            Assert.That(limiter.TryAcquire("a", 60, 2, Start, out _), Is.True);

            var later = Start.AddHours(1);
            Assert.That(limiter.TryAcquire("a", 60, 2, later, out _), Is.True);
            Assert.That(limiter.TryAcquire("a", 60, 2, later, out _), Is.True);
            Assert.That(limiter.TryAcquire("a", 60, 2, later, out _), Is.False);
        }

        [Test]
        public void ASlowRate_GivesALongRetryAfter()
        {
            var limiter = new ClientRateLimiter(10);
            Assert.That(limiter.TryAcquire("a", 1, 1, Start, out _), Is.True);

            Assert.That(limiter.TryAcquire("a", 1, 1, Start.AddSeconds(15), out var retryAfter), Is.False);
            Assert.That(retryAfter.TotalSeconds, Is.EqualTo(45).Within(0.001));
        }

        [Test]
        public void EveryKey_HasItsOwnBucket()
        {
            var limiter = new ClientRateLimiter(10);
            Assert.That(limiter.TryAcquire("a", 60, 1, Start, out _), Is.True);
            Assert.That(limiter.TryAcquire("a", 60, 1, Start, out _), Is.False);

            Assert.That(limiter.TryAcquire("b", 60, 1, Start, out _), Is.True);
        }

        [Test]
        public void TheStore_IsBounded_AndDropsTheLeastRecentlyUsedBucket()
        {
            var limiter = new ClientRateLimiter(2);
            Assert.That(limiter.TryAcquire("a", 60, 1, Start, out _), Is.True);
            Assert.That(limiter.TryAcquire("b", 60, 1, Start, out _), Is.True);
            Assert.That(limiter.TryAcquire("a", 60, 1, Start, out _), Is.False, "a is now the most recently used");

            Assert.That(limiter.TryAcquire("c", 60, 1, Start, out _), Is.True);

            Assert.That(limiter.Count, Is.EqualTo(2));
            Assert.That(limiter.TryAcquire("a", 60, 1, Start, out _), Is.False, "a was kept");
            Assert.That(limiter.TryAcquire("b", 60, 1, Start, out _), Is.True, "b was dropped and starts with a full bucket");
        }

        [Test]
        public void AClockGoingBackwards_DoesNotRefill()
        {
            var limiter = new ClientRateLimiter(10);
            Assert.That(limiter.TryAcquire("a", 60, 1, Start, out _), Is.True);

            Assert.That(limiter.TryAcquire("a", 60, 1, Start.AddMinutes(-5), out _), Is.False);
            Assert.That(limiter.TryAcquire("a", 60, 1, Start.AddSeconds(1), out _), Is.True);
        }
    }
}
