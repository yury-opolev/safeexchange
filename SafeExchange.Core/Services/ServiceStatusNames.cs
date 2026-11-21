/// <summary>
/// ServiceStatusNames
/// </summary>

namespace SafeExchange.Core.Services
{
    using System;

    public static class ServiceStatusNames
    {
        /// <summary>
        /// The <c>status</c> string REST writes (and MCP reports) for <paramref name="status"/>.
        /// </summary>
        public static string ToStatusString(this ServiceStatus status) => status switch
        {
            ServiceStatus.Ok => "ok",
            ServiceStatus.NotFound => "not_found",
            ServiceStatus.Forbidden => "forbidden",
            ServiceStatus.Unauthorized => "unauthorized",
            ServiceStatus.Unprocessable => "unprocessable",
            ServiceStatus.BadRequest => "bad_request",
            ServiceStatus.Conflict => "conflict",
            ServiceStatus.Error => "error",
            ServiceStatus.ChunkHashMismatch => "chunk_hash_mismatch",
            ServiceStatus.HashMismatch => "hash_mismatch",
            ServiceStatus.NoUploadState => "no_upload_state",
            ServiceStatus.InconsistentUploadMode => "inconsistent_upload_mode",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown service status."),
        };
    }
}
