
namespace SafeExchange.Core.Graph
{
    using SafeExchange.Core.Model.Dto.Output;
    using System;

    public class GraphUserInfo
    {
        public GraphUserInfo()
        {
        }

        public GraphUserInfo(string? displayName, string? userPrincipalName)
        {
            this.DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
            this.UserPrincipalName = userPrincipalName ?? throw new ArgumentNullException(nameof(userPrincipalName));
        }

        public string DisplayName { get; set; }

        public string UserPrincipalName { get; set; }

        internal GraphUserOutput ToDto() => new()
        {
            DisplayName = this.DisplayName,
            UserPrincipalName = this.UserPrincipalName
        };
    }
}
