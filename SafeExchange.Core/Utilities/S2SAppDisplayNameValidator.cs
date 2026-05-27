namespace SafeExchange.Core.Utilities
{
    using System.Text.RegularExpressions;

    /// <summary>
    /// Validates the display name a user submits when self-registering an S2S app.
    /// Must start with an English letter and contain only English letters, digits,
    /// dash, or underscore; length 3..64.
    /// </summary>
    public static class S2SAppDisplayNameValidator
    {
        public const int MinLength = 3;
        public const int MaxLength = 64;

        private static readonly Regex Pattern = new(
            @"^[A-Za-z][A-Za-z0-9_-]*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static bool TryValidate(string? displayName, out string? reason)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                reason = "Display name is required.";
                return false;
            }

            if (displayName.Length < MinLength)
            {
                reason = $"Display name must be at least {MinLength} characters.";
                return false;
            }

            if (displayName.Length > MaxLength)
            {
                reason = $"Display name must be at most {MaxLength} characters.";
                return false;
            }

            if (!Pattern.IsMatch(displayName))
            {
                reason = "Display name must start with an English letter and contain only English letters, digits, '-' or '_'.";
                return false;
            }

            reason = null;
            return true;
        }
    }
}
