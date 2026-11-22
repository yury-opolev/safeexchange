/// <summary>
/// OAuthParameters
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;

    /// <summary>
    /// Parses an OAuth query string or <c>application/x-www-form-urlencoded</c> body. Parameters without a value are
    /// treated as omitted and a repeated parameter fails the whole request (RFC 6749 §3.1, §3.2).
    /// </summary>
    public static class OAuthParameters
    {
        public static bool TryParse(string? encoded, [NotNullWhen(true)] out IReadOnlyDictionary<string, string>? parameters)
        {
            var text = encoded ?? string.Empty;
            if (text.StartsWith('?'))
            {
                text = text[1..];
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                var name = Decode(separator < 0 ? pair : pair[..separator]);
                var value = separator < 0 ? string.Empty : Decode(pair[(separator + 1)..]);
                if (value.Length == 0)
                {
                    continue;
                }

                if (!values.TryAdd(name, value))
                {
                    parameters = null;
                    return false;
                }
            }

            parameters = values;
            return true;
        }

        private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
    }
}
