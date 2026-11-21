/// <summary>
/// ContractJson
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Encodings.Web;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Canonical form for REST contract comparisons: compact, wire property order kept,
    /// relaxed escaping so expected literals stay readable. Volatile values are replaced
    /// by placeholder tokens before comparison.
    /// </summary>
    public static class ContractJson
    {
        public const string CorrelationIdToken = "<correlationId>";

        private static readonly JsonSerializerOptions CanonicalOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private static readonly JsonSerializerOptions WireOptions = new();

        private static readonly Regex CorrelationIdPattern = new(
            "Internal error\\. Reference: [0-9a-f]{32}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static string Canonical(string json)
            => Parse(json).ToJsonString(CanonicalOptions);

        /// <summary>
        /// True when the text is exactly what System.Text.Json writes with default options
        /// (compact, default encoder) for its own tree, i.e. the canonical comparison does
        /// not hide whitespace, ordering or escaping differences on the wire.
        /// </summary>
        public static bool IsWireCompact(string json)
            => string.Equals(Parse(json).ToJsonString(WireOptions), json, StringComparison.Ordinal);

        /// <summary>
        /// Canonicalises <paramref name="json"/>, replaces every non-empty string value of a
        /// property named in <paramref name="maskedProperties"/> by <c>"&lt;name&gt;"</c>,
        /// replaces the TryCatch correlation id by <see cref="CorrelationIdToken"/>, then
        /// replaces each literal key of <paramref name="literals"/> by its token.
        /// </summary>
        public static string Normalize(string json, IEnumerable<string> maskedProperties, IReadOnlyDictionary<string, string> literals)
        {
            var node = Parse(json);
            var masked = new HashSet<string>(maskedProperties, StringComparer.Ordinal);
            if (masked.Count > 0)
            {
                Mask(node, masked);
            }

            var canonical = node.ToJsonString(CanonicalOptions);
            canonical = CorrelationIdPattern.Replace(canonical, $"Internal error. Reference: {CorrelationIdToken}");
            foreach (var literal in literals)
            {
                canonical = canonical.Replace(literal.Key, literal.Value, StringComparison.Ordinal);
            }

            return canonical;
        }

        private static JsonNode Parse(string json)
            => JsonNode.Parse(json) ?? throw new InvalidOperationException("Response body is the JSON literal null.");

        private static void Mask(JsonNode? node, HashSet<string> masked)
        {
            switch (node)
            {
                case JsonObject jsonObject:
                    foreach (var name in jsonObject.Select(p => p.Key).ToList())
                    {
                        var child = jsonObject[name];
                        if (masked.Contains(name)
                            && child is JsonValue value
                            && value.TryGetValue<string>(out var text)
                            && text.Length > 0)
                        {
                            jsonObject[name] = JsonValue.Create($"<{name}>");
                        }
                        else
                        {
                            Mask(child, masked);
                        }
                    }

                    break;

                case JsonArray jsonArray:
                    foreach (var item in jsonArray)
                    {
                        Mask(item, masked);
                    }

                    break;
            }
        }
    }
}
