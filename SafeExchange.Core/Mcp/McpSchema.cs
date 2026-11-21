/// <summary>
/// McpSchema
/// </summary>

namespace SafeExchange.Core.Mcp
{
    using System.Text.Json;

    /// <summary>
    /// Tool input schemas: parsing, and the fragments several tools share (the REST input DTOs).
    /// </summary>
    public static class McpSchema
    {
        public const string SecretName = """
            {"type":"string","description":"The secret name (letters, digits and hyphens)."}
            """;

        public const string ExpirationSettings = """
            {"type":"object","description":"When the secret expires (same fields and defaults as the REST API).","properties":{"scheduleExpiration":{"type":"boolean","description":"Delete the secret at expireAt.","default":false},"expireAt":{"type":"string","format":"date-time","description":"UTC date-time used when scheduleExpiration is true."},"expireOnIdleTime":{"type":"boolean","description":"Delete the secret when it is not accessed for idleTimeToExpire.","default":false},"idleTimeToExpire":{"type":"string","description":"Idle period as d.hh:mm:ss, e.g. 30.00:00:00.","default":"00:00:00"}}}
            """;

        public const string Tags = """
            {"type":"array","items":{"type":"string"},"description":"Up to 16 tags of lowercase letters, digits, ':', '_' and '-'."}
            """;

        public const string SubjectPermissions = """
            {"type":"array","items":{"type":"object","properties":{"subjectType":{"type":"integer","enum":[0,1,100],"description":"0 = user, 1 = group, 100 = application.","default":0},"subjectName":{"type":"string","description":"User principal name, group display name or mail, or application display name."},"subjectId":{"type":"string","description":"Group object id (GUID); users and applications are identified by subjectName."},"canRead":{"type":"boolean","default":false},"canWrite":{"type":"boolean","default":false},"canGrantAccess":{"type":"boolean","default":false},"canRevokeAccess":{"type":"boolean","default":false}},"required":["subjectName"]}}
            """;

        /// <summary>
        /// Parses a tool's JSON Schema once; the returned element owns its data.
        /// </summary>
        public static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }
}
