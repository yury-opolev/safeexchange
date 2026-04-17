/// <summary>
/// SearchStringValidator
/// </summary>

namespace SafeExchange.Core.Utilities
{
    using System;

    /// <summary>
    /// Validates free-form search strings before they are interpolated into a
    /// Microsoft Graph <c>$search</c> query parameter.
    ///
    /// Graph <c>$search</c> uses double-quoted <c>"field:value"</c> tokens joined
    /// by <c>AND</c> / <c>OR</c>. A user-controlled value embedded naively into the
    /// template — as this codebase originally did in
    /// <see cref="SafeExchange.Core.Graph.GraphDataProvider.TryFindUsersAsync"/>
    /// and <see cref="SafeExchange.Core.Graph.GraphDataProvider.TryFindGroupsAsync"/>
    /// — lets the caller break out of the enclosing quotes by submitting
    /// <c>foo" OR "accountEnabled:true</c> and enumerate fields or records the
    /// handler never intended to expose. That is OWASP A05:2025 / CWE-943
    /// (Improper Neutralization of Special Elements in Data Query Logic).
    ///
    /// This validator is intentionally minimal and allowlist-based: it rejects
    /// anything with quotes, control characters, or beyond a small length bound,
    /// and leaves everything else to Graph itself (which handles whitespace,
    /// wildcards, and unicode display-name characters correctly). It is called
    /// both from the handler (to return a clear 400) and from the provider (so
    /// every future caller gets the guard for free).
    /// </summary>
    public static class SearchStringValidator
    {
        /// <summary>
        /// Maximum accepted length. Microsoft Graph does not document a hard limit
        /// on <c>$search</c> terms; 64 chars is plenty for a display-name / UPN /
        /// mail prefix search and keeps the parameter well below reasonable URL
        /// and log-line budgets.
        /// </summary>
        public const int MaxLength = 64;

        /// <summary>
        /// Returns <c>true</c> when the supplied string is safe to embed inside a
        /// quoted Graph <c>$search</c> token; otherwise populates
        /// <paramref name="reason"/> with a short operator-friendly description of
        /// the failure.
        /// </summary>
        public static bool TryValidate(string? searchString, out string? reason)
        {
            if (string.IsNullOrWhiteSpace(searchString))
            {
                reason = "searchString is empty.";
                return false;
            }

            if (searchString.Length > MaxLength)
            {
                reason = $"searchString is too long (max {MaxLength} characters).";
                return false;
            }

            foreach (var c in searchString)
            {
                if (c == '"' || c == '\\' || c == '\r' || c == '\n' || char.IsControl(c))
                {
                    reason = "searchString contains invalid characters.";
                    return false;
                }
            }

            reason = null;
            return true;
        }
    }
}
