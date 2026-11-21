/// <summary>
/// ContentCommitInput
/// </summary>

namespace SafeExchange.Core.Services
{
    /// <summary>
    /// The client's whole-content hash for <see cref="ContentService.CommitAsync"/>.
    /// <see cref="Malformed"/> stands for a request body the host could not parse, which the service
    /// answers in its place in the check order (after the lock and upload-state checks).
    /// </summary>
    public sealed record ContentCommitInput(string? Hash)
    {
        public static ContentCommitInput Malformed { get; } = new((string?)null) { IsMalformed = true };

        public bool IsMalformed { get; private init; }
    }
}
