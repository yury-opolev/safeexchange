
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
    }
}
