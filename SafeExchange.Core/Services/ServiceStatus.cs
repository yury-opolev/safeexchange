/// <summary>
/// ServiceStatus
/// </summary>

namespace SafeExchange.Core.Services
{
    /// <summary>
    /// Outcome of a service call: one member per <c>status</c> string the REST handlers emit.
    /// <see cref="ServiceStatusNames.ToStatusString"/> gives the wire string.
    /// </summary>
    public enum ServiceStatus
    {
        Ok,
        NotFound,
        Forbidden,
        Unauthorized,
        Unprocessable,
        BadRequest,
        Conflict,
        Error,
        ChunkHashMismatch,
        HashMismatch,
        NoUploadState,
        InconsistentUploadMode,
    }
}
