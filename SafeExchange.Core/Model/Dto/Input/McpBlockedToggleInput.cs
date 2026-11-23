/// <summary>
/// McpBlockedToggleInput — body of PATCH /admin/users/{upn}/mcp-blocked.
/// </summary>

namespace SafeExchange.Core.Model.Dto.Input
{
    public class McpBlockedToggleInput
    {
        /// <summary>Required; null when the body omits it or sets it to null.</summary>
        public bool? Blocked { get; set; }
    }
}
