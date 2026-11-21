/// <summary>
/// TestDatabase
/// </summary>

namespace SafeExchange.Tests
{
    using System.Net;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos;
    using Microsoft.EntityFrameworkCore.Infrastructure;
    using NUnit.Framework;

    /// <summary>
    /// Drops a fixture's test database without failing the run when the Linux vNext
    /// Cosmos emulator answers the database DELETE with 500
    /// (PostgresError E58P01). The tests have already passed at that point and the
    /// emulator is disposable, so a failed cleanup is reported, not raised.
    /// </summary>
    public static class TestDatabase
    {
        public static void EnsureDeletedTolerant(DatabaseFacade database)
        {
            try
            {
                database.EnsureDeleted();
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.InternalServerError)
            {
                Report(exception);
            }
        }

        public static async Task EnsureDeletedTolerantAsync(DatabaseFacade database)
        {
            try
            {
                await database.EnsureDeletedAsync();
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.InternalServerError)
            {
                Report(exception);
            }
        }

        private static void Report(CosmosException exception)
            => TestContext.Progress.WriteLine($"Test database cleanup skipped: emulator returned {(int)exception.StatusCode} ({exception.ActivityId}).");
    }
}
