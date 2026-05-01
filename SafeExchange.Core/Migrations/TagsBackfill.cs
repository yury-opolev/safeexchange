/// <summary>
/// TagsBackfill — pure helper for the Tags-backfill migration.
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using System.Text.Json;
    using System.Text.Json.Nodes;

    public static class TagsBackfill
    {
        /// <summary>
        /// Returns the rewritten document JSON if the input lacks a <c>Tags</c> field
        /// (or has it set to <c>null</c>), with <c>Tags</c> set to an empty array.
        /// Returns <c>null</c> if the document already has a non-null <c>Tags</c>
        /// (i.e. nothing to backfill).
        /// </summary>
        /// <remarks>
        /// SafeExchange stores ObjectMetadata in Cosmos with PascalCase property names,
        /// so we look for <c>"Tags"</c> (not <c>"tags"</c>).
        /// </remarks>
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
            var tagsNode = node["Tags"];
            if (tagsNode is not null && tagsNode.GetValueKind() != JsonValueKind.Null)
            {
                return null;
            }
            node["Tags"] = new JsonArray();
            return node.ToJsonString();
        }
    }
}
