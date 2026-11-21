/// <summary>
/// ChunkUploadRequest
/// </summary>

namespace SafeExchange.Core.Services
{
    /// <summary>
    /// Everything about a chunk upload other than its bytes. REST fills it from the route and the
    /// <c>X-SafeExchange-*</c> / <c>Content-Length</c> headers.
    /// </summary>
    public sealed class ChunkUploadRequest
    {
        /// <summary>
        /// Must be empty: the server names chunks. REST passes its route value.
        /// </summary>
        public string ChunkId { get; init; } = string.Empty;

        /// <summary>
        /// The upload lock returned by the first chunk; empty for the first chunk.
        /// </summary>
        public string AccessTicket { get; init; } = string.Empty;

        /// <summary>
        /// <c>interim</c> keeps a legacy (unhashed) upload open after this chunk; anything else finishes it.
        /// </summary>
        public string OperationType { get; init; } = string.Empty;

        /// <summary>
        /// The client's SHA-256 (hex) of this chunk; selects the hashed upload mode for attachments.
        /// </summary>
        public string? ChunkHash { get; init; }

        /// <summary>
        /// The chunk length to record for hashed uploads (REST: the <c>Content-Length</c> header, 0 if absent).
        /// When <c>null</c>, the number of bytes actually read is recorded.
        /// </summary>
        public long? DeclaredLength { get; init; }

        /// <summary>
        /// Optional resume guard (MCP). When set: a chunk already stored at this index with the same
        /// <see cref="ChunkHash"/> is answered as a successful no-op; otherwise the content's chunk count must
        /// equal it. REST leaves it <c>null</c>.
        /// </summary>
        public int? ExpectedIndex { get; init; }
    }
}
