/// <summary>
/// EmailValidator
/// </summary>

namespace SafeExchange.Core.Utilities
{
    using System.Text.RegularExpressions;

    /// <summary>
    /// The one email-like format rule for contact addresses and group mail.
    /// Four functions carried a copy of this regex, which is how they drifted into
    /// rejecting real addresses; they all call in here now.
    /// The Blazor client mirrors the same rule for its access list subjects
    /// (SafeExchange.Client.Common.Utilities.SubjectNameValidator).
    /// </summary>
    public static class EmailValidator
    {
        public const int MaxLength = 320;

        /// <summary>
        /// The local part carries what a user principal name can carry, '+' included.
        /// The last label is not length capped: the previous {2,4} rejected every address
        /// on a long top level domain, '.online' and '.consulting' among them.
        /// Ends with '\z', not '$'. In .NET '$' also matches immediately before a trailing
        /// newline, so "user@example.com\n" passed, and these values reach log messages.
        /// </summary>
        public const string Pattern = @"^[\w.+-]+@([\w-]+\.)+[\w-]{2,}\z";

        private static readonly Regex EmailRegex = new(
            Pattern,
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Shape only, for callers that report length and format separately.
        /// </summary>
        public static bool HasValidFormat(string? email)
            => !string.IsNullOrEmpty(email) && EmailRegex.IsMatch(email);

        /// <summary>
        /// The whole rule: present, within the length limit, and email-like.
        /// </summary>
        public static bool IsValid(string? email)
            => !string.IsNullOrEmpty(email) && email.Length <= MaxLength && EmailRegex.IsMatch(email);
    }
}
