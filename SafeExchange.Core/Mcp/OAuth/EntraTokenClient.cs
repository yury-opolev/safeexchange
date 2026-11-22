/// <summary>
/// EntraTokenClient
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using Microsoft.Extensions.Options;
    using SafeExchange.Core.Configuration;
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// <see cref="IEntraTokenClient"/> over the named <see cref="HttpClientName"/> client (10 s timeout, no redirects).
    /// The URL is always the token endpoint derived from <c>Authentication:Authority</c>. Nothing is logged here.
    /// </summary>
    public sealed class EntraTokenClient : IEntraTokenClient
    {
        public const string HttpClientName = "SafeExchange.McpOAuth.EntraToken";

        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private readonly IHttpClientFactory httpClientFactory;

        private readonly IOptionsMonitor<AuthenticationConfiguration> authentication;

        public EntraTokenClient(IHttpClientFactory httpClientFactory, IOptionsMonitor<AuthenticationConfiguration> authentication)
        {
            this.httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            this.authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        }

        public async Task<EntraTokenResponse> RedeemAsync(IReadOnlyList<KeyValuePair<string, string>> form, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(form);
            var endpoints = EntraEndpoints.FromAuthority(this.authentication.CurrentValue.Authority)
                ?? throw new InvalidOperationException("Authentication:Authority is not a plain https URL.");

            var client = this.httpClientFactory.CreateClient(HttpClientName);
            using var content = new FormUrlEncodedContent(form);
            using var response = await client.PostAsync(endpoints.Token, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return new EntraTokenResponse(response.StatusCode, body);
        }
    }
}
