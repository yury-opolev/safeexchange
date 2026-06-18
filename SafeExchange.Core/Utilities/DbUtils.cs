
namespace SafeExchange.Core.Utilities
{
    using System;
    using System.Net;
    using Microsoft.Azure.Cosmos;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;

    public static class DbUtils
    {
        public static async Task<T> TryAddOrGetEntityAsync<T>(Func<Task<T>> addAsync, Func<Task<T>> getAsync, ILogger log)
        {
            try
            {
                return await addAsync();
            }
            catch (DbUpdateException dbUpdateException)
                when (dbUpdateException.InnerException is CosmosException cosmosException &&
                      cosmosException.StatusCode == HttpStatusCode.Conflict)
            {
                log.LogInformation($"Conflict '{cosmosException.Message}', entity of type '{typeof(T)}' was created in a different process.");
                return await getAsync();
            }
        }

        /// <summary>
        /// Runs a non-critical, side-effect persistence (e.g. telemetry bookkeeping)
        /// that must never fail the caller's primary operation. A Cosmos 409 Conflict
        /// is swallowed as success-by-idempotency (a concurrent request already wrote
        /// the same item); any other failure is logged and swallowed.
        /// </summary>
        public static async Task<BestEffortSaveResult> TrySaveBestEffortAsync(Func<Task> saveAsync, ILogger log, string description)
        {
            try
            {
                await saveAsync();
                return BestEffortSaveResult.Saved;
            }
            catch (DbUpdateException dbUpdateException)
                when (dbUpdateException.InnerException is CosmosException cosmosException &&
                      cosmosException.StatusCode == HttpStatusCode.Conflict)
            {
                log.LogInformation("Conflict while {Description}; already written by a concurrent request.", description);
                return BestEffortSaveResult.Conflict;
            }
            catch (Exception exception)
            {
                log.LogWarning(exception, "Best-effort operation '{Description}' failed; continuing without failing the request.", description);
                return BestEffortSaveResult.Failed;
            }
        }
    }
}
