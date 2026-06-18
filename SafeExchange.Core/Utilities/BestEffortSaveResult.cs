/// <summary>
/// BestEffortSaveResult
/// </summary>

namespace SafeExchange.Core.Utilities
{
    /// <summary>Outcome of a best-effort, non-fatal persistence attempt
    /// (see <see cref="DbUtils.TrySaveBestEffortAsync"/>).</summary>
    public enum BestEffortSaveResult
    {
        /// <summary>The write was persisted.</summary>
        Saved,

        /// <summary>A Cosmos 409 Conflict was swallowed — a concurrent request had
        /// already written the same item, so the desired end state already holds.</summary>
        Conflict,

        /// <summary>An unexpected failure was logged and swallowed; the caller's
        /// primary operation was allowed to continue.</summary>
        Failed,
    }
}
