/// <summary>
/// DbUtilsTests
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.Azure.Cosmos;
    using Microsoft.EntityFrameworkCore;
    using NUnit.Framework;
    using SafeExchange.Core.Utilities;
    using SafeExchange.Tests.Utilities;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// Pure unit tests (no Cosmos emulator) for the shared add-or-get mitigation that the
    /// telemetry-id map insert and the user/group/pin inserts all rely on. Verifies that a
    /// benign Cosmos 409 (a concurrent writer created the same document) is swallowed and the
    /// get path is taken, while any other failure is propagated.
    /// </summary>
    [TestFixture]
    public class DbUtilsTests
    {
        private sealed class FakeEntity
        {
            public string Id { get; set; } = string.Empty;
        }

        private static DbUpdateException CosmosFailure(HttpStatusCode statusCode)
            => new DbUpdateException(
                "An error occurred while saving the entity changes.",
                new CosmosException("Resource with specified id or name already exists.", statusCode, 0, "test-activity-id", 1.0));

        [Test]
        public async Task TryAddOrGetEntityAsync_SwallowsCosmosConflict_AndReturnsGetResult()
        {
            // [GIVEN] An add that fails with a Cosmos 409 conflict and a get returning the existing entity.
            var existing = new FakeEntity { Id = "existing" };
            var conflict = CosmosFailure(HttpStatusCode.Conflict);

            // [WHEN] The add-or-get mitigation runs.
            var result = await DbUtils.TryAddOrGetEntityAsync(
                () => throw conflict,
                () => Task.FromResult(existing),
                TestFactory.CreateLogger());

            // [THEN] The conflict is swallowed and the entity from the get path is returned.
            Assert.That(result, Is.SameAs(existing));
        }

        [Test]
        public async Task TryAddOrGetEntityAsync_ReturnsAddedEntity_WhenNoConflict()
        {
            // [GIVEN] An add that succeeds.
            var added = new FakeEntity { Id = "added" };
            var getWasInvoked = false;

            // [WHEN] The add-or-get mitigation runs.
            var result = await DbUtils.TryAddOrGetEntityAsync(
                () => Task.FromResult(added),
                () =>
                {
                    getWasInvoked = true;
                    return Task.FromResult(new FakeEntity());
                },
                TestFactory.CreateLogger());

            // [THEN] The added entity is returned and the get path is never taken.
            Assert.That(result, Is.SameAs(added));
            Assert.That(getWasInvoked, Is.False);
        }

        [Test]
        public void TryAddOrGetEntityAsync_RethrowsNonConflictCosmosError()
        {
            // [GIVEN] An add that fails with a non-conflict Cosmos error (e.g. 500).
            var serverError = CosmosFailure(HttpStatusCode.InternalServerError);

            // [WHEN/THEN] The error is propagated, not swallowed.
            Assert.ThrowsAsync<DbUpdateException>(async () =>
                await DbUtils.TryAddOrGetEntityAsync<FakeEntity>(
                    () => throw serverError,
                    () => Task.FromResult(new FakeEntity()),
                    TestFactory.CreateLogger()));
        }

        [Test]
        public void TryAddOrGetEntityAsync_RethrowsDbUpdateException_WithoutCosmosInner()
        {
            // [GIVEN] A DbUpdateException whose inner exception is not a CosmosException.
            var nonCosmos = new DbUpdateException("plain update failure", new System.InvalidOperationException("inner"));

            // [WHEN/THEN] The error is propagated, not swallowed.
            Assert.ThrowsAsync<DbUpdateException>(async () =>
                await DbUtils.TryAddOrGetEntityAsync<FakeEntity>(
                    () => throw nonCosmos,
                    () => Task.FromResult(new FakeEntity()),
                    TestFactory.CreateLogger()));
        }
    }
}
