# MCP OAuth broker for Entra ID — design

Date: 2026-11-22
Status: Approved by the owner ("yes please fix it")
Repo: `safeexchange` (backend) · Branch: `feat/mcp-oauth-broker` · one PR

## Problem

Copilot CLI (Rust MCP SDK `rmcp`) cannot sign in to `/api/mcp` on staging:
`Failed to discover authorization server metadata for https://…/api/mcp`. Three causes, all verified
against `rmcp` (`crates/rmcp/src/transport/auth.rs`) and Entra:

1. **Wrong `resource` in protected-resource metadata.** We return `api://safeexchange-staging.onmicrosoft.com`.
   RFC 9728 and `rmcp` (`validate_resource_metadata_resource`) require the protected resource's own URL
   (the MCP endpoint, e.g. `https://safeexchange-staging.azurewebsites.net/api/mcp`); anything else rejects
   the document.
2. **No client registration path.** `rmcp` obtains a `client_id` by dynamic client registration (RFC 7591)
   or a client-ID metadata document. Entra supports neither (its metadata has no `registration_endpoint`).
3. **`resource` parameter.** `rmcp` sends `resource=<MCP URL>` (RFC 8707) on authorize and token requests;
   Entra v2 rejects a `resource` that does not match the requested scope (`api://…/user_impersonation`).

Entra itself is fine as the identity provider and its access tokens are what the backend already
validates (audience `api://…`).

## Decision

Add a small **OAuth broker** on SafeExchange that presents a standards-conformant authorization server
to MCP clients and delegates every real decision to Entra. SafeExchange never issues, mints, stores or
inspects tokens; clients still receive Entra tokens.

Flow (authorization code + PKCE, public client):

```
client ──► POST /api/oauth/register        → fixed client_id (the Entra public client), loopback redirect URIs only
client ──► GET  /api/oauth/authorize?…     → 302 to Entra /authorize (our client_id, our callback, scope, PKCE, wrapped state)
Entra  ──► GET  /api/oauth/callback?code&state → 302 to the client's loopback redirect_uri with code + original state
client ──► POST /api/oauth/token (code | refresh_token) → forwarded to Entra /token (our callback, scope, no resource) → Entra's JSON
```

### Endpoints (all anonymous, all under the existing `/api` route prefix)

| Route | Purpose |
|---|---|
| `GET /api/.well-known/oauth-protected-resource` | (existing) now `resource` = this host's `/api/mcp` URL; `authorization_servers` = [`https://<host>/api`] when the broker is configured, else the Entra authority (old behaviour) |
| `GET /api/.well-known/oauth-authorization-server` and `GET /api/.well-known/openid-configuration` | Authorization-server metadata (same JSON). `rmcp` tries `{issuer}/.well-known/openid-configuration` for an issuer with a path, which is the one we can serve under the `api` route prefix |
| `POST /api/oauth/register` | RFC 7591 response with the configured Entra public `client_id`, `token_endpoint_auth_method: none` |
| `GET /api/oauth/authorize` | Validates and redirects to Entra |
| `GET /api/oauth/callback` | Entra's redirect target; forwards to the client's loopback URI |
| `POST /api/oauth/token` | Forwards to Entra's token endpoint and relays the response |

Authorization-server metadata:

```json
{
  "issuer": "https://<host>/api",
  "authorization_endpoint": "https://<host>/api/oauth/authorize",
  "token_endpoint": "https://<host>/api/oauth/token",
  "registration_endpoint": "https://<host>/api/oauth/register",
  "response_types_supported": ["code"],
  "grant_types_supported": ["authorization_code", "refresh_token"],
  "code_challenge_methods_supported": ["S256"],
  "token_endpoint_auth_methods_supported": ["none"],
  "scopes_supported": ["<api scope>", "offline_access"]
}
```

`<host>` comes from the incoming request URL (as `file_transfer_info` and the challenge already do).

### Rules (security)

- **Loopback only.** Client `redirect_uri`s must be `http` with host `127.0.0.1`, `localhost` or `[::1]`,
  any port, no fragment, no user-info. Enforced at register, authorize and callback (callback re-validates
  the decoded value). This makes the broker useless as an open redirect: a code can only reach the
  browser's own machine, and PKCE means only the client holding the verifier can redeem it.
