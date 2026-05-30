/// <summary>
/// TelemetryIdBackfill — pure helper for the TelemetryId / TelemetryIdExpiresAt backfill migration.
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Nodes;

    public static class TelemetryIdBackfill
    {
        /// <summary>
        /// Returns the rewritten document JSON with <c>TelemetryId</c> and
        /// <c>TelemetryIdExpiresAt</c> set to the supplied values, when the input
        /// document has a missing or empty <c>TelemetryId</c>. Returns <c>null</c>
        /// when the document already has a non-empty <c>TelemetryId</c> (no backfill needed),
        /// or when the JSON is invalid.
        /// </summary>
        public static string? BackfillIfMissing(string documentJson, string telemetryId, DateTime telemetryIdExpiresAt)
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

            if (node is null)
            {
                return null;
            }

            var existing = node["TelemetryId"];
            if (existing is not null
                && existing.GetValueKind() != JsonValueKind.Null
                && !string.IsNullOrEmpty(existing.GetValue<string>()))
            {
                return null;
            }

            node["TelemetryId"] = telemetryId;
            node["TelemetryIdExpiresAt"] = telemetryIdExpiresAt.ToString("o");
            return node.ToJsonString();
        }
    }
}
