/// <summary>
/// IssuedAtBackfill — pure helper for the TelemetryIdIssuedAt backfill migration (00012).
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Nodes;

    public static class IssuedAtBackfill
    {
        /// <summary>
        /// Returns the rewritten document JSON with <c>TelemetryIdIssuedAt</c> set to
        /// <c>telemetryIdExpiresAt - 7 days</c> (the start of the current calendar week),
        /// when the document has a non-empty <c>TelemetryId</c> and no usable
        /// <c>TelemetryIdIssuedAt</c>. Returns <c>null</c> when no change is needed
        /// (already set, no telemetry id) or the JSON is invalid.
        /// </summary>
        public static string? BackfillIfMissing(string documentJson, DateTime telemetryIdExpiresAt)
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

            var telemetryId = node["TelemetryId"];
            if (telemetryId is null
                || telemetryId.GetValueKind() == JsonValueKind.Null
                || string.IsNullOrEmpty(telemetryId.GetValue<string>()))
            {
                return null;
            }

            var issued = node["TelemetryIdIssuedAt"];
            if (issued is not null
                && issued.GetValueKind() != JsonValueKind.Null
                && DateTime.TryParse(issued.GetValue<string>(), out var existing)
                && existing != default)
            {
                return null;
            }

            node["TelemetryIdIssuedAt"] = telemetryIdExpiresAt.AddDays(-7).ToString("o");
            return node.ToJsonString();
        }
    }
}
