/// <summary>
/// McpProtectedResource
/// </summary>

namespace SafeExchange.Core.Mcp
{
    using Microsoft.Extensions.Options;
    using ModelContextProtocol.Authentication;
    using SafeExchange.Core.Configuration;
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// The OAuth facts an MCP client needs, derived from the <c>Authentication</c> section (RFC 9728 protected
    /// resource metadata): <c>resource</c> is the first <c>api://</c> entry of <c>ValidAudiences</c> (else the
    /// first entry), <c>authorization_servers</c> is <c>Authority</c> ending in <c>/v2.0</c>, and
    /// <c>scopes_supported</c> is <c>McpScopes</c> or <c>{resource}/user_impersonation</c>.
    /// </summary>
    public sealed class McpProtectedResource
    {
        public const string DefaultScopeName = "user_impersonation";

        private const string AppUriScheme = "api://";

        private const string V2Suffix = "/v2.0";

        private readonly IOptionsMonitor<AuthenticationConfiguration> authentication;

        public McpProtectedResource(IOptionsMonitor<AuthenticationConfiguration> authentication)
        {
            this.authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        }

        public string Resource
        {
            get
            {
                var audiences = SplitList(this.authentication.CurrentValue.ValidAudiences);
                return audiences.FirstOrDefault(a => a.StartsWith(AppUriScheme, StringComparison.OrdinalIgnoreCase))
                    ?? audiences.FirstOrDefault()
                    ?? string.Empty;
            }
        }

        /// <summary>
        /// Delegated (user) scopes to request.
        /// </summary>
        public IReadOnlyList<string> Scopes
        {
            get
            {
                var configured = SplitList(this.authentication.CurrentValue.McpScopes);
                if (configured.Count > 0)
                {
                    return configured;
                }

                var resource = this.Resource;
                return resource.Length == 0 ? Array.Empty<string>() : new[] { $"{resource.TrimEnd('/')}/{DefaultScopeName}" };
            }
        }

        /// <summary>
        /// The client-credentials scope for an S2S application (<c>{resource}/.default</c>).
        /// </summary>
        public string ApplicationScope
        {
            get
            {
                var resource = this.Resource;
                return resource.Length == 0 ? string.Empty : $"{resource.TrimEnd('/')}/.default";
            }
        }

        public IReadOnlyList<string> AuthorizationServers
        {
            get
            {
                var authority = (this.authentication.CurrentValue.Authority ?? string.Empty).Trim().TrimEnd('/');
                if (authority.Length == 0)
                {
                    return Array.Empty<string>();
                }

                return new[] { authority.EndsWith(V2Suffix, StringComparison.OrdinalIgnoreCase) ? authority : authority + V2Suffix };
            }
        }

        public ProtectedResourceMetadata ToMetadata() => new()
        {
            Resource = this.Resource,
            AuthorizationServers = this.AuthorizationServers.ToList(),
            ScopesSupported = this.Scopes.ToList(),
            BearerMethodsSupported = new List<string> { "header" },
        };

        private static IReadOnlyList<string> SplitList(string? value)
            => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
