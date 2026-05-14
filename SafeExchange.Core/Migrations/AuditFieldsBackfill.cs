/// <summary>
/// AuditFieldsBackfill — pure helper for the AuditEnabled/AuditInstanceId backfill migration.
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using System.Text.Json;
    using System.Text.Json.Nodes;

    public static class AuditFieldsBackfill
    {
        /// <summary>
        /// Returns the rewritten document JSON if the input lacks an <c>AuditEnabled</c>
        /// field (or has it set to <c>null</c>), with <c>AuditEnabled = false</c> and
        /// <c>AuditInstanceId = null</c> appended. Returns <c>null</c> if the document
        /// already has a non-null <c>AuditEnabled</c> (no backfill needed).
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
            if (node is null)
            {
                return null;
            }

            var auditEnabledNode = node["AuditEnabled"];
            if (auditEnabledNode is not null && auditEnabledNode.GetValueKind() != JsonValueKind.Null)
            {
                return null;
            }

            node["AuditEnabled"] = false;
            node["AuditInstanceId"] = null;
            return node.ToJsonString();
        }
    }
}
