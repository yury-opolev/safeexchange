/// <summary>
/// TagValidator
/// </summary>

namespace SafeExchange.Core.Utilities
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;

    public static class TagValidator
    {
        public const int MaxTagLength = 64;

        public const int MaxTagsPerSecret = 16;

        private static readonly Regex Pattern =
            new(@"^[a-z0-9][a-z0-9:_-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static (bool ok, string normalised, string? error) TryNormalize(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return (false, string.Empty, "Tag is empty.");
            }

            var lowered = input.Trim().ToLowerInvariant();
            if (lowered.Length > MaxTagLength)
            {
                return (false, string.Empty, $"Tag exceeds {MaxTagLength} characters.");
            }

            if (!Pattern.IsMatch(lowered))
            {
                return (false, string.Empty, $"Tag '{input}' must match {Pattern}.");
            }

            return (true, lowered, null);
        }

        public static (bool ok, IReadOnlyList<string> normalised, string? error) TryNormalizeList(IEnumerable<string>? input)
        {
            var result = new List<string>();
            if (input is null)
            {
                return (true, result, null);
            }

            foreach (var raw in input)
            {
                var (ok, normalised, error) = TryNormalize(raw);
                if (!ok)
                {
                    return (false, Array.Empty<string>(), error);
                }

                if (!result.Contains(normalised))
                {
                    result.Add(normalised);
                }
            }

            if (result.Count > MaxTagsPerSecret)
            {
                return (false, Array.Empty<string>(),
                    $"Too many tags ({result.Count}); max {MaxTagsPerSecret}.");
            }

            return (true, result, null);
        }
    }
}