- **Client.** `client_id` must equal the configured Entra public client ID; anything else → `invalid_client`.
- **Authorize parameters.** `response_type=code` and `code_challenge_method=S256` with a `code_challenge`
  are required; `state` passes through; `resource` and unknown parameters are dropped; `scope` is replaced
  by the configured API scope plus `offline_access` (requested scopes outside that set are ignored).
- **State wrapping.** Entra receives `state = base64url(JSON{ r: clientRedirect, s: clientState })`
  (max 2 KB). It carries no authority (see loopback rule), so it is not encrypted or signed; size and
  format are validated on decode.
- **Errors at callback.** `error`/`error_description` from Entra are forwarded to the client's redirect
  (only `error`, `error_description`, `state`), never rendered as HTML.
- **Token forwarding.** Only `grant_type` `authorization_code` (with `code`, `code_verifier`) or
  `refresh_token` (with `refresh_token`) are accepted; the broker sets `client_id`, `redirect_uri`
  (our callback) and `scope`, drops `resource` and `client_secret`, and posts to Entra's fixed token
  endpoint derived from `Authentication:Authority` (no caller-influenced URL → no SSRF). Entra's status
  code and JSON body are relayed unchanged with `Cache-Control: no-store`.
- **Logging.** Never log codes, verifiers, tokens, refresh tokens or state; log route, outcome and Entra
  error codes only.
- **Feature gating.** All broker routes return 404 unless `Features.McpEndpoint` is on **and**
  `Mcp:OAuth:ClientId` is set. The auth middleware skips the broker functions by exact function name.

### Configuration

- `Mcp:OAuth:ClientId` — Entra public client application ID used by the broker (Key Vault
  `Mcp--OAuth--ClientId`; ARM parameter `settings-mcp-oauthClientId`, default empty).
- The API scope comes from the existing `McpProtectedResource` scope logic
  (`Authentication:McpScopes` or `{api resource}/user_impersonation`), where the api resource keeps being
  derived from `ValidAudiences` (`api://…`). Only the **published** `resource` changes to the MCP URL.

### Entra setup (per environment, documented + done for staging)

New app registration "SafeExchange MCP Client (<env>)": public client (`isFallbackPublicClient: true`),
public-client redirect URI `https://<host>/api/oauth/callback`, required resource access to the API's
`user_impersonation`; the API app pre-authorises it (`api.preAuthorizedApplications`) so users get no
consent prompt.

## Architecture

- `SafeExchange.Core/Mcp/OAuth/` (host-agnostic, pure, unit-tested): `McpOAuthOptions`,
  `LoopbackRedirectUri` (validation), `BrokerState` (encode/decode), `AuthorizationServerMetadata` builder,
  `McpOAuthBroker` (register/authorize/callback request → result objects: redirect URL, JSON body, error),
  `IEntraTokenClient` + `EntraTokenClient` (HttpClient POST to the fixed token endpoint).
- `SafeExchange.Core.Functions/Mcp/OAuth/`: thin `HttpRequestData` adapters.
- `SafeExchange.Functions/Functions/SafeMcpOAuth.cs`: triggers. `IsAuthenticatedHttpTrigger` excludes them
  by function name.
- `McpProtectedResource` publishes `resource` = MCP URL (needs the request base URI) and
  `authorization_servers` = broker issuer when configured.

## Testing

TDD per task. Unit tests for every rule above (loopback variants incl. `localhost.evil.com`,
`127.0.0.1.nip.io`, `https://127.0.0.1`, user-info, fragments; state round-trip, oversize, malformed;
authorize parameter rewriting; token form rewriting incl. dropped `resource`/`client_secret`; metadata
JSON; feature gating 404; middleware skip list exact). Adapter tests with a fake `IEntraTokenClient`.
REST contract tests stay green. Staging: real Copilot CLI `/mcp auth safeexchange-staging` end to end.

## Out of scope

Confidential clients, client secrets, token minting/encryption, consent UI, per-client registrations,
S2S changes (client-credentials callers keep using Entra directly).
