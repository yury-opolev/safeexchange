/// <summary>
/// ServiceOperation
/// </summary>

namespace SafeExchange.Core.Services
{
    using System;
    using System.Threading.Tasks;

    /// <summary>
    /// Runs the operation part of a service call and wraps any exception it throws in a
    /// <see cref="ServiceOperationException"/>.
    /// </summary>
    public static class ServiceOperation
    {
        public static async Task<TResult> RunAsync<TResult>(Func<Task<TResult>> operation)
        {
            if (operation is null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            try
            {
                return await operation();
            }
            catch (ServiceOperationException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new ServiceOperationException(exception);
            }
        }

        public static async Task RunAsync(Func<Task> operation)
        {
            if (operation is null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            try
            {
                await operation();
            }
            catch (ServiceOperationException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new ServiceOperationException(exception);
            }
        }
    }
}
