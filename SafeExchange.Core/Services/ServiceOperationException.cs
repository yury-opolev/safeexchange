/// <summary>
/// ServiceOperationException
/// </summary>

namespace SafeExchange.Core.Services
{
    using System;

    /// <summary>
    /// Wraps an exception thrown by the operation part of a service call: everything after the steps
    /// REST runs outside its error handler today (purge-if-needed, and for access calls the secret lookup
    /// and permission checks). Hosts turn it into their internal-error response; exceptions thrown before
    /// the operation part are not wrapped and propagate unchanged.
    /// </summary>
    public sealed class ServiceOperationException : Exception
    {
        public ServiceOperationException(Exception innerException)
            : base("A service operation failed.", innerException ?? throw new ArgumentNullException(nameof(innerException)))
        {
        }
    }
}
