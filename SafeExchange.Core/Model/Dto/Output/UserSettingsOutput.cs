/// <summary>
/// UserSettingsOutput — result of GET and PATCH /me/settings.
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    public class UserSettingsOutput
    {
        public bool McpAllowed { get; set; }

        public bool McpBlockedByAdmin { get; set; }

        /// <summary>Whether MCP calls of the user are served: allowed and not blocked.</summary>
        public bool McpEffective { get; set; }

        public static UserSettingsOutput From(User user) => new()
        {
            McpAllowed = user.McpAllowed,
            McpBlockedByAdmin = user.McpBlockedByAdmin,
            McpEffective = user.McpAllowed && !user.McpBlockedByAdmin,
        };
    }
}
