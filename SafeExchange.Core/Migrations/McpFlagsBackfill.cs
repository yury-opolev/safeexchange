/// <summary>
/// McpFlagsBackfill — pure helper for the McpAllowed / McpBlockedByAdmin backfill migration (00013).
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using System.Text.Json;
    using System.Text.Json.Nodes;

    public static class McpFlagsBackfill
    {
        private static readonly string[] FlagNames = { "McpAllowed", "McpBlockedByAdmin" };

        /// <summary>
        /// Returns the rewritten user document JSON with <c>McpAllowed = false</c> and/or
        /// <c>McpBlockedByAdmin = false</c> added where the field is missing or <c>null</c>.
        /// Existing values (including <c>true</c>) are never changed. Returns <c>null</c> when
        /// both fields are already present or the JSON is not an object.
        /// </summary>
        public static string? BackfillIfMissing(string documentJson)
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(documentJson);
            }
            catch (JsonException)
            {
                return null;
            }

            if (node is not JsonObject document)
            {
                return null;
            }

            var changed = false;
            foreach (var name in FlagNames)
            {
                // A JSON null and a missing property both read as a null node.
                if (document[name] is null)
                {
                    document[name] = false;
                    changed = true;
                }
            }

            return changed ? document.ToJsonString() : null;
        }
    }
}
