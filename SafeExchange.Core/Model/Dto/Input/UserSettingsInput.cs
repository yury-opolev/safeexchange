/// <summary>
/// UserSettingsInput — body of PATCH /me/settings.
/// </summary>

namespace SafeExchange.Core.Model.Dto.Input
{
    public class UserSettingsInput
    {
        /// <summary>Required; null when the body omits it or sets it to null.</summary>
        public bool? McpAllowed { get; set; }
    }
}
