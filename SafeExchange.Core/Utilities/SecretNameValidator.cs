/// <summary>
/// SecretNameValidator
/// </summary>

namespace SafeExchange.Core.Utilities
{
    using System.Text.RegularExpressions;

    /// <summary>
    /// The authoritative naming contract for newly created secrets.
    /// Applied on the creation path only: a secret name is immutable, so an identifier
    /// accepted under earlier behavior must stay readable, updatable and deletable.
    /// The Blazor client mirrors this rule (SafeExchange.Client.Common.Utilities.SecretNameValidator)
    /// for immediate feedback; the API is what actually enforces it, for every client.
    /// </summary>
    public static class SecretNameValidator
    {
        public const int MaxLength = 100;

        private static readonly Regex Pattern = new(
            @"^[0-9a-zA-Z_-]+\z",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static bool TryValidate(string? secretName, out string? reason)
        {
            if (string.IsNullOrEmpty(secretName))
            {
                reason = "Secret id value is not provided.";
                return false;
            }

            if (secretName.Length > MaxLength)
            {
                reason = $"Secret id must be at most {MaxLength} characters.";
                return false;
            }

            if (!Pattern.IsMatch(secretName))
            {
                reason = "Secret id must contain only English letters, digits, '-' or '_'.";
                return false;
            }

            reason = null;
            return true;
        }
    }
}
