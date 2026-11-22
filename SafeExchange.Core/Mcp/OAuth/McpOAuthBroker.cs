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
    using System.Globalization;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A standards-shaped OAuth authorization server for MCP clients that delegates every decision to Entra ID:
    /// registration hands out the configured Entra public client, authorize asks the user for consent and then relays
    /// the code flow through this host's callback with a signed state bound to the consent cookie, and token forwards to
    /// Entra's fixed token endpoint. It never issues, stores or inspects tokens, and never logs codes, verifiers,
    /// tokens, state, cookies or nonces.
    /// </summary>
    public sealed class McpOAuthBroker
    {
        public const int MaxBodyLength = 16 * 1024;

        public const string OfflineAccessScope = "offline_access";

        private const string ConsentTokenField = "consent_token";

        private const string ConsentExpiresField = "consent_expires";

        private const int MaxRedirectUris = 10;

        private static readonly string[] SupportedGrantTypes = { "authorization_code", "refresh_token" };

        private static readonly string[] SupportedResponseTypes = { "code" };

        private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false };

        private readonly McpProtectedResource protectedResource;

        private readonly IOptionsMonitor<McpOAuthOptions> oauth;

        private readonly IOptionsMonitor<AuthenticationConfiguration> authentication;

        private readonly IOptionsMonitor<Features> features;

        private readonly IEntraTokenClient tokenClient;

        private readonly EntraCallGate entraCallGate;

        private readonly ILogger log;

        public McpOAuthBroker(
            McpProtectedResource protectedResource,
            IOptionsMonitor<McpOAuthOptions> oauth,
            IOptionsMonitor<AuthenticationConfiguration> authentication,
            IOptionsMonitor<Features> features,
            IEntraTokenClient tokenClient,
            EntraCallGate entraCallGate,
            ILogger<McpOAuthBroker> log)
        {
            this.protectedResource = protectedResource ?? throw new ArgumentNullException(nameof(protectedResource));
            this.oauth = oauth ?? throw new ArgumentNullException(nameof(oauth));
            this.authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
            this.features = features ?? throw new ArgumentNullException(nameof(features));
            this.tokenClient = tokenClient ?? throw new ArgumentNullException(nameof(tokenClient));
            this.entraCallGate = entraCallGate ?? throw new ArgumentNullException(nameof(entraCallGate));
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

            if (request["token_endpoint_auth_method"] is JsonNode method && !(TryGetString(method, out var name) && name == "none"))
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
        /// Validates an authorization request (unknown client or non-loopback redirect: 400 JSON, never redirected; other
        /// errors go back to the client's redirect URI) and answers the consent page, setting the consent cookie. Nothing
        /// is sent to Entra until the user allows the request (<see cref="AuthorizeConsent"/>); <c>prompt</c> is never
        /// forwarded.
        /// </summary>
        public McpOAuthResult Authorize(Uri apiBaseUri, string? query)
        {
            ArgumentNullException.ThrowIfNull(apiBaseUri);
            if (!this.TryGetStateKey(out var key))
            {
                return McpOAuthResult.NotFound;
            }

            if (!OAuthParameters.TryParse(query, out var parameters))
            {
                return this.Refuse("authorize", "invalid_request", "A parameter was repeated.");
            }

            if (!this.TryReadAuthorize(parameters, out var request, out var failure))
            {
                return failure.RedirectUri is null
                    ? this.Refuse("authorize", failure.Error, failure.Description)
                    : this.RedirectError("authorize", failure.RedirectUri, failure.ClientState, failure.Error, failure.Description);
            }

            var now = DateTimeProvider.UtcNow;
            var nonce = ConsentToken.NewNonce();
            if (!SignedBrokerState.TryCreate(key, request.RedirectUri, request.State, nonce, now, out _))
            {
                return this.RedirectError("authorize", request.RedirectUri, request.State, "invalid_request", "state is too long.");
            }

            var fields = request.Fields(SignedBrokerState.UnixSeconds(now + ConsentCookie.Lifetime));
            fields.Add(new(ConsentTokenField, ConsentToken.Create(key, nonce, fields)));
            var page = ConsentPage.Render(McpOAuthPaths.Endpoint(apiBaseUri, McpOAuthPaths.Authorize), Origin(request.RedirectUri), fields);

            this.log.LogInformation("MCP OAuth authorize answered the consent page.");
            return McpOAuthResult.Html(HttpStatusCode.OK, page) with { Cookie = ConsentCookie.Issue(nonce) };
        }

        /// <summary>
        /// The consent form post: re-validates the parameters like <see cref="Authorize"/>, requires the consent cookie
        /// and a consent token that matches it and the parameters, and is valid for <see cref="ConsentCookie.Lifetime"/>.
        /// <c>allow</c> redirects to Entra with the broker's client ID, callback and scope, the client's S256 challenge,
        /// <c>login_hint</c> when present and the signed state; <c>deny</c> redirects <c>access_denied</c> to the
        /// client. Any failure answers the 400 page and never redirects.
        /// </summary>
        public McpOAuthResult AuthorizeConsent(Uri apiBaseUri, string? form, string? consentCookie)
        {
            ArgumentNullException.ThrowIfNull(apiBaseUri);
            var endpoints = this.EnabledEndpoints;
            if (endpoints is null || !this.TryGetStateKey(out var key))
            {
                return McpOAuthResult.NotFound;
            }

            if (string.IsNullOrEmpty(form) || form.Length > MaxBodyLength || !OAuthParameters.TryParse(form, out var parameters))
            {
                return this.RefusePage("authorize", "invalid_form");
            }

            if (!this.TryReadAuthorize(parameters, out var request, out _)
                || !long.TryParse(Get(parameters, ConsentExpiresField), NumberStyles.None, CultureInfo.InvariantCulture, out var expires))
            {
                return this.RefusePage("authorize", "invalid_parameters");
            }

            if (!ConsentToken.IsNonce(consentCookie))
            {
                return this.RefusePage("authorize", "missing_cookie");
            }

            if (!ConsentToken.Verify(key, consentCookie, request.Fields(expires), Get(parameters, ConsentTokenField)))
            {
                return this.RefusePage("authorize", "invalid_consent_token");
            }

            var now = DateTimeProvider.UtcNow;
            if (expires <= SignedBrokerState.UnixSeconds(now))
            {
                return this.RefusePage("authorize", "expired");
            }

            switch (Get(parameters, "decision"))
            {
                case "deny":
                    this.log.LogInformation("MCP OAuth authorize consent was denied by the user.");
                    return McpOAuthResult.Redirect(AppendQuery(request.RedirectUri, new KeyValuePair<string, string?>[]
                    {
                        new("error", "access_denied"),
                        new("error_description", "The user denied the sign-in."),
                        new("state", request.State),
                    })) with { Cookie = ConsentCookie.Expire };
                case "allow":
                    break;
                default:
                    return this.RefusePage("authorize", "invalid_decision");
            }

            if (!SignedBrokerState.TryCreate(key, request.RedirectUri, request.State, consentCookie, now, out var signedState))
            {
                return this.RefusePage("authorize", "invalid_parameters");
            }

            var entraParameters = new List<KeyValuePair<string, string?>>
            {
                new("client_id", this.ClientId),
                new("response_type", "code"),
                new("redirect_uri", McpOAuthPaths.Endpoint(apiBaseUri, McpOAuthPaths.Callback)),
                new("response_mode", "query"),
                new("scope", string.Join(' ', this.Scopes)),
                new("code_challenge", request.CodeChallenge),
                new("code_challenge_method", "S256"),
                new("state", signedState),
                new("login_hint", request.LoginHint),
            };

            this.log.LogInformation("MCP OAuth authorize redirected to Entra.");
            return McpOAuthResult.Redirect(AppendQuery(endpoints.Authorize, entraParameters));
        }

        /// <summary>
        /// Entra's redirect target: verifies the signed state (HMAC, expiry, loopback redirect) and that its nonce is the
        /// consent cookie's, then forwards <c>code</c> and the client's <c>state</c> (or only <c>error</c>,
        /// <c>error_description</c> and <c>state</c>) to the loopback redirect and expires the cookie. Without a valid
        /// state and cookie there is nowhere safe to go, so the answer is the 400 page.
        /// </summary>
        public McpOAuthResult Callback(string? query, string? consentCookie)
        {
            if (!this.TryGetStateKey(out var key))
            {
                return McpOAuthResult.NotFound;
            }

            if (!OAuthParameters.TryParse(query, out var parameters)
                || !SignedBrokerState.TryVerify(key, Get(parameters, "state"), DateTimeProvider.UtcNow, out var state))
            {
                return this.RefusePage("callback", "invalid_state");
            }

            if (!ConsentToken.IsNonce(consentCookie))
            {
                return this.RefusePage("callback", "missing_cookie");
            }

            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(consentCookie), Encoding.ASCII.GetBytes(state.Nonce)))
            {
                return this.RefusePage("callback", "nonce_mismatch");
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
                })) with { Cookie = ConsentCookie.Expire };
            }

            var code = Get(parameters, "code");
            if (code is null)
            {
                return this.RedirectError("callback", state.RedirectUri, state.ClientState, "server_error", "The identity provider returned no authorization code.")
                    with { Cookie = ConsentCookie.Expire };
            }

            this.log.LogInformation("MCP OAuth callback forwarded the authorization code to the client.");
            return McpOAuthResult.Redirect(AppendQuery(state.RedirectUri, new KeyValuePair<string, string?>[]
            {
                new("code", code),
                new("state", state.ClientState),
            })) with { Cookie = ConsentCookie.Expire };
        }

        /// <summary>
        /// Forwards an <c>authorization_code</c> (with <c>code</c> and <c>code_verifier</c>) or <c>refresh_token</c>
        /// grant to Entra's fixed token endpoint with the broker's client ID, callback and scope; everything else the
        /// client sent (<c>resource</c>, <c>client_secret</c>, its own <c>redirect_uri</c> and <c>scope</c>) is dropped.
        /// Entra's status and body are returned unchanged when the status is 200, 400 or 401 and the body is a JSON
        /// object; anything else is 502 <c>server_error</c>. At most <see cref="EntraCallGate"/> calls run at once; beyond
        /// that the answer is 503 <c>temporarily_unavailable</c> with <c>Retry-After</c>.
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

            if (!string.Equals(Get(parameters, "client_id"), this.ClientId, StringComparison.Ordinal))
            {
                return this.Refuse("token", "invalid_client", "Unknown client_id; register at the registration endpoint first.");
            }

            var grantType = Get(parameters, "grant_type");
            if (grantType is null)
            {
                return this.Refuse("token", "invalid_request", "grant_type is required.");
            }

            if (grantType is not ("authorization_code" or "refresh_token"))
            {
                return this.Refuse("token", "unsupported_grant_type", "Only authorization_code and refresh_token are supported.");
            }

            var form = new List<KeyValuePair<string, string>>
            {
                new("grant_type", grantType),
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

            if (!await this.entraCallGate.TryEnterAsync(cancellationToken))
            {
                this.log.LogWarning("MCP OAuth token ({GrantType}) found every Entra call slot busy; answered 503.", grantType);
                return McpOAuthResult.Error(HttpStatusCode.ServiceUnavailable, "temporarily_unavailable", "Too many sign-ins are in progress; retry shortly.")
                    with { RetryAfterSeconds = EntraCallGate.RetryAfterSeconds };
            }

            EntraTokenResponse response;
            try
            {
                response = await this.tokenClient.RedeemAsync(form, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                this.log.LogWarning(
                    "MCP OAuth token ({GrantType}) could not reach Entra: {ExceptionType} ({HttpRequestError}, inner {InnerExceptionType}).",
                    grantType,
                    ex.GetType().Name,
                    ex is HttpRequestException requestException ? requestException.HttpRequestError.ToString() : "none",
                    ex.InnerException?.GetType().Name ?? "none");
                return McpOAuthResult.Error(HttpStatusCode.BadGateway, "server_error", "The identity provider could not be reached.");
            }
            finally
            {
                this.entraCallGate.Exit();
            }

            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                || !TryReadEntraError(response.Body, out var entraError))
            {
                this.log.LogWarning("MCP OAuth token ({GrantType}) got an unexpected Entra response with status {StatusCode}.", grantType, (int)response.StatusCode);
                return McpOAuthResult.Error(HttpStatusCode.BadGateway, "server_error", "The identity provider returned an unexpected response.");
            }

            this.log.LogInformation(
                "MCP OAuth token ({GrantType}) relayed Entra status {StatusCode} (error: {EntraError}).",
                grantType,
                (int)response.StatusCode,
                entraError);
            return McpOAuthResult.Json(response.StatusCode, response.Body);
        }

        private string ClientId => this.oauth.CurrentValue.ClientId.Trim();

        private IReadOnlyList<string> Scopes
            => this.protectedResource.Scopes.Append(OfflineAccessScope).Distinct(StringComparer.Ordinal).ToList();

        private bool TryGetStateKey([NotNullWhen(true)] out byte[]? key)
        {
            key = null;
            return this.IsEnabled && McpOAuthStateKey.TryDecode(this.oauth.CurrentValue.StateKey, out key);
        }

        // The checks shared by the authorize request and the consent form post, in RFC 6749 order.
        private bool TryReadAuthorize(
            IReadOnlyDictionary<string, string> parameters,
            [NotNullWhen(true)] out AuthorizeRequest? request,
            [NotNullWhen(false)] out AuthorizeFailure? failure)
        {
            request = null;
            failure = null;
            if (!string.Equals(Get(parameters, "client_id"), this.ClientId, StringComparison.Ordinal))
            {
                failure = new("invalid_client", "Unknown client_id; register at the registration endpoint first.", null, null);
                return false;
            }

            var redirectUri = Get(parameters, "redirect_uri");
            if (!LoopbackRedirectUri.IsValid(redirectUri))
            {
                failure = new("invalid_request", "redirect_uri must be a loopback http URI (127.0.0.1, localhost or [::1]) without fragment or user info.", null, null);
                return false;
            }

            var clientState = Get(parameters, "state");
            var responseType = Get(parameters, "response_type");
            if (responseType is null)
            {
                failure = new("invalid_request", "response_type is required.", redirectUri, clientState);
                return false;
            }

            if (responseType != "code")
            {
                failure = new("unsupported_response_type", "Only response_type=code is supported.", redirectUri, clientState);
                return false;
            }

            var challenge = Get(parameters, "code_challenge");
            if (!IsPkceValue(challenge) || Get(parameters, "code_challenge_method") != "S256")
            {
                failure = new("invalid_request", "PKCE with code_challenge_method=S256 is required.", redirectUri, clientState);
                return false;
            }

            request = new AuthorizeRequest(this.ClientId, redirectUri, challenge, clientState, Get(parameters, "scope"), Get(parameters, "login_hint"));
            return true;
        }

        private McpOAuthResult RefusePage(string endpoint, string reason)
        {
            this.log.LogWarning("MCP OAuth {Endpoint} refused the request: {Reason}.", endpoint, reason);
            return McpOAuthResult.Html(HttpStatusCode.BadRequest, ConsentPage.Message(ConsentPage.InvalidRequestMessage));
        }

        private static string Origin(string redirectUri) => new Uri(redirectUri).GetLeftPart(UriPartial.Authority);

        private McpOAuthResult Refuse(string endpoint, string error, string description)
        {
            this.log.LogWarning("MCP OAuth {Endpoint} refused the request with {Error}.", endpoint, error);
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
                if (!TryGetString(item, out var text))
                {
                    return false;
                }

                values.Add(text);
            }

            return true;
        }

        // A string with an invalid UTF-16 escape (e.g. a lone "\uD800") throws InvalidOperationException on read.
        private static bool TryGetString(JsonNode? node, [NotNullWhen(true)] out string? text)
        {
            text = null;
            try
            {
                return node is JsonValue value && value.TryGetValue(out text);
            }
            catch (InvalidOperationException)
            {
                text = null;
                return false;
            }
        }

        private static bool IsAbsentOrSubset(JsonObject request, string name, IReadOnlyCollection<string> allowed)
            => request[name] is null || (TryGetStrings(request, name, out var values) && values.All(allowed.Contains));

        private EntraEndpoints? EnabledEndpoints
            => this.IsEnabled ? EntraEndpoints.FromAuthority(this.authentication.CurrentValue.Authority) : null;

        private McpOAuthResult RedirectError(string endpoint, string redirectUri, string? clientState, string error, string description)
        {
            this.log.LogWarning("MCP OAuth {Endpoint} redirected {Error} to the client.", endpoint, error);
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

        // False unless the body is a JSON object; errorCode is its log-safe "error" ("none" when absent).
        private static bool TryReadEntraError(string body, out string errorCode)
        {
            errorCode = "none";
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                if (document.RootElement.TryGetProperty("error", out var error))
                {
                    errorCode = error.ValueKind == JsonValueKind.String ? LogSafeErrorCode(error.GetString()) : "unrecognized";
                }

                return true;
            }
            catch (JsonException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                errorCode = "unrecognized";
                return true;
            }
        }

        // The validated authorize parameters; Fields are the consent form fields the consent token covers, in order.
        private sealed record AuthorizeRequest(string ClientId, string RedirectUri, string CodeChallenge, string? State, string? Scope, string? LoginHint)
        {
            public List<KeyValuePair<string, string>> Fields(long consentExpires)
                => new KeyValuePair<string, string?>[]
                {
                    new("client_id", this.ClientId),
                    new("redirect_uri", this.RedirectUri),
                    new("response_type", "code"),
                    new("code_challenge", this.CodeChallenge),
                    new("code_challenge_method", "S256"),
                    new("state", this.State),
                    new("scope", this.Scope),
                    new("login_hint", this.LoginHint),
                    new(ConsentExpiresField, consentExpires.ToString(CultureInfo.InvariantCulture)),
                }
                .Where(field => field.Value is not null)
                .Select(field => new KeyValuePair<string, string>(field.Key, field.Value!))
                .ToList();
        }

        // RedirectUri is null when the error must not be redirected.
        private sealed record AuthorizeFailure(string Error, string Description, string? RedirectUri, string? ClientState);
    }
}
