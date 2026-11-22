/// <summary>
/// McpProtectedResource
/// </summary>

namespace SafeExchange.Core.Mcp
{
    using Microsoft.Extensions.Options;
    using ModelContextProtocol.Authentication;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// The OAuth facts an MCP client needs (RFC 9728 protected resource metadata). <see cref="Resource"/> (the API's
    /// identifier for scopes) is the first <c>api://</c> entry of <c>ValidAudiences</c> (else the first entry) and
    /// <see cref="Scopes"/> is <c>McpScopes</c> or <c>{Resource}/user_impersonation</c>. The published document names
    /// the MCP URL as <c>resource</c> and, when the OAuth broker is configured, this host's broker as the
    /// authorization server; otherwise <c>Authority</c> ending in <c>/v2.0</c>.
    /// </summary>
    public sealed class McpProtectedResource
    {
        public const string DefaultScopeName = "user_impersonation";

        private const string AppUriScheme = "api://";

        private const string V2Suffix = "/v2.0";

        private readonly IOptionsMonitor<AuthenticationConfiguration> authentication;

        private readonly IOptionsMonitor<McpOAuthOptions> oauth;

        public McpProtectedResource(IOptionsMonitor<AuthenticationConfiguration> authentication, IOptionsMonitor<McpOAuthOptions> oauth)
        {
            this.authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
            this.oauth = oauth ?? throw new ArgumentNullException(nameof(oauth));
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

        /// <summary>
        /// True when <c>Mcp:OAuth:ClientId</c> is set and <c>Authentication:Authority</c> gives Entra endpoints.
        /// </summary>
        public bool IsBrokerConfigured
            => !string.IsNullOrWhiteSpace(this.oauth.CurrentValue.ClientId)
                && EntraEndpoints.FromAuthority(this.authentication.CurrentValue.Authority) is not null;

        /// <summary>
        /// The RFC 9728 document for the MCP endpoint under <paramref name="apiBaseUri"/> (<c>https://host/api/</c>).
        /// </summary>
        public ProtectedResourceMetadata ToMetadata(Uri apiBaseUri)
        {
            ArgumentNullException.ThrowIfNull(apiBaseUri);
            return new ProtectedResourceMetadata
            {
                Resource = McpOAuthPaths.Endpoint(apiBaseUri, McpOAuthPaths.Mcp),
                AuthorizationServers = this.IsBrokerConfigured
                    ? new List<string> { McpOAuthPaths.Issuer(apiBaseUri) }
                    : this.AuthorizationServers.ToList(),
                ScopesSupported = this.Scopes.ToList(),
                BearerMethodsSupported = new List<string> { "header" },
            };
        }

        private static IReadOnlyList<string> SplitList(string? value)
            => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
