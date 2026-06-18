namespace SafeExchange.Tests
{
    using Microsoft.Azure.Cosmos;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging.Abstractions;
    using NUnit.Framework;
    using SafeExchange.Core.Utilities;
    using System;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// Covers <see cref="DbUtils.TrySaveBestEffortAsync"/>, the conflict-swallowing,
    /// best-effort persistence used for non-critical side-effect writes (e.g. the
    /// rotating telemetry id bookkeeping in the auth middleware) which must never
    /// fail the caller's primary request.
    /// </summary>
    [TestFixture]
    public class DbUtilsBestEffortSaveTests
    {
        [Test]
        public async Task TrySaveBestEffortAsync_SaveSucceeds_ReturnsSaved()
        {
            var result = await DbUtils.TrySaveBestEffortAsync(
                () => Task.CompletedTask,
                NullLogger.Instance,
                "test save");

            Assert.That(result, Is.EqualTo(BestEffortSaveResult.Saved));
        }

        [Test]
        public async Task TrySaveBestEffortAsync_CosmosConflict_IsSwallowedAndReportsConflict()
        {
            // Reproduces the production race: two concurrent requests both insert the
            // same retired telemetry id, the loser gets a Cosmos 409 wrapped in a
            // DbUpdateException. It must NOT propagate (the row already exists).
            var conflict = new DbUpdateException(
                "Conflicts were detected for item.",
                new CosmosException("Resource with specified id or name already exists.", HttpStatusCode.Conflict, 0, "activity-id", 0));

            var result = await DbUtils.TrySaveBestEffortAsync(
                () => throw conflict,
                NullLogger.Instance,
                "persisting rotated telemetry id");

            Assert.That(result, Is.EqualTo(BestEffortSaveResult.Conflict));
        }

        [Test]
        public async Task TrySaveBestEffortAsync_UnexpectedException_IsSwallowedAndReportsFailed()
        {
            // Best-effort: any other failure persisting telemetry bookkeeping must be
            // logged and swallowed, never surfaced as a 500 to the user's real request.
            var result = await DbUtils.TrySaveBestEffortAsync(
                () => throw new InvalidOperationException("transient db failure"),
                NullLogger.Instance,
                "persisting rotated telemetry id");

            Assert.That(result, Is.EqualTo(BestEffortSaveResult.Failed));
        }

        [Test]
        public async Task TrySaveBestEffortAsync_NonConflictDbUpdateException_IsSwallowedAndReportsFailed()
        {
            // A DbUpdateException that is not a 409 (e.g. throttling/unavailable) is
            // still non-fatal for telemetry bookkeeping and must be swallowed.
            var nonConflict = new DbUpdateException(
                "Service unavailable.",
                new CosmosException("Service is currently unavailable.", HttpStatusCode.ServiceUnavailable, 0, "activity-id", 0));

            var result = await DbUtils.TrySaveBestEffortAsync(
                () => throw nonConflict,
                NullLogger.Instance,
                "persisting rotated telemetry id");

            Assert.That(result, Is.EqualTo(BestEffortSaveResult.Failed));
        }
    }
}
