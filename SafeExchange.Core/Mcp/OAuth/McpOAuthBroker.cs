/// <summary>
/// McpOAuthBroker
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core.Configuration;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A standards-shaped OAuth authorization server for MCP clients that delegates every decision to Entra ID:
    /// registration hands out the configured Entra public client, authorize and callback relay the code flow through
    /// this host's callback with a wrapped state, and token forwards to Entra's fixed token endpoint. It never issues,
    /// stores or inspects tokens, and never logs codes, verifiers, tokens or state.
    /// </summary>
    public sealed class McpOAuthBroker
    {
        public const int MaxBodyLength = 16 * 1024;

        public const string OfflineAccessScope = "offline_access";

        private const int MaxRedirectUris = 10;

        private static readonly string[] SupportedGrantTypes = { "authorization_code", "refresh_token" };

        private static readonly string[] SupportedResponseTypes = { "code" };

        private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false };

        private readonly McpProtectedResource protectedResource;

        private readonly IOptionsMonitor<McpOAuthOptions> oauth;

        private readonly IOptionsMonitor<AuthenticationConfiguration> authentication;

        private readonly IOptionsMonitor<Features> features;

        private readonly IEntraTokenClient tokenClient;

        private readonly ILogger log;

        public McpOAuthBroker(
            McpProtectedResource protectedResource,
            IOptionsMonitor<McpOAuthOptions> oauth,
            IOptionsMonitor<AuthenticationConfiguration> authentication,
            IOptionsMonitor<Features> features,
            IEntraTokenClient tokenClient,
            ILogger<McpOAuthBroker> log)
        {
            this.protectedResource = protectedResource ?? throw new ArgumentNullException(nameof(protectedResource));
            this.oauth = oauth ?? throw new ArgumentNullException(nameof(oauth));
            this.authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
            this.features = features ?? throw new ArgumentNullException(nameof(features));
            this.tokenClient = tokenClient ?? throw new ArgumentNullException(nameof(tokenClient));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// False (every endpoint answers 404) unless <c>Features.McpEndpoint</c> is on and the broker is configured.
        /// </summary>
        public bool IsEnabled => this.features.CurrentValue.McpEndpoint && this.protectedResource.IsBrokerConfigured;

        public McpOAuthResult Metadata(Uri apiBaseUri)
        {
            ArgumentNullException.ThrowIfNull(apiBaseUri);
            if (!this.IsEnabled)
            {
                return McpOAuthResult.NotFound;
            }

            return McpOAuthResult.Json(HttpStatusCode.OK, AuthorizationServerMetadata.ToJson(apiBaseUri, this.Scopes));
        }

        /// <summary>
        /// RFC 7591 registration: every client gets the configured Entra public client ID; only loopback redirect
        /// URIs, the code flow and <c>token_endpoint_auth_method: none</c> are accepted.
        /// </summary>
        public McpOAuthResult Register(string? body)
        {
            if (!this.IsEnabled)
            {
                return McpOAuthResult.NotFound;
            }

            if (string.IsNullOrEmpty(body) || body.Length > MaxBodyLength || !TryParseObject(body, out var request))
            {
                return this.Refuse("register", "invalid_client_metadata", "The registration request must be a JSON object of at most 16 KB.");
            }

            if (!TryGetStrings(request, "redirect_uris", out var redirectUris)
                || redirectUris.Count == 0
                || redirectUris.Count > MaxRedirectUris
                || !redirectUris.All(uri => LoopbackRedirectUri.IsValid(uri)))
            {
                return this.Refuse("register", "invalid_redirect_uri", "redirect_uris must list 1 to 10 loopback http URIs (127.0.0.1, localhost or [::1]) without fragment or user info.");
            }

            if (request["token_endpoint_auth_method"] is JsonNode method && !(method is JsonValue value && value.TryGetValue<string>(out var name) && name == "none"))
            {
                return this.Refuse("register", "invalid_client_metadata", "Only public clients (token_endpoint_auth_method none) can register.");
            }

            if (!IsAbsentOrSubset(request, "grant_types", SupportedGrantTypes))
            {
                return this.Refuse("register", "invalid_client_metadata", "grant_types may only contain authorization_code and refresh_token.");
            }

            if (!IsAbsentOrSubset(request, "response_types", SupportedResponseTypes))
            {
                return this.Refuse("register", "invalid_client_metadata", "response_types may only contain code.");
            }

            this.log.LogInformation("MCP OAuth register answered 201 for {RedirectUriCount} redirect URI(s).", redirectUris.Count);
            return McpOAuthResult.Json(HttpStatusCode.Created, new JsonObject
            {
                ["client_id"] = this.ClientId,
                ["redirect_uris"] = new JsonArray(redirectUris.Select(uri => (JsonNode?)uri).ToArray()),
                ["grant_types"] = new JsonArray(SupportedGrantTypes.Select(grant => (JsonNode?)grant).ToArray()),
                ["response_types"] = new JsonArray(SupportedResponseTypes.Select(type => (JsonNode?)type).ToArray()),
                ["token_endpoint_auth_method"] = "none",
            }.ToJsonString());
        }

        /// <summary>
        /// Validates an authorization request and redirects to Entra with the broker's client ID, callback and scope,
        /// the client's S256 challenge and the wrapped state. An unknown client or a redirect URI that is not
        /// loopback is answered with 400 and never redirected; other errors go back to the client's redirect URI.
        /// </summary>
        public McpOAuthResult Authorize(Uri apiBaseUri, string? query)
        {
            ArgumentNullException.ThrowIfNull(apiBaseUri);
            var endpoints = this.EnabledEndpoints;
            if (endpoints is null)
            {
                return McpOAuthResult.NotFound;
            }

            if (!OAuthParameters.TryParse(query, out var parameters))
            {
                return this.Refuse("authorize", "invalid_request", "A parameter was repeated.");
            }

            if (!string.Equals(Get(parameters, "client_id"), this.ClientId, StringComparison.Ordinal))
            {
                return this.Refuse("authorize", "invalid_client", "Unknown client_id; register at the registration endpoint first.");
            }

            var redirectUri = Get(parameters, "redirect_uri");
            if (!LoopbackRedirectUri.IsValid(redirectUri))
            {
                return this.Refuse("authorize", "invalid_request", "redirect_uri must be a loopback http URI (127.0.0.1, localhost or [::1]) without fragment or user info.");
            }

            var clientState = Get(parameters, "state");
            if (Get(parameters, "response_type") != "code")
            {
                return this.RedirectError("authorize", redirectUri, clientState, "unsupported_response_type", "Only response_type=code is supported.");
            }

            var challenge = Get(parameters, "code_challenge");
            if (!IsPkceValue(challenge) || Get(parameters, "code_challenge_method") != "S256")
            {
                return this.RedirectError("authorize", redirectUri, clientState, "invalid_request", "PKCE with code_challenge_method=S256 is required.");
            }

            if (!new BrokerState(redirectUri, clientState).TryEncode(out var wrappedState))
            {
                return this.RedirectError("authorize", redirectUri, clientState, "invalid_request", "state is too long.");
            }

            var entraParameters = new List<KeyValuePair<string, string?>>
            {
                new("client_id", this.ClientId),
                new("response_type", "code"),
                new("redirect_uri", McpOAuthPaths.Endpoint(apiBaseUri, McpOAuthPaths.Callback)),
                new("response_mode", "query"),
                new("scope", string.Join(' ', this.Scopes)),
                new("code_challenge", challenge),
                new("code_challenge_method", "S256"),
                new("state", wrappedState),
                new("prompt", Get(parameters, "prompt")),
                new("login_hint", Get(parameters, "login_hint")),
            };

            this.log.LogInformation("MCP OAuth authorize redirected to Entra.");
            return McpOAuthResult.Redirect(AppendQuery(endpoints.Authorize, entraParameters));
        }

        /// <summary>
        /// Entra's redirect target: decodes and re-validates the wrapped state, then forwards <c>code</c> and the
        /// client's <c>state</c> (or only <c>error</c>, <c>error_description</c> and <c>state</c>) to the loopback
        /// redirect. Without a valid state there is nowhere safe to go, so the answer is 400.
        /// </summary>
        public McpOAuthResult Callback(string? query)
        {
            if (!this.IsEnabled)
            {
                return McpOAuthResult.NotFound;
            }

            if (!OAuthParameters.TryParse(query, out var parameters) || !BrokerState.TryDecode(Get(parameters, "state"), out var state))
            {
                return this.Refuse("callback", "invalid_request", "The authorization response has no valid state.");
            }

            var error = Get(parameters, "error");
            if (error is not null)
            {
                this.log.LogInformation("MCP OAuth callback forwarded Entra error {EntraError} to the client.", LogSafeErrorCode(error));
                return McpOAuthResult.Redirect(AppendQuery(state.RedirectUri, new KeyValuePair<string, string?>[]
                {
                    new("error", error),
                    new("error_description", Get(parameters, "error_description")),
                    new("state", state.ClientState),
                }));
            }

            var code = Get(parameters, "code");
            if (code is null)
            {
                return this.RedirectError("callback", state.RedirectUri, state.ClientState, "server_error", "The identity provider returned no authorization code.");
            }

            this.log.LogInformation("MCP OAuth callback forwarded the authorization code to the client.");
            return McpOAuthResult.Redirect(AppendQuery(state.RedirectUri, new KeyValuePair<string, string?>[]
            {
                new("code", code),
                new("state", state.ClientState),
            }));
        }

        /// <summary>
        /// Forwards an <c>authorization_code</c> (with <c>code</c> and <c>code_verifier</c>) or <c>refresh_token</c>
        /// grant to Entra's fixed token endpoint with the broker's client ID, callback and scope; everything else the
        /// client sent (<c>resource</c>, <c>client_secret</c>, its own <c>redirect_uri</c> and <c>scope</c>) is dropped.
        /// Entra's status and body are returned unchanged.
        /// </summary>
        public async Task<McpOAuthResult> TokenAsync(Uri apiBaseUri, string? body, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(apiBaseUri);
            if (!this.IsEnabled)
            {
                return McpOAuthResult.NotFound;
            }

            if (string.IsNullOrEmpty(body) || body.Length > MaxBodyLength || !OAuthParameters.TryParse(body, out var parameters))
            {
                return this.Refuse("token", "invalid_request", "The token request must be a form of at most 16 KB without repeated parameters.");
            }

            var clientId = Get(parameters, "client_id");
            if (clientId is not null && !string.Equals(clientId, this.ClientId, StringComparison.Ordinal))
            {
                return this.Refuse("token", "invalid_client", "Unknown client_id.");
            }

            var grantType = Get(parameters, "grant_type");
            var form = new List<KeyValuePair<string, string>>
            {
                new("grant_type", grantType ?? string.Empty),
                new("client_id", this.ClientId),
            };
            switch (grantType)
            {
                case "authorization_code":
                    var code = Get(parameters, "code");
                    var verifier = Get(parameters, "code_verifier");
                    if (code is null || !IsPkceValue(verifier))
                    {
                        return this.Refuse("token", "invalid_request", "authorization_code needs code and a valid code_verifier.");
                    }

                    form.Add(new("code", code));
                    form.Add(new("code_verifier", verifier));
                    form.Add(new("redirect_uri", McpOAuthPaths.Endpoint(apiBaseUri, McpOAuthPaths.Callback)));
                    break;
                case "refresh_token":
                    var refreshToken = Get(parameters, "refresh_token");
                    if (refreshToken is null)
                    {
                        return this.Refuse("token", "invalid_request", "refresh_token is required.");
                    }

                    form.Add(new("refresh_token", refreshToken));
                    break;
                default:
                    return this.Refuse("token", "unsupported_grant_type", "Only authorization_code and refresh_token are supported.");
            }

            form.Add(new("scope", string.Join(' ', this.Scopes)));

            EntraTokenResponse response;
            try
            {
                response = await this.tokenClient.RedeemAsync(form, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                this.log.LogWarning("MCP OAuth token ({GrantType}) could not reach Entra: {ExceptionType}.", grantType, ex.GetType().Name);
                return McpOAuthResult.Error(HttpStatusCode.BadGateway, "server_error", "The identity provider could not be reached.");
            }

            this.log.LogInformation(
                "MCP OAuth token ({GrantType}) relayed Entra status {StatusCode} (error: {EntraError}).",
                grantType,
                (int)response.StatusCode,
                LogSafeErrorCode(EntraErrorCode(response.Body)));
            return McpOAuthResult.Json(response.StatusCode, response.Body);
        }

        private string ClientId => this.oauth.CurrentValue.ClientId.Trim();

        private IReadOnlyList<string> Scopes
            => this.protectedResource.Scopes.Append(OfflineAccessScope).Distinct(StringComparer.Ordinal).ToList();

        private McpOAuthResult Refuse(string endpoint, string error, string description)
        {
            this.log.LogInformation("MCP OAuth {Endpoint} refused the request with {Error}.", endpoint, error);
            return McpOAuthResult.Error(HttpStatusCode.BadRequest, error, description);
        }

        private static bool TryParseObject(string body, [NotNullWhen(true)] out JsonObject? value)
        {
            try
            {
                value = JsonNode.Parse(body, documentOptions: StrictJson) as JsonObject;
            }
            catch (JsonException)
            {
                value = null;
            }

            return value is not null;
        }

        private static bool TryGetStrings(JsonObject request, string name, out List<string> values)
        {
            values = new List<string>();
            if (request[name] is not JsonArray array)
            {
                return false;
            }

            foreach (var item in array)
            {
                if (item is not JsonValue value || !value.TryGetValue<string>(out var text))
                {
                    return false;
                }

                values.Add(text);
            }

            return true;
        }

        private static bool IsAbsentOrSubset(JsonObject request, string name, IReadOnlyCollection<string> allowed)
            => request[name] is null || (TryGetStrings(request, name, out var values) && values.All(allowed.Contains));

        private EntraEndpoints? EnabledEndpoints
            => this.IsEnabled ? EntraEndpoints.FromAuthority(this.authentication.CurrentValue.Authority) : null;

        private McpOAuthResult RedirectError(string endpoint, string redirectUri, string? clientState, string error, string description)
        {
            this.log.LogInformation("MCP OAuth {Endpoint} redirected {Error} to the client.", endpoint, error);
            return McpOAuthResult.Redirect(AppendQuery(redirectUri, new KeyValuePair<string, string?>[]
            {
                new("error", error),
                new("error_description", description),
                new("state", clientState),
            }));
        }

        private static string? Get(IReadOnlyDictionary<string, string> parameters, string name)
            => parameters.TryGetValue(name, out var value) ? value : null;

        // RFC 7636: 43 to 128 characters from [A-Za-z0-9-._~].
        private static bool IsPkceValue([NotNullWhen(true)] string? value)
            => value is { Length: >= 43 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~');

        private static string AppendQuery(string uri, IEnumerable<KeyValuePair<string, string?>> parameters)
        {
            var query = string.Join('&', parameters
                .Where(p => p.Value is not null)
                .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!)}"));
            return uri + (uri.Contains('?') ? "&" : "?") + query;
        }

        // Entra error codes are short snake_case words; anything else is not echoed into logs.
        private static string LogSafeErrorCode(string? error)
            => error is { Length: > 0 and <= 64 } && error.All(c => char.IsAsciiLetterLower(c) || c == '_') ? error : "unrecognized";

        private static string? EntraErrorCode(string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                return document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.String
                        ? error.GetString()
                        : "none";
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
