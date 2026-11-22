# MCP OAuth broker hardening (OWASP review follow-up) — design

Date: 2026-11-22
Status: Approved by the owner ("okay please implement")
Branch: `feat/mcp-oauth-broker` (same PR #74)
Base design: `2026-11-22-mcp-oauth-broker-design.md`

## Why

The OWASP Top 10:2025 review of PR #74 found no P0/P1 issues, but two P2 findings and several cheap P3/P4
items. The main one is the MCP "confused deputy" gap: a static, pre-authorised client, no per-client user
consent, `prompt` passthrough (so `prompt=none` issues a code silently), and an unsigned `state`. MCP
security best practices say proxies with static client IDs MUST obtain user consent.

## Changes

### A. User consent, no prompt passthrough, signed state

**Flow**

1. `GET /api/oauth/authorize?…` (opened by the MCP client in the browser): validate exactly as today
   (client_id, loopback redirect_uri, response_type=code, S256 challenge, strict parsing). On success,
   instead of redirecting to Entra, return a **consent page** (HTML, 200) and set cookie
   `__Host-saex-mcp-consent` = random 32-byte nonce (base64url), `Secure; HttpOnly; SameSite=Lax; Path=/;
   Max-Age=600`. The page contains a form `POST /api/oauth/authorize` (same route, POST) with hidden
   fields: every validated authorize parameter that is forwarded (redirect_uri, state, code_challenge,
   scope as received, login_hint if present) and `consent_token` = HMAC-SHA256(key, nonce ‖ canonical
   parameter string) base64url. Buttons: `decision=allow` and `decision=deny`.
2. `POST /api/oauth/authorize` (form, `application/x-www-form-urlencoded`, ≤ 16 KB): re-parse and
   re-validate the parameters exactly like GET; require the cookie; verify `consent_token` against the
   cookie nonce with `CryptographicOperations.FixedTimeEquals`; on any failure → 400 page "This sign-in
   request is invalid or expired. Start the sign-in again from your MCP client." (no redirect).
   - `deny` → 302 to the client redirect with `error=access_denied` (+ client `state`).
   - `allow` → 302 to Entra authorize (as today) with the **signed state** below. `prompt` is never
     forwarded (not on GET, not on POST). `login_hint` keeps being forwarded when present.
3. `GET /api/oauth/callback`: decode and verify the signed state (HMAC, expiry), require the cookie
   and that its nonce equals the state's nonce, re-validate loopback; then forward `code`/`state` (or
   `error`/`error_description`/`state`) to the client as today, and expire the cookie
   (`Max-Age=0`). Any failure → 400 page, no redirect.

**Signed state** (replaces `BrokerState` encoding): `base64url(payloadJson) + "." + base64url(hmac)`
where `payloadJson = {"r": redirectUri, "s": clientState|null, "n": nonce, "e": unixExpiry}`;
expiry = now + 10 minutes; total ≤ 2 KB. HMAC-SHA256 with the state key, fixed-time comparison.

**Key**: `Mcp:OAuth:StateKey` (base64, ≥ 32 bytes). The broker is enabled only when it is set (in
addition to the existing conditions). ARM: new `securestring` parameter `settings-mcp-oauthStateKey` whose
default value is `[base64(concat(newGuid(), newGuid()))]` (ARM allows `newGuid()` only in parameter
default values); the Key Vault secret `Mcp--OAuth--StateKey` is written with the same condition as the
client id (only when the broker client id is set). The default regenerates on every deployment, so a
redeploy rotates the key; only sign-ins in progress at that moment fail. Tests use a fixed key.

**Consent page** (server-rendered, no JavaScript, everything HTML-encoded):
title "Sign in an MCP client to SafeExchange", text: "An application on this computer is asking for
access to SafeExchange as you. It will receive the result at <b>{scheme://host:port}</b>. Access
requested: read and manage your secrets; stay signed in. Only continue if you just started a sign-in in
an MCP client such as Copilot CLI." Headers: `Content-Security-Policy: default-src 'none'; style-src
'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'`, `X-Frame-Options: DENY`,
`X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store`,
`Content-Type: text/html; charset=utf-8`. The environment name is not shown (no reliable source).

### B. Rate limiting

In-memory, per-instance, per-client-IP token bucket for all anonymous broker endpoints
(`/api/oauth/register|authorize|callback|token`): default 30 requests/minute burst 30 per IP for token,
60/min for the others (config `Mcp:OAuth:RateLimit:*`, sensible defaults). Exceeded → 429 with
`Retry-After` (seconds) and RFC 6749-style JSON `{"error":"slow_down"}` for token, plain 429 page/JSON for
the others. Client IP: the first entry of `X-Forwarded-For` set by the App Service front end, else the
connection remote address (document that it is best-effort; edge rate limiting is the stronger option).
Bucket store bounded (LRU / max 10k keys) to avoid memory growth. Also cap concurrent outbound Entra
token calls per instance (default 16) with `SemaphoreSlim`; when saturated beyond a short wait (2 s) →
503 `{"error":"temporarily_unavailable"}` with `Retry-After: 2`.

### C. Robust JSON handling

Catch `InvalidOperationException` (invalid UTF-16 escapes / invalid UTF-8 in strings) wherever broker
JSON is read (`Register` body, state payload) → treat as malformed (400). Tests with `"\uD800"` and raw
invalid UTF-8 bytes.

### D. Refresh relay note

Document in `docs/mcp.md` security notes that refresh-token and code redemptions reach Entra from the
function app's egress IP, so IP/location-based Conditional Access and Identity Protection see that IP.

### E. Metadata hardening

- `Cache-Control: no-store` on the protected-resource metadata responses (all three routes).
- Derived URLs must be `https` unless the host is a loopback name (local development); otherwise the
  broker and the metadata respond 404 (treated as not configured) and log a warning once.

### F. Logging

- Broker refusals (`Refuse`, `RedirectError`, consent failures, state verification failures, rate-limit
  hits) log at **Warning** with the existing safe templates (never codes, tokens, verifiers, state,
  cookies or nonces).
- The "could not reach Entra" warning includes `HttpRequestException.HttpRequestError` and the inner
  exception type name.

### G. Token relay and ARM

- Relay Entra's token response unchanged only when the status is 200, 400 or 401 **and** the body is a
  JSON object; otherwise log the status and return 502 `{"error":"server_error"}`.
- Document in `docs/mcp.md` how to switch the broker off: set `Features--McpEndpoint=False`, or delete /
  disable the `Mcp--OAuth--ClientId` Key Vault secret (an empty ARM parameter leaves an existing secret in
  place).

## Testing

TDD for every item. Unit tests in `SafeExchange.Tests\Mcp\OAuth` for: consent GET page content/headers/
cookie; POST allow/deny; POST with missing/mismatched cookie, tampered token, tampered parameters,
expired nonce; `prompt` never forwarded (GET and POST, incl. `prompt=none`); signed state round-trip,
tamper, expiry, nonce/cookie mismatch at callback; rate limiting (burst then 429 + Retry-After; per-IP
isolation; bounded store); concurrency cap (503); JSON edge cases; no-store headers; https enforcement;
Warning-level logs and no secrets in logs (extend the existing log-capture test with the cookie nonce and
state); non-JSON / unexpected-status Entra responses → 502. Functions adapter tests for cookies (`Set-Cookie`
attributes) and HTML headers. Contract tests and the route guard test stay green (the authorize function
gains the POST method — update `FunctionRouteTests` expected methods for that function only).

## Out of scope

Persisted per-client registrations, remembering approvals, edge (Front Door/APIM) rate limiting, alert
rules, removing `localhost` from the loopback allow-list.
