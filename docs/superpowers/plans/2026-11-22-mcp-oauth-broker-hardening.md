# MCP OAuth broker hardening (OWASP follow-up) — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Close the OWASP review findings on the MCP OAuth broker: a SafeExchange consent page with an HMAC-signed,
cookie-bound state and no `prompt` passthrough (A), per-instance per-client rate limits and an Entra call cap (B),
robust JSON handling (C), the refresh-relay and switch-off notes (D, G), https-only discovery with uncacheable
protected-resource metadata (E), Warning-level refusal logs (F) and a strict token relay (G), plus the ARM state key.

**Architecture:** Every rule stays host-agnostic in `SafeExchange.Core\Mcp\OAuth` (new: `McpOAuthStateKey`,
`McpOAuthRateLimitOptions`, `SignedBrokerState`, `ConsentToken`, `ConsentCookie`, `ConsentPage`, `ClientRateLimiter`,
`EntraCallGate`, `McpOAuthThrottle`, `ClientAddress`) plus `SafeExchange.Core\Mcp\McpBaseUriGuard`; `McpOAuthBroker`
gains the consent flow and returns `McpOAuthResult` values that now carry a content type, a cookie and `Retry-After`.
`SafeExchange.Core.Functions\Mcp\OAuth\McpOAuthHandler` stays a thin adapter (cookie header in, client address in,
throttle first, headers out) and the authorize trigger accepts `GET` and `POST`.

**Tech Stack:** .NET 10, Azure Functions isolated worker (`HttpRequestData`), `System.Text.Json`,
`System.Security.Cryptography` (`HMACSHA256`, `CryptographicOperations.FixedTimeEquals`, `RandomNumberGenerator`),
`System.Buffers.Text.Base64Url`, NUnit 4 + Moq, ARM templates (+ `az bicep` for an optional expression check).

**Spec (authoritative):** `docs\superpowers\specs\2026-11-22-mcp-oauth-broker-hardening-design.md`.
**Background:** base design `docs\superpowers\specs\2026-11-22-mcp-oauth-broker-design.md`, the broker plan
`docs\superpowers\plans\2026-11-22-mcp-oauth-broker.md` and the master style rules in
`docs\superpowers\plans\2026-11-21-mcp-endpoint.md`.

**Verified baseline:** this plan was applied task by task, exactly as written, in a scratch worktree created from
`feat/mcp-oauth-broker` at **`47c47fb`** (`docs(spec): keep in-app throttling for the new anonymous broker endpoints`).
Results:
- every red step failed with the quoted compiler errors and every green step passed with the quoted counts;
- `dotnet build SafeExchange.slnx --nologo -v q --no-incremental` reports **0 errors and 704 warnings before and after**
  (no new warnings);
- task filter (`SafeExchange.Tests.Mcp`) **315 → 437**; phase filter (ground rule 6) **548 → 670** with the Cosmos
  emulator (without it exactly one test of that filter fails, see ground rule 6); the **215 contract tests pass** with
  one expected-methods line changed in `FunctionRouteTests`;
- full suite against the Cosmos emulator `mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-EN20260907pre`:
  **1281/1281 before, 1403/1403 after**;
- a real Functions host (`func start`, Core Tools 4.6.0, Azurite) with `Features__McpEndpoint=True`,
  `Mcp__OAuth__ClientId` and `Mcp__OAuth__StateKey` set served the consent page (`200`, the `Set-Cookie` and all
  security headers), redirected the allowed form to Entra without `prompt` and with a signed `state`, answered the
  `400` page to a form without the cookie and to a forged callback, answered `429` with `Retry-After` after the
  callback burst, served the protected-resource metadata with `Cache-Control: no-store`, and logged no nonce or
  consent token (Task 11 Step 5);
- the tasks produce 11 commits (Tasks 1–10, then Task 11 adds this plan).

---

## Ground rules

1. **Worktree.** Run every command from the root of a worktree on `feat/mcp-oauth-broker` at `47c47fb` or later
   (for example `C:\Users\yurio\Documents\github\safeexchange-wt-oauth`), in PowerShell 7. Never touch
   `C:\Users\yurio\Documents\github\safeexchange` (the main tree).
2. **Style** (master plan): file header `/// <summary>\n/// TypeName\n/// </summary>` before the namespace; block
   `namespace X { … }` with `using` directives inside it; `this.` prefix on members; constructor null guards
   `?? throw new ArgumentNullException(nameof(x))`; 4-space indentation; NUnit `Assert.That(..., Is....)`; comments only
   where a line needs explaining. The test project has **no implicit usings**. New files: UTF-8 without BOM.
3. **Frozen tests.** `SafeExchange.Tests\Contract\*` is not edited except the single expected line of
   `SafeExchange-OAuthAuthorize` in `FunctionRouteTests` (Task 4: `get` → `get,post`). Existing MCP/OAuth tests change
   only where a task shows the edit.
4. **Commits** after every task, staged by exact paths (never `git add -A`), message `feat(mcp-oauth): …` or
   `fix(mcp-oauth): …` followed by the trailer line `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`.
   The last commit (Task 11) adds this plan file.
5. **File locks.** If a build fails with `MSB3027`/`MSB3021`, a stale MSBuild node or `testhost` holds a `bin` DLL. Set
   `$env:MSBUILDDISABLENODEREUSE='1'` and pass `-nr:false`; never kill processes you did not start.
6. **Commands.**
   - Build: `dotnet build SafeExchange.slnx --nologo -v q`
   - Test build (red steps): `dotnet build SafeExchange.Tests\SafeExchange.Tests.csproj --nologo -v q`
   - Task filter (no emulator needed): `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.Mcp"`
   - Phase filter: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.Mcp|FullyQualifiedName~SafeExchange.Tests.Contract|FullyQualifiedName~Startup|FullyQualifiedName~Middleware|FullyQualifiedName~Route|FullyQualifiedName~WellKnown|FullyQualifiedName~ServiceRegistration"`.
     `~Route` also matches `SafeExchange.Tests.SecretStreamTests.HashedMode_IsMainContent_WithHeader_RoutedThroughLegacySanitiser`,
     which needs the Cosmos emulator: without it that one test fails in `OneTimeSetUp` with
     `HttpRequestException : No connection could be made … (localhost:8081)` and the summary reads
     `Failed: 1, Passed: <count − 1>`. That failure is expected and unrelated. (Without `|FullyQualifiedName~ServiceRegistration`
     the filter has 2 tests fewer.)
7. **Red steps** fail at compile time because the tests reference types or members the task adds; the expected
   failure is the quoted compiler error (first lines; paths relative to the repository root; line/column numbers may
   differ by a line if a file is formatted differently). Test summaries end with a duration that varies (`…`).
8. **Expected counts** (all green; phase filter with the emulator):

   | After | Task filter (`SafeExchange.Tests.Mcp`) | of which `SafeExchange.Tests.Mcp.OAuth` | Phase filter |
   |---|---|---|---|
   | Task 0 (baseline) | 315 | 200 | 548 |
   | Task 1 | 328 | 211 | 561 |
   | Task 2 | 351 | 234 | 584 |
   | Task 3 | 377 | 260 | 610 |
   | Task 4 | 379 | 262 | 612 |
   | Task 5 | 392 | 275 | 625 |
   | Task 6 | 410 | 293 | 643 |
   | Task 7 | 420 | 295 | 653 |
   | Task 8–10 | 437 | 312 | 670 |

## Decisions

- **D1 — consent flow.** `GET /api/oauth/authorize` validates exactly as before (unknown client or non-loopback
  redirect: `400` JSON, never redirected; other errors redirected to the client) and then answers the consent page
  (`200` HTML) with the consent cookie; Entra is not contacted. `POST /api/oauth/authorize` (same route) re-parses and
  re-validates the form like GET, then requires the cookie, a matching consent token and an unexpired form; any
  failure answers the `400` page *"This sign-in request is invalid or expired. Start the sign-in again from your MCP
  client."* and never redirects (including invalid parameters such as a foreign `redirect_uri`). `decision=allow` →
  `302` to Entra; `decision=deny` → `302` `error=access_denied` (+ client `state`) to the client and the cookie is
  expired; any other decision → `400` page.
- **D2 — form fields.** The page posts back, in this fixed order and only when present: `client_id`, `redirect_uri`,
  `response_type=code`, `code_challenge`, `code_challenge_method=S256`, `state`, `scope` (as received), `login_hint`,
  `consent_expires` (Unix seconds, now + 10 min), then `consent_token`. The spec's list (redirect_uri, state,
  code_challenge, scope, login_hint) is extended with `client_id`, `response_type` and `code_challenge_method` so the
  POST can be validated "exactly like GET", and with `consent_expires` so the 10-minute lifetime is enforced by the
  server and not only by the cookie's `Max-Age`. `prompt` and unknown parameters are never put into the form, and a
  `prompt` added to the POST is ignored (it is not read).
- **D3 — consent token.** `consent_token = base64url(HMAC-SHA256(key, "saex-mcp-consent\n" + nonce + "\n" + canonical))`,
  `canonical` = the fields above as `escape(name)=escape(value)` joined by `&` (`Uri.EscapeDataString`, so no value can
  forge a separator). The nonce (32 random bytes, base64url, 43 characters) travels only in the cookie, never in the
  page. Verification uses `CryptographicOperations.FixedTimeEquals`; the token is checked before the expiry so the
  expiry value is authenticated.
- **D4 — signed state.** `base64url(payloadJson) + "." + base64url(HMAC-SHA256(key, "saex-mcp-state\n" + payloadSegment))`
  with `payloadJson = {"r": redirect, "s": clientState|null, "n": nonce, "e": unixExpiry}`, expiry now + 10 min, at most
  2048 characters. The HMAC covers the encoded payload segment and is verified (fixed time) **before** the JSON is
  parsed; parsing is strict (exactly the four members, the right types, no duplicates, non-empty nonce), the redirect is
  re-validated as loopback, and `FormatException`, `JsonException` and `InvalidOperationException` (invalid UTF-16
  escapes) all mean "invalid". The two purpose prefixes keep a consent token from ever verifying as a state and vice
  versa. A `state` that does not fit is detected already at GET (a trial `TryCreate`) and redirected `invalid_request`.
- **D5 — the key.** `Mcp:OAuth:StateKey` = standard (padded) base64 of at least 32 bytes (`McpOAuthStateKey.TryDecode`).
  `McpProtectedResource.IsBrokerConfigured` now also needs a valid key, so without one the broker answers `404` and the
  protected-resource metadata names Entra, exactly like a missing client ID. ARM: `securestring` parameter
  `settings-mcp-oauthStateKey`, default `[base64(concat(newGuid(), newGuid()))]` (72 bytes). ARM documents that
  `newGuid()` may be used "within an expression for the default value of a parameter"; the Bicep compiler (0.46.1)
  accepts the decompiled `param … = base64(concat(newGuid(), newGuid()))` without BCP065, so the nested form is used
  (no fallback to a raw-string key is needed). The parameter is **not** added to the parameter files, so the default
  applies on every deployment and rotates the key; the Key Vault secret `Mcp--OAuth--StateKey` is written only when
  `settings-mcp-oauthClientId` is not empty (the client ID's own condition). Tests use the fixed key
  `AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=` (bytes 1–32).
- **D6 — the cookie.** `__Host-saex-mcp-consent=<nonce>; Max-Age=600; Path=/; Secure; HttpOnly; SameSite=Lax`
  (`ConsentCookie`). `Lax` is required: Entra's redirect to the callback is a cross-site top-level `GET`, which `Lax`
  still carries (the broker always asks Entra for `response_mode=query`). The adapter writes it as a `Set-Cookie`
  header and reads the `Cookie` request header(s) itself (`HttpRequestData.Cookies` is not populated by the test host
  and HTTP/2 may split `Cookie` into several fields, which are joined with `"; "`); the first occurrence of the exact,
  case-sensitive name wins. The callback compares the cookie with the state's nonce in fixed time and expires the cookie
  (`Max-Age=0`, empty value) on every redirect it issues; deny expires it too; allow keeps it for the callback.
- **D7 — client address.** The **last** `X-Forwarded-For` entry, port stripped (`ip:port`, `[ipv6]:port`), parsed and
  normalised with `IPAddress`; anything unparsable or absent → no address → the shared bucket `unknown`. This
  deliberately deviates from the spec's "first entry": the App Service front end *appends* the peer address, so the
  first entry is whatever the client sent and would let anyone pick a fresh bucket per request. The isolated worker's
  `HttpRequestData` exposes no connection address, so there is no further fallback. Behind another proxy (Front Door,
  APIM) the last entry would be that proxy — see Risks.
- **D8 — throttle.** `McpOAuthThrottle` (singleton) over `ClientRateLimiter` (token buckets, `lock`-protected dictionary
  + LRU list, at most `MaxClients` = 10 000 keys, clock passed in). Key = `endpoint|address`, endpoints `register`,
  `authorize` (GET and POST share it), `callback`, `token`. Defaults (`Mcp:OAuth:RateLimit`): token 30/min burst 30,
  the others 60/min burst 60; values below 1 count as 1. Over the limit: `register`/`token` → `429`
  `{"error":"slow_down",…}`, `authorize`/`callback` → `429` HTML page, all with `Retry-After` (whole seconds, at least 1)
  and one Warning without the address. The adapter checks the throttle first, but only while the broker is enabled
  (a disabled broker keeps answering a bare `404`). Metadata routes are not throttled (spec scope).
- **D9 — Entra call gate.** `EntraCallGate` = `SemaphoreSlim(MaxConcurrentEntraCalls = 16)`, wait 2 s; when no slot
  frees up the token request answers `503` `{"error":"temporarily_unavailable"}` with `Retry-After: 2` and a Warning.
  The slot is released in `finally`, also when Entra throws.
- **D10 — token relay.** Relayed unchanged only for status `200`, `400` or `401` **and** a body that parses as a JSON
  object; otherwise Warning `got an unexpected Entra response with status {StatusCode}` and `502`
  `{"error":"server_error"}`. A JSON object whose `error` has an invalid escape is still relayed (logged as
  `unrecognized`).
- **D11 — https only.** `McpBaseUriGuard` (singleton) allows `https`, or `http` when `Uri.IsLoopback`
  (`localhost`, `127.0.0.0/8`, `[::1]`); anything else answers `404` from every broker endpoint and from the
  protected-resource metadata, with one Warning per instance (`Interlocked` flag). `Register` and `Callback` therefore
  gain the `apiBaseUri` parameter. The protected-resource metadata also gets `Cache-Control: no-store` (the handler
  serves both `/api/.well-known/oauth-protected-resource` and the root `/.well-known/oauth-protected-resource/api/mcp`).
- **D12 — robust input.** Register reads strings through `TryGetString`, which treats `InvalidOperationException`
  (lone surrogates such as `"\uD800"`) as malformed → `400`. The adapter decodes bodies with a throwing `UTF8Encoding`;
  invalid UTF-8 reads as `null`, which every POST endpoint refuses like an empty body.
- **D13 — logging.** `Refuse` and `RedirectError` log at Warning; the new refusals use
  `MCP OAuth {Endpoint} refused the request: {Reason}.` with fixed reasons (`invalid_form`, `invalid_parameters`,
  `missing_cookie`, `invalid_consent_token`, `expired`, `invalid_decision`, `invalid_state`, `nonce_mismatch`). The
  Entra-unreachable Warning adds `HttpRequestException.HttpRequestError` (or `none`) and the inner exception type name.
  Nothing logs codes, verifiers, tokens, state, cookies, nonces, consent tokens or client addresses.
- **D14 — clock.** The pure types take the time as a parameter (`SignedBrokerState.TryCreate/TryVerify`,
  `ClientRateLimiter.TryAcquire`); the broker and the throttle pass `DateTimeProvider.UtcNow`. The one broker test that
  moves time saves and restores `DateTimeProvider.UseSpecifiedDateTime`/`SpecifiedDateTime` in `finally` (tests run
  sequentially; nothing in the test project is `[Parallelizable]`).

## File structure

| File | Responsibility |
|---|---|
| Create `SafeExchange.Core\Mcp\OAuth\McpOAuthRateLimitOptions.cs` | `Mcp:OAuth:RateLimit` settings and defaults |
| Create `SafeExchange.Core\Mcp\OAuth\McpOAuthStateKey.cs` | Decode/validate the base64 key (≥ 32 bytes) |
| Create `SafeExchange.Core\Mcp\OAuth\SignedBrokerState.cs` | HMAC-signed state codec with expiry (replaces `BrokerState`) |
| Create `SafeExchange.Core\Mcp\OAuth\ConsentToken.cs` | Nonce generation, consent-token HMAC and fixed-time check |
| Create `SafeExchange.Core\Mcp\OAuth\ConsentCookie.cs` | `__Host-` cookie value object, `Set-Cookie` text, `Cookie` header parsing |
| Create `SafeExchange.Core\Mcp\OAuth\ConsentPage.cs` | Server-rendered consent and message pages, security headers |
| Create `SafeExchange.Core\Mcp\OAuth\ClientRateLimiter.cs` | Bounded LRU token buckets |
| Create `SafeExchange.Core\Mcp\OAuth\EntraCallGate.cs` | Concurrency cap for outbound Entra calls |
| Create `SafeExchange.Core\Mcp\OAuth\McpOAuthThrottle.cs` | Per-endpoint/per-address policy and 429 answers |
| Create `SafeExchange.Core\Mcp\OAuth\ClientAddress.cs` | Client address from `X-Forwarded-For` |
| Create `SafeExchange.Core\Mcp\McpBaseUriGuard.cs` | https-or-loopback rule with a once-per-instance Warning |
| Modify `SafeExchange.Core\Mcp\OAuth\McpOAuthOptions.cs` | `StateKey`, `RateLimit` |
| Modify `SafeExchange.Core\Mcp\OAuth\McpOAuthResult.cs` | Content type, cookie, `Retry-After`, `Html(...)` |
| Modify `SafeExchange.Core\Mcp\OAuth\McpOAuthBroker.cs` | Consent flow, signed state, gate, relay rule, guard, logging |
| Delete `SafeExchange.Core\Mcp\OAuth\BrokerState.cs` | Replaced by `SignedBrokerState` |
| Modify `SafeExchange.Core\Mcp\McpProtectedResource.cs` | Broker configured only with a valid key |
| Modify `SafeExchange.Core\Mcp\McpServiceRegistration.cs` | Register guard, gate and throttle |
| Modify `SafeExchange.Core.Functions\Mcp\OAuth\McpOAuthHandler.cs` | POST authorize, cookies, client address, throttle, headers, strict UTF-8 |
| Modify `SafeExchange.Core.Functions\Mcp\OAuthProtectedResourceHandler.cs` | Guard + `no-store` |
| Modify `SafeExchange.Functions\Functions\SafeMcpOAuth.cs` | Authorize trigger accepts `get` and `post` |
| Modify `deployment\current\arm\services-template.arm.json` | `settings-mcp-oauthStateKey`, `Mcp--OAuth--StateKey` |
| Modify `docs\mcp.md`, `docs\api-endpoints.md` | Consent UX, rate limits, https, switch-off, egress note, local dev |
| Tests: 15 new files under `SafeExchange.Tests\Mcp\` and `SafeExchange.Tests\Mcp\OAuth\`; `BrokerStateTests.cs` deleted; existing OAuth tests, `McpMetadataTests`, `McpProtectedResourceTests` and one line of `FunctionRouteTests` edited | |

---

## Task 0: Baseline

**Files:** none.

- [ ] **Step 1: Check the worktree**

Run: `git status --short; git --no-pager log --oneline -1`
Expected: no output from `git status`, and `47c47fb docs(spec): keep in-app throttling for the new anonymous broker endpoints`
(or a later commit on `feat/mcp-oauth-broker`).

- [ ] **Step 2: Build and record the warning count**

Run: `dotnet build SafeExchange.slnx --nologo -v q --no-incremental`
Expected: ends with `704 Warning(s)` and `0 Error(s)`.

- [ ] **Step 3: Run the task and phase filters**

Run the task filter, then the phase filter (ground rule 6).
Expected: `Passed!  - Failed:     0, Passed:   315, Skipped:     0, Total:   315, …` and, with the emulator,
`Passed:   548` (without it: `Failed:     1, Passed:   547`, the one test named in ground rule 6). The contract tests
alone (`--filter "FullyQualifiedName~SafeExchange.Tests.Contract"`): `Passed:   215`.

- [ ] **Step 4 (optional): full suite** — see Task 11 Step 3 for the emulator setup. Expected `Passed:  1281`.

---

## Task 1: State key and rate-limit options; the broker needs a valid key

The broker becomes "configured" only with `Mcp:OAuth:StateKey` (D5). Test bases get the fixed test key so every
existing broker test keeps running against a configured broker.

**Files:**
- Modify: `SafeExchange.Core\Mcp\McpProtectedResource.cs`
- Modify: `SafeExchange.Core\Mcp\OAuth\McpOAuthOptions.cs`
- Create: `SafeExchange.Core\Mcp\OAuth\McpOAuthRateLimitOptions.cs`
- Create: `SafeExchange.Core\Mcp\OAuth\McpOAuthStateKey.cs`
- Test (modify): `SafeExchange.Tests\Mcp\McpMetadataTests.cs`
- Test (modify): `SafeExchange.Tests\Mcp\McpProtectedResourceTests.cs`
- Test (modify): `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerGatingTests.cs`
- Test (modify): `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTestBase.cs`
- Test (modify): `SafeExchange.Tests\Mcp\OAuth\McpOAuthHttpTestBase.cs`
- Test (modify): `SafeExchange.Tests\Mcp\OAuth\McpOAuthOptionsTests.cs`
- Test (create): `SafeExchange.Tests\Mcp\OAuth\McpOAuthStateKeyTests.cs`
- Test (create): `SafeExchange.Tests\Mcp\OAuth\McpOAuthTestKey.cs`

- [ ] **Step 1: Write the failing tests**

Modify `SafeExchange.Tests\Mcp\McpMetadataTests.cs` (2 edits):

Edit 1 of 2. Replace:

```csharp
namespace SafeExchange.Tests.Mcp
{
    using NUnit.Framework;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Net;
```

with:

```csharp
namespace SafeExchange.Tests.Mcp
{
    using NUnit.Framework;
    using SafeExchange.Tests.Mcp.OAuth;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Net;
```

Edit 2 of 2. Replace:

```csharp
        [Test]
        public async Task Metadata_WithTheBrokerConfigured_NamesThisHostAsTheAuthorizationServer()
        {
            this.BuildProvider(("Mcp:OAuth:ClientId", "00000000-0000-0000-0000-0000000000c9"));

            var response = await this.MetadataHandler.Run(MetadataRequest());
```

with:

```csharp
        [Test]
        public async Task Metadata_WithTheBrokerConfigured_NamesThisHostAsTheAuthorizationServer()
        {
            this.BuildProvider(("Mcp:OAuth:ClientId", "00000000-0000-0000-0000-0000000000c9"), ("Mcp:OAuth:StateKey", McpOAuthTestKey.Value));

            var response = await this.MetadataHandler.Run(MetadataRequest());
```

Modify `SafeExchange.Tests\Mcp\McpProtectedResourceTests.cs` (2 edits):

Edit 1 of 2. Replace:

```csharp
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Mcp.OAuth;
    using System;

    [TestFixture]
```

with:

```csharp
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Mcp.OAuth;
    using SafeExchange.Tests.Mcp.OAuth;
    using System;

    [TestFixture]
```

Edit 2 of 2. Replace:

```csharp
            Assert.That(metadata.ScopesSupported, Is.EqualTo(new[] { "api://safeexchange.test/user_impersonation" }));
        }

        [TestCase("", Authority)]
        [TestCase("   ", Authority)]
        [TestCase(BrokerClientId, "")]
        [TestCase(BrokerClientId, "http://login.microsoftonline.com/00000000-0000-0000-0000-000000000001")]
        public void TheBroker_NeedsAClientIdAndAnHttpsAuthority(string clientId, string authority)
        {
            var resource = Create(new AuthenticationConfiguration { Authority = authority, ValidAudiences = "api://safeexchange.test" }, clientId);

            Assert.That(resource.IsBrokerConfigured, Is.False);
            Assert.That(resource.ToMetadata(ApiBase).AuthorizationServers, Does.Not.Contain("https://safeexchange.test/api"));
        }

        private static McpProtectedResource Create(AuthenticationConfiguration configuration, string clientId = "")
            => new(
                Mock.Of<IOptionsMonitor<AuthenticationConfiguration>>(x => x.CurrentValue == configuration),
                Mock.Of<IOptionsMonitor<McpOAuthOptions>>(x => x.CurrentValue == new McpOAuthOptions { ClientId = clientId }));
    }
}
```

with:

```csharp
            Assert.That(metadata.ScopesSupported, Is.EqualTo(new[] { "api://safeexchange.test/user_impersonation" }));
        }

        [TestCase("", Authority, McpOAuthTestKey.Value)]
        [TestCase("   ", Authority, McpOAuthTestKey.Value)]
        [TestCase(BrokerClientId, "", McpOAuthTestKey.Value)]
        [TestCase(BrokerClientId, "http://login.microsoftonline.com/00000000-0000-0000-0000-000000000001", McpOAuthTestKey.Value)]
        [TestCase(BrokerClientId, Authority, "")]
        [TestCase(BrokerClientId, Authority, "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHw==")]
        public void TheBroker_NeedsAClientIdAnHttpsAuthorityAndAStateKey(string clientId, string authority, string stateKey)
        {
            var resource = Create(new AuthenticationConfiguration { Authority = authority, ValidAudiences = "api://safeexchange.test" }, clientId, stateKey);

            Assert.That(resource.IsBrokerConfigured, Is.False);
            Assert.That(resource.ToMetadata(ApiBase).AuthorizationServers, Does.Not.Contain("https://safeexchange.test/api"));
        }

        private static McpProtectedResource Create(AuthenticationConfiguration configuration, string clientId = "", string stateKey = McpOAuthTestKey.Value)
            => new(
                Mock.Of<IOptionsMonitor<AuthenticationConfiguration>>(x => x.CurrentValue == configuration),
                Mock.Of<IOptionsMonitor<McpOAuthOptions>>(x => x.CurrentValue == new McpOAuthOptions { ClientId = clientId, StateKey = stateKey }));
    }
}
```

Modify `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerGatingTests.cs`:

Replace:

```csharp

    /// <summary>
    /// Every broker endpoint answers a bare 404 unless <c>Features.McpEndpoint</c> is on, <c>Mcp:OAuth:ClientId</c> is
    /// set and <c>Authentication:Authority</c> gives Entra endpoints.
    /// </summary>
    [TestFixture]
    public class McpOAuthBrokerGatingTests : McpOAuthBrokerTestBase
    {
        [TestCase(false, BrokerClientId, Tenant)]
        [TestCase(true, "", Tenant)]
        [TestCase(true, "   ", Tenant)]
        [TestCase(true, BrokerClientId, "")]
        public async Task EveryEndpoint_IsNotFound_UnlessTheBrokerIsFullyConfigured(bool mcpEnabled, string clientId, string authority)
        {
            var broker = this.CreateBroker(mcpEnabled, clientId, authority);

            Assert.That(broker.IsEnabled, Is.False);
            foreach (var result in await AllEndpointsAsync(broker))
```

with:

```csharp

    /// <summary>
    /// Every broker endpoint answers a bare 404 unless <c>Features.McpEndpoint</c> is on, <c>Mcp:OAuth:ClientId</c> is
    /// set, <c>Mcp:OAuth:StateKey</c> is a valid key and <c>Authentication:Authority</c> gives Entra endpoints.
    /// </summary>
    [TestFixture]
    public class McpOAuthBrokerGatingTests : McpOAuthBrokerTestBase
    {
        [TestCase(false, BrokerClientId, Tenant, McpOAuthTestKey.Value)]
        [TestCase(true, "", Tenant, McpOAuthTestKey.Value)]
        [TestCase(true, "   ", Tenant, McpOAuthTestKey.Value)]
        [TestCase(true, BrokerClientId, "", McpOAuthTestKey.Value)]
        [TestCase(true, BrokerClientId, Tenant, "")]
        [TestCase(true, BrokerClientId, Tenant, "dG9vIHNob3J0")]
        public async Task EveryEndpoint_IsNotFound_UnlessTheBrokerIsFullyConfigured(bool mcpEnabled, string clientId, string authority, string stateKey)
        {
            var broker = this.CreateBroker(mcpEnabled, clientId, authority, stateKey);

            Assert.That(broker.IsEnabled, Is.False);
            foreach (var result in await AllEndpointsAsync(broker))
```

Modify `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTestBase.cs`:

Replace:

```csharp
            this.Broker = this.CreateBroker();
        }

        protected McpOAuthBroker CreateBroker(bool mcpEnabled = true, string clientId = BrokerClientId, string authority = Tenant)
        {
            var authentication = Mock.Of<IOptionsMonitor<AuthenticationConfiguration>>(x => x.CurrentValue == new AuthenticationConfiguration
            {
                Authority = authority,
                ValidAudiences = "00000000-0000-0000-0000-0000000000e1,api://safeexchange.test",
            });
            var oauth = Mock.Of<IOptionsMonitor<McpOAuthOptions>>(x => x.CurrentValue == new McpOAuthOptions { ClientId = clientId });
            var features = Mock.Of<IOptionsMonitor<Features>>(x => x.CurrentValue == new Features { McpEndpoint = mcpEnabled });
            var loggerFactory = new LoggerFactory(new[] { this.Logs });
            return new McpOAuthBroker(new McpProtectedResource(authentication, oauth), oauth, authentication, features, this.Entra, loggerFactory.CreateLogger<McpOAuthBroker>());
```

with:

```csharp
            this.Broker = this.CreateBroker();
        }

        protected McpOAuthBroker CreateBroker(bool mcpEnabled = true, string clientId = BrokerClientId, string authority = Tenant, string stateKey = McpOAuthTestKey.Value)
        {
            var authentication = Mock.Of<IOptionsMonitor<AuthenticationConfiguration>>(x => x.CurrentValue == new AuthenticationConfiguration
            {
                Authority = authority,
                ValidAudiences = "00000000-0000-0000-0000-0000000000e1,api://safeexchange.test",
            });
            var oauth = Mock.Of<IOptionsMonitor<McpOAuthOptions>>(x => x.CurrentValue == new McpOAuthOptions { ClientId = clientId, StateKey = stateKey });
            var features = Mock.Of<IOptionsMonitor<Features>>(x => x.CurrentValue == new Features { McpEndpoint = mcpEnabled });
            var loggerFactory = new LoggerFactory(new[] { this.Logs });
            return new McpOAuthBroker(new McpProtectedResource(authentication, oauth), oauth, authentication, features, this.Entra, loggerFactory.CreateLogger<McpOAuthBroker>());
```

Modify `SafeExchange.Tests\Mcp\OAuth\McpOAuthHttpTestBase.cs` (2 edits):

Edit 1 of 2. Replace:

```csharp
    using System.Text;

    /// <summary>
    /// <see cref="McpHttpTestBase"/> with the broker configured (<c>Mcp:OAuth:ClientId</c>), the
    /// <see cref="McpOAuthHandler"/> adapter and a <see cref="FakeEntraTokenClient"/> in place of Entra.
    /// </summary>
    public abstract class McpOAuthHttpTestBase : McpHttpTestBase
    {
```

with:

```csharp
    using System.Text;

    /// <summary>
    /// <see cref="McpHttpTestBase"/> with the broker configured (<c>Mcp:OAuth:ClientId</c> and <c>Mcp:OAuth:StateKey</c>),
    /// the <see cref="McpOAuthHandler"/> adapter and a <see cref="FakeEntraTokenClient"/> in place of Entra.
    /// </summary>
    public abstract class McpOAuthHttpTestBase : McpHttpTestBase
    {
```

Edit 2 of 2. Replace:

```csharp
        [SetUp]
        public void OAuthSetUp()
        {
            this.BuildProvider(("Mcp:OAuth:ClientId", BrokerClientId));
        }

        protected override void ConfigureServices(IServiceCollection services)
```

with:

```csharp
        [SetUp]
        public void OAuthSetUp()
        {
            this.BuildProvider(("Mcp:OAuth:ClientId", BrokerClientId), ("Mcp:OAuth:StateKey", McpOAuthTestKey.Value));
        }

        protected override void ConfigureServices(IServiceCollection services)
```

Modify `SafeExchange.Tests\Mcp\OAuth\McpOAuthOptionsTests.cs`:

Replace:

```csharp
            using var provider = services.BuildServiceProvider();
            Assert.That(provider.GetRequiredService<IOptionsMonitor<McpOAuthOptions>>().CurrentValue.ClientId, Is.EqualTo("00000000-0000-0000-0000-0000000000c9"));
        }
    }
}
```

with:

```csharp
            using var provider = services.BuildServiceProvider();
            Assert.That(provider.GetRequiredService<IOptionsMonitor<McpOAuthOptions>>().CurrentValue.ClientId, Is.EqualTo("00000000-0000-0000-0000-0000000000c9"));
        }

        [Test]
        public void ConfigureServices_BindsTheStateKeyAndTheRateLimits()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "CosmosDb:CosmosDbEndpoint", "https://localhost:8081/" },
                    { "CosmosDb:DatabaseName", "startup-test" },
                    { "Mcp:OAuth:StateKey", McpOAuthTestKey.Value },
                    { "Mcp:OAuth:RateLimit:TokenPermitsPerMinute", "5" },
                    { "Mcp:OAuth:RateLimit:MaxConcurrentEntraCalls", "4" },
                })
                .Build();
            var services = new ServiceCollection();

            SafeExchangeStartup.ConfigureServices(configuration, services);

            using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<IOptionsMonitor<McpOAuthOptions>>().CurrentValue;
            Assert.That(options.StateKey, Is.EqualTo(McpOAuthTestKey.Value));
            Assert.That(options.RateLimit.TokenPermitsPerMinute, Is.EqualTo(5));
            Assert.That(options.RateLimit.MaxConcurrentEntraCalls, Is.EqualTo(4));
            Assert.That(options.RateLimit.TokenBurst, Is.EqualTo(30));
        }
    }
}
```

Create `SafeExchange.Tests\Mcp\OAuth\McpOAuthStateKeyTests.cs`:

```csharp
/// <summary>
/// McpOAuthStateKeyTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Linq;
    using System.Text;

    [TestFixture]
    public class McpOAuthStateKeyTests
    {
        [Test]
        public void ABase64KeyOf32Bytes_IsDecoded()
        {
            Assert.That(McpOAuthStateKey.TryDecode(McpOAuthTestKey.Value, out var key), Is.True);
            Assert.That(key, Is.EqualTo(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()));
        }

        // What the ARM default [base64(concat(newGuid(), newGuid()))] produces: 72 ASCII bytes.
        [Test]
        public void TheArmDefaultShape_IsAccepted()
        {
            var value = Convert.ToBase64String(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString() + Guid.NewGuid().ToString()));

            Assert.That(McpOAuthStateKey.TryDecode(value, out var key), Is.True);
            Assert.That(key!.Length, Is.EqualTo(72));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHw==")]
        [TestCase("not base64 at all, but long enough to be 32 characters")]
        [TestCase("AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA")]
        public void AnythingElse_IsRejected(string? value)
        {
            Assert.That(McpOAuthStateKey.TryDecode(value, out var key), Is.False);
            Assert.That(key, Is.Null);
        }
    }
}
```

Create `SafeExchange.Tests\Mcp\OAuth\McpOAuthTestKey.cs`:

```csharp
/// <summary>
/// McpOAuthTestKey
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using System;

    /// <summary>
    /// The fixed broker state key of the tests (<c>Mcp:OAuth:StateKey</c>): base64 of the 32 bytes 0x01 to 0x20.
    /// </summary>
    public static class McpOAuthTestKey
    {
        public const string Value = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=";

        public static byte[] Bytes => Convert.FromBase64String(Value);
    }
}
```


- [ ] **Step 2: Run the test build to verify it fails**

Run: `dotnet build SafeExchange.Tests\SafeExchange.Tests.csproj --nologo -v q`
Expected: FAIL, first errors:

```
SafeExchange.Tests\Mcp\OAuth\McpOAuthStateKeyTests.cs(19,25): error CS0103: The name 'McpOAuthStateKey' does not exist in the current context
SafeExchange.Tests\Mcp\OAuth\McpOAuthOptionsTests.cs(56,33): error CS1061: 'McpOAuthOptions' does not contain a definition for 'StateKey' and no accessible extension method 'StateKey' accepting a first argument of type 'McpOAuthOptions' could be found (are you missing a using directive or an assembly reference?)
SafeExchange.Tests\Mcp\OAuth\McpOAuthOptionsTests.cs(57,33): error CS1061: 'McpOAuthOptions' does not contain a definition for 'RateLimit' …
```

- [ ] **Step 3: Implement**

Modify `SafeExchange.Core\Mcp\McpProtectedResource.cs`:

Replace:

```csharp
        }

        /// <summary>
        /// True when <c>Mcp:OAuth:ClientId</c> is set and <c>Authentication:Authority</c> gives Entra endpoints.
        /// </summary>
        public bool IsBrokerConfigured
            => !string.IsNullOrWhiteSpace(this.oauth.CurrentValue.ClientId)
                && EntraEndpoints.FromAuthority(this.authentication.CurrentValue.Authority) is not null;

        /// <summary>
```

with:

```csharp
        }

        /// <summary>
        /// True when <c>Mcp:OAuth:ClientId</c> is set, <c>Mcp:OAuth:StateKey</c> is a valid key and
        /// <c>Authentication:Authority</c> gives Entra endpoints.
        /// </summary>
        public bool IsBrokerConfigured
            => !string.IsNullOrWhiteSpace(this.oauth.CurrentValue.ClientId)
                && McpOAuthStateKey.TryDecode(this.oauth.CurrentValue.StateKey, out _)
                && EntraEndpoints.FromAuthority(this.authentication.CurrentValue.Authority) is not null;

        /// <summary>
```

Replace the whole content of `SafeExchange.Core\Mcp\OAuth\McpOAuthOptions.cs` with:

```csharp
/// <summary>
/// McpOAuthOptions
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    /// <summary>
    /// Binds to the <c>Mcp:OAuth</c> configuration section (Key Vault <c>Mcp--OAuth--ClientId</c>,
    /// <c>Mcp--OAuth--StateKey</c>). The OAuth broker for MCP clients is configured only when <see cref="ClientId"/> is
    /// set and <see cref="StateKey"/> is a valid key (<see cref="McpOAuthStateKey"/>).
    /// </summary>
    public sealed class McpOAuthOptions
    {
        public const string SectionName = "Mcp:OAuth";

        /// <summary>
        /// Application (client) ID of the Entra public client app the broker signs users in with.
        /// </summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>
        /// Base64 HMAC key (at least 32 bytes) that signs the consent token and the state sent to Entra.
        /// </summary>
        public string StateKey { get; set; } = string.Empty;

        public McpOAuthRateLimitOptions RateLimit { get; set; } = new();
    }
}
```

Create `SafeExchange.Core\Mcp\OAuth\McpOAuthRateLimitOptions.cs`:

```csharp
/// <summary>
/// McpOAuthRateLimitOptions
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    /// <summary>
    /// <c>Mcp:OAuth:RateLimit</c>: per-instance, per-client-address token buckets for the anonymous broker endpoints
    /// and the cap on concurrent outbound Entra token calls. Values below 1 count as 1.
    /// </summary>
    public sealed class McpOAuthRateLimitOptions
    {
        public int TokenPermitsPerMinute { get; set; } = 30;

        public int TokenBurst { get; set; } = 30;

        /// <summary>
        /// Refill rate of the register, authorize and callback buckets.
        /// </summary>
        public int PermitsPerMinute { get; set; } = 60;

        public int Burst { get; set; } = 60;

        /// <summary>
        /// Most buckets kept per instance; the least recently used one is dropped beyond that.
        /// </summary>
        public int MaxClients { get; set; } = 10000;

        public int MaxConcurrentEntraCalls { get; set; } = 16;
    }
}
```

Create `SafeExchange.Core\Mcp\OAuth\McpOAuthStateKey.cs`:

```csharp
/// <summary>
/// McpOAuthStateKey
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;
    using System.Diagnostics.CodeAnalysis;

    /// <summary>
    /// Decodes <c>Mcp:OAuth:StateKey</c>: standard (padded) base64 of at least <see cref="MinLength"/> bytes. The ARM
    /// default <c>[base64(concat(newGuid(), newGuid()))]</c> decodes to 72 bytes.
    /// </summary>
    public static class McpOAuthStateKey
    {
        public const int MinLength = 32;

        public static bool TryDecode(string? value, [NotNullWhen(true)] out byte[]? key)
        {
            key = null;
            var text = (value ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return false;
            }

            var buffer = new byte[text.Length];
            if (!Convert.TryFromBase64String(text, buffer, out var written) || written < MinLength)
            {
                return false;
            }

            key = buffer[..written];
            return true;
        }
    }
}
```


- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build SafeExchange.slnx --nologo -v q`, then the task filter.
Expected: `0 Error(s)`; `Passed!  - Failed:     0, Passed:   328, Skipped:     0, Total:   328, …`.

- [ ] **Step 5: Commit**

```powershell
git add SafeExchange.Core\Mcp\McpProtectedResource.cs SafeExchange.Core\Mcp\OAuth\McpOAuthOptions.cs SafeExchange.Core\Mcp\OAuth\McpOAuthRateLimitOptions.cs SafeExchange.Core\Mcp\OAuth\McpOAuthStateKey.cs SafeExchange.Tests\Mcp\McpMetadataTests.cs SafeExchange.Tests\Mcp\McpProtectedResourceTests.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerGatingTests.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTestBase.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthHttpTestBase.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthOptionsTests.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthStateKeyTests.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthTestKey.cs
git commit -m "feat(mcp-oauth): state key and rate-limit options; the broker needs a valid state key" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
git status --short
```


---

## Task 2: Signed broker state

`SignedBrokerState` is the new, signed `state` for Entra (D4). It is added next to `BrokerState`; Task 4 switches the
broker over and deletes `BrokerState`.

**Files:**
- Create: `SafeExchange.Core\Mcp\OAuth\SignedBrokerState.cs`
- Test (create): `SafeExchange.Tests\Mcp\OAuth\SignedBrokerStateTests.cs`

- [ ] **Step 1: Write the failing tests**

The tests build signed payloads themselves (`Sign`) to pin the documented format and to reach the strict parser with
crafted JSON, including a lone surrogate (spec item C).

Create `SafeExchange.Tests\Mcp\OAuth\SignedBrokerStateTests.cs`:

```csharp
/// <summary>
/// SignedBrokerStateTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Buffers.Text;
    using System.Security.Cryptography;
    using System.Text;

    [TestFixture]
    public class SignedBrokerStateTests
    {
        private const string Nonce = "bm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm8";

        private static readonly DateTime Now = new(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);

        private static readonly long Expiry = new DateTimeOffset(Now).ToUnixTimeSeconds() + 600;

        [TestCase("http://127.0.0.1:53682/callback", "client-state-123")]
        [TestCase("http://localhost:8080/cb?x=1", "with spaces, \"quotes\", ünïcödé & =+/.")]
        [TestCase("http://[::1]:1234/", null)]
        public void Create_ThenVerify_RoundTrips_WithATenMinuteExpiry(string redirectUri, string? clientState)
        {
            Assert.That(SignedBrokerState.TryCreate(McpOAuthTestKey.Bytes, redirectUri, clientState, Nonce, Now, out var encoded), Is.True);

            Assert.That(encoded, Does.Match("^[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]{43}$"), "base64url payload, a dot and a base64url HMAC-SHA256");
            Assert.That(SignedBrokerState.TryVerify(McpOAuthTestKey.Bytes, encoded, Now, out var state), Is.True);
            Assert.That(state, Is.EqualTo(new SignedBrokerState(redirectUri, clientState, Nonce, Expiry)));
        }

        [Test]
        public void TheState_IsValidForTenMinutes()
        {
            Assert.That(SignedBrokerState.TryCreate(McpOAuthTestKey.Bytes, "http://127.0.0.1:1/", "s", Nonce, Now, out var encoded), Is.True);

            Assert.That(SignedBrokerState.TryVerify(McpOAuthTestKey.Bytes, encoded, Now.AddSeconds(599), out _), Is.True);
            Assert.That(SignedBrokerState.TryVerify(McpOAuthTestKey.Bytes, encoded, Now.AddSeconds(600), out var expired), Is.False);
            Assert.That(expired, Is.Null);
        }

        [Test]
        public void ATamperedPayloadOrSignature_OrAnotherKey_IsRejected()
        {
            Assert.That(SignedBrokerState.TryCreate(McpOAuthTestKey.Bytes, "http://127.0.0.1:1/", "s", Nonce, Now, out var encoded), Is.True);
            var dot = encoded!.IndexOf('.');
            var otherKey = new byte[32];

            Assert.That(SignedBrokerState.TryVerify(McpOAuthTestKey.Bytes, Flip(encoded, 3), Now, out _), Is.False);
            Assert.That(SignedBrokerState.TryVerify(McpOAuthTestKey.Bytes, Flip(encoded, dot + 5), Now, out _), Is.False);
            Assert.That(SignedBrokerState.TryVerify(otherKey, encoded, Now, out _), Is.False);
            Assert.That(SignedBrokerState.TryVerify(McpOAuthTestKey.Bytes, Sign(otherKey, Payload("http://127.0.0.1:1/")), Now, out _), Is.False);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("eyJyIjoiaHR0cDovLzEyNy4wLjAuMToxLyJ9")]
        [TestCase("not base64url!.c2ln")]
        [TestCase("a.b.c")]
        [TestCase(".")]
        public void MissingUnsignedOrMalformedValues_AreRejected(string? encoded)
        {
            Assert.That(SignedBrokerState.TryVerify(McpOAuthTestKey.Bytes, encoded, Now, out var state), Is.False);
            Assert.That(state, Is.Null);
        }

        [TestCase("not json")]
        [TestCase("[]")]
        [TestCase("{}")]
        [TestCase("{\"r\":\"http://127.0.0.1:1/\",\"s\":null,\"n\":\"" + Nonce + "\"}")]
        [TestCase("{\"r\":\"http://127.0.0.1:1/\",\"s\":null,\"n\":\"" + Nonce + "\",\"e\":\"1795342200\"}")]
        [TestCase("{\"r\":\"http://127.0.0.1:1/\",\"s\":2,\"n\":\"" + Nonce + "\",\"e\":1795342200}")]
        [TestCase("{\"r\":\"http://127.0.0.1:1/\",\"s\":null,\"n\":\"" + Nonce + "\",\"e\":1795342200,\"x\":\"extra\"}")]
        [TestCase("{\"r\":\"http://127.0.0.1:1/\",\"r\":\"http://127.0.0.1:2/\",\"s\":null,\"n\":\"" + Nonce + "\",\"e\":1795342200}")]
        [TestCase("{\"r\":\"https://evil.example/cb\",\"s\":null,\"n\":\"" + Nonce + "\",\"e\":1795342200}")]
        [TestCase("{\"r\":\"http://127.0.0.1:1/\",\"s\":\"\\uD800\",\"n\":\"" + Nonce + "\",\"e\":1795342200}")]
        [TestCase("{\"r\":\"http://127.0.0.1:1/\",\"s\":null,\"n\":\"\",\"e\":1795342200}")]
        public void ASignedButMalformedPayload_IsRejected(string json)
        {
            Assert.That(SignedBrokerState.TryVerify(McpOAuthTestKey.Bytes, Sign(McpOAuthTestKey.Bytes, json), Now, out _), Is.False);
        }

        [Test]
        public void TheEncodedState_IsAtMost2KB()
        {
            Assert.That(SignedBrokerState.TryCreate(McpOAuthTestKey.Bytes, "http://127.0.0.1:1/", new string('s', 1000), Nonce, Now, out var fits), Is.True);
            Assert.That(fits!.Length, Is.LessThanOrEqualTo(SignedBrokerState.MaxEncodedLength));
            Assert.That(SignedBrokerState.TryCreate(McpOAuthTestKey.Bytes, "http://127.0.0.1:1/", new string('s', 1600), Nonce, Now, out var none), Is.False);
            Assert.That(none, Is.Null);

            var oversize = Sign(McpOAuthTestKey.Bytes, Payload("http://127.0.0.1:1/", new string('s', 1600)));
            Assert.That(oversize.Length, Is.GreaterThan(SignedBrokerState.MaxEncodedLength));
            Assert.That(SignedBrokerState.TryVerify(McpOAuthTestKey.Bytes, oversize, Now, out _), Is.False);
        }

        private static string Payload(string redirectUri, string clientState = "s")
            => $"{{\"r\":\"{redirectUri}\",\"s\":\"{clientState}\",\"n\":\"{Nonce}\",\"e\":{Expiry}}}";

        // The documented format: base64url(payload) "." base64url(HMAC-SHA256(key, "saex-mcp-state\n" + payload segment)).
        private static string Sign(byte[] key, string json)
        {
            var payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
            var signature = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("saex-mcp-state\n" + payload));
            return payload + "." + Base64Url.EncodeToString(signature);
        }

        private static string Flip(string value, int index)
            => value[..index] + (value[index] == 'A' ? 'B' : 'A') + value[(index + 1)..];
    }
}
```


- [ ] **Step 2: Run the test build to verify it fails**

Run: `dotnet build SafeExchange.Tests\SafeExchange.Tests.csproj --nologo -v q`
Expected: FAIL, first error:

```
SafeExchange.Tests\Mcp\OAuth\SignedBrokerStateTests.cs(28,25): error CS0103: The name 'SignedBrokerState' does not exist in the current context
```

- [ ] **Step 3: Implement**

Create `SafeExchange.Core\Mcp\OAuth\SignedBrokerState.cs`:

```csharp
/// <summary>
/// SignedBrokerState
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;
    using System.Buffers.Text;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;

    /// <summary>
    /// The <c>state</c> the broker sends to Entra: <c>base64url(payload) + "." + base64url(HMAC-SHA256)</c> with
    /// payload <c>{"r": clientRedirect, "s": clientState|null, "n": consentNonce, "e": unixExpiry}</c>, valid for
    /// <see cref="Lifetime"/> and at most <see cref="MaxEncodedLength"/> characters. The HMAC (key
    /// <c>Mcp:OAuth:StateKey</c>) covers <c>"saex-mcp-state\n" + payload segment</c> and is checked in fixed time before
    /// the payload is parsed; the redirect is re-validated as loopback after that.
    /// </summary>
    public sealed record SignedBrokerState(string RedirectUri, string? ClientState, string Nonce, long ExpiresAt)
    {
        public const int MaxEncodedLength = 2048;

        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

        private const string Purpose = "saex-mcp-state\n";

        private const int SignatureLength = 32;

        private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false };

        public static bool TryCreate(byte[] key, string redirectUri, string? clientState, string nonce, DateTime utcNow, [NotNullWhen(true)] out string? encoded)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(redirectUri);
            ArgumentNullException.ThrowIfNull(nonce);

            var payload = new JsonObject
            {
                ["r"] = redirectUri,
                ["s"] = clientState,
                ["n"] = nonce,
                ["e"] = UnixSeconds(utcNow + Lifetime),
            };
            var segment = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload));
            var value = segment + "." + Base64Url.EncodeToString(Sign(key, segment));
            encoded = value.Length <= MaxEncodedLength ? value : null;
            return encoded is not null;
        }

        public static bool TryVerify(byte[] key, string? encoded, DateTime utcNow, [NotNullWhen(true)] out SignedBrokerState? state)
        {
            ArgumentNullException.ThrowIfNull(key);
            state = null;
            if (string.IsNullOrEmpty(encoded) || encoded.Length > MaxEncodedLength)
            {
                return false;
            }

            var parts = encoded.Split('.');
            if (parts.Length != 2 || parts[0].Length == 0 || !parts.All(IsBase64Url))
            {
                return false;
            }

            try
            {
                var signature = Base64Url.DecodeFromChars(parts[1]);
                if (signature.Length != SignatureLength || !CryptographicOperations.FixedTimeEquals(signature, Sign(key, parts[0])))
                {
                    return false;
                }

                if (!TryRead(Base64Url.DecodeFromChars(parts[0]), out var decoded)
                    || decoded.ExpiresAt <= UnixSeconds(utcNow)
                    || !LoopbackRedirectUri.IsValid(decoded.RedirectUri))
                {
                    return false;
                }

                state = decoded;
                return true;
            }
            catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
            {
                return false;
            }
        }

        private static byte[] Sign(byte[] key, string segment)
            => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Purpose + segment));

        // Exactly r (string), s (string or null), n (non-empty string) and e (integer), each once.
        private static bool TryRead(byte[] json, [NotNullWhen(true)] out SignedBrokerState? state)
        {
            state = null;
            using var document = JsonDocument.Parse(json, StrictJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Count() != 4)
            {
                return false;
            }

            var root = document.RootElement;
            if (!root.TryGetProperty("r", out var redirect) || redirect.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("s", out var clientState) || clientState.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
                || !root.TryGetProperty("n", out var nonce) || nonce.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("e", out var expiry) || expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetInt64(out var expiresAt))
            {
                return false;
            }

            var nonceValue = nonce.GetString()!;
            if (nonceValue.Length == 0)
            {
                return false;
            }

            state = new SignedBrokerState(redirect.GetString()!, clientState.GetString(), nonceValue, expiresAt);
            return true;
        }

        /// <summary>
        /// Seconds since the Unix epoch; a <see cref="DateTimeKind.Unspecified"/> value counts as UTC.
        /// </summary>
        public static long UnixSeconds(DateTime utcNow)
            => new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeSeconds();

        private static bool IsBase64Url(string value)
            => value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');
    }
}
```


- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build SafeExchange.slnx --nologo -v q`, then
`dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SignedBrokerStateTests"`
and the task filter.
Expected: `Passed:    23`; task filter `Passed:   351`.

- [ ] **Step 5: Commit**

```powershell
git add SafeExchange.Core\Mcp\OAuth\SignedBrokerState.cs SafeExchange.Tests\Mcp\OAuth\SignedBrokerStateTests.cs
git commit -m "feat(mcp-oauth): HMAC-signed broker state with expiry and consent nonce" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
git status --short
```


---

## Task 3: Consent token, consent cookie and consent page

Three small host-agnostic building blocks (D3, D6, and the page of D1). `McpOAuthResult` is extended in Task 4,
where it is first used.

**Files:**
- Create: `SafeExchange.Core\Mcp\OAuth\ConsentCookie.cs`
- Create: `SafeExchange.Core\Mcp\OAuth\ConsentPage.cs`
- Create: `SafeExchange.Core\Mcp\OAuth\ConsentToken.cs`
- Test (create): `SafeExchange.Tests\Mcp\OAuth\ConsentCookieTests.cs`
- Test (create): `SafeExchange.Tests\Mcp\OAuth\ConsentPageTests.cs`
- Test (create): `SafeExchange.Tests\Mcp\OAuth\ConsentTokenTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `SafeExchange.Tests\Mcp\OAuth\ConsentCookieTests.cs`:

```csharp
/// <summary>
/// ConsentCookieTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;

    [TestFixture]
    public class ConsentCookieTests
    {
        private const string Nonce = "bm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm8";

        [Test]
        public void AnIssuedCookie_IsAHostOnlySecureHttpOnlyLaxCookieForTenMinutes()
        {
            Assert.That(
                ConsentCookie.Issue(Nonce).ToSetCookieHeader(),
                Is.EqualTo($"__Host-saex-mcp-consent={Nonce}; Max-Age=600; Path=/; Secure; HttpOnly; SameSite=Lax"));
        }

        [Test]
        public void TheExpiringCookie_ClearsTheValue()
        {
            Assert.That(
                ConsentCookie.Expire.ToSetCookieHeader(),
                Is.EqualTo("__Host-saex-mcp-consent=; Max-Age=0; Path=/; Secure; HttpOnly; SameSite=Lax"));
        }

        [TestCase("__Host-saex-mcp-consent=" + Nonce, Nonce)]
        [TestCase("a=1; __Host-saex-mcp-consent=" + Nonce + "; b=2", Nonce)]
        [TestCase("a=1;__Host-saex-mcp-consent=" + Nonce, Nonce)]
        [TestCase("__Host-saex-mcp-consent=first; __Host-saex-mcp-consent=second", "first")]
        [TestCase("__Host-saex-mcp-consent=", null)]
        [TestCase("x__Host-saex-mcp-consent=" + Nonce, null)]
        [TestCase("__host-saex-mcp-consent=" + Nonce, null)]
        [TestCase("a=1; b=2", null)]
        [TestCase("", null)]
        [TestCase(null, null)]
        public void Read_FindsTheCookieInACookieHeader(string? header, string? expected)
        {
            Assert.That(ConsentCookie.Read(header), Is.EqualTo(expected));
        }
    }
}
```

Create `SafeExchange.Tests\Mcp\OAuth\ConsentPageTests.cs`:

```csharp
/// <summary>
/// ConsentPageTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System.Collections.Generic;

    [TestFixture]
    public class ConsentPageTests
    {
        private const string Action = "https://safeexchange.test/api/oauth/authorize";

        [Test]
        public void Render_IsAFormPostingEveryFieldBackWithAllowAndDenyButtons()
        {
            var html = ConsentPage.Render(Action, "http://127.0.0.1:53682", new KeyValuePair<string, string>[]
            {
                new("client_id", "00000000-0000-0000-0000-0000000000c9"),
                new("state", "abc"),
            });

            Assert.That(html, Does.StartWith("<!DOCTYPE html>"));
            Assert.That(html, Does.Contain("<title>Sign in an MCP client to SafeExchange</title>"));
            Assert.That(html, Does.Contain("<h1>Sign in an MCP client to SafeExchange</h1>"));
            Assert.That(html, Does.Contain("An application on this computer is asking for access to SafeExchange as you. It will receive the result at <b>http://127.0.0.1:53682</b>."));
            Assert.That(html, Does.Contain("Access requested: read and manage your secrets; stay signed in."));
            Assert.That(html, Does.Contain("Only continue if you just started a sign-in in an MCP client such as Copilot CLI."));
            Assert.That(html, Does.Contain($"<form method=\"post\" action=\"{Action}\">"));
            Assert.That(html, Does.Contain("<input type=\"hidden\" name=\"client_id\" value=\"00000000-0000-0000-0000-0000000000c9\">"
                + "<input type=\"hidden\" name=\"state\" value=\"abc\">"));
            Assert.That(html, Does.Contain("<button type=\"submit\" name=\"decision\" value=\"allow\">"));
            Assert.That(html, Does.Contain("<button type=\"submit\" name=\"decision\" value=\"deny\">"));
            Assert.That(html, Does.Not.Contain("<script"));
        }

        [Test]
        public void Render_HtmlEncodesEveryValue()
        {
            const string Hostile = "\"><script>alert('x')</script>&";

            var html = ConsentPage.Render(Action + "?" + Hostile, "http://127.0.0.1:1" + Hostile, new KeyValuePair<string, string>[] { new("state" + Hostile, Hostile) });

            Assert.That(html, Does.Not.Contain("<script>"));
            Assert.That(html, Does.Not.Contain("'x'"));
            Assert.That(html, Does.Contain("&quot;&gt;&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;&amp;"));
        }

        [Test]
        public void Message_IsAPlainPageWithTheEncodedText()
        {
            var html = ConsentPage.Message("Wait <a minute>.");

            Assert.That(html, Does.StartWith("<!DOCTYPE html>"));
            Assert.That(html, Does.Contain("<p>Wait &lt;a minute&gt;.</p>"));
            Assert.That(html, Does.Not.Contain("<form"));
        }
    }
}
```

Create `SafeExchange.Tests\Mcp\OAuth\ConsentTokenTests.cs`:

```csharp
/// <summary>
/// ConsentTokenTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System.Collections.Generic;
    using System.Linq;

    [TestFixture]
    public class ConsentTokenTests
    {
        private static readonly KeyValuePair<string, string>[] Fields =
        {
            new("client_id", "00000000-0000-0000-0000-0000000000c9"),
            new("redirect_uri", "http://127.0.0.1:53682/callback"),
            new("state", "a&b=c d"),
            new("consent_expires", "1795342200"),
        };

        [Test]
        public void NewNonce_Is32RandomBytesInBase64Url()
        {
            var first = ConsentToken.NewNonce();
            var second = ConsentToken.NewNonce();

            Assert.That(first, Does.Match("^[A-Za-z0-9_-]{43}$"));
            Assert.That(ConsentToken.IsNonce(first), Is.True);
            Assert.That(second, Is.Not.EqualTo(first));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("short")]
        [TestCase("bm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm8=")]
        [TestCase("bm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Ut+m8")]
        public void IsNonce_RejectsAnythingButA43CharacterBase64UrlValue(string? value)
        {
            Assert.That(ConsentToken.IsNonce(value), Is.False);
        }

        [Test]
        public void ATokenVerifies_ForTheSameKeyNonceAndFields()
        {
            var nonce = ConsentToken.NewNonce();
            var token = ConsentToken.Create(McpOAuthTestKey.Bytes, nonce, Fields);

            Assert.That(token, Does.Match("^[A-Za-z0-9_-]{43}$"));
            Assert.That(ConsentToken.Verify(McpOAuthTestKey.Bytes, nonce, Fields, token), Is.True);
        }

        [Test]
        public void ATokenFails_ForAnotherNonceKeyOrAnyChangedField()
        {
            var nonce = ConsentToken.NewNonce();
            var token = ConsentToken.Create(McpOAuthTestKey.Bytes, nonce, Fields);
            var changedValue = Fields.Select(f => f.Key == "state" ? new KeyValuePair<string, string>("state", "a&b=c e") : f);
            var movedSeparator = Fields.Select(f => f.Key == "state" ? new KeyValuePair<string, string>("state&b", "c d") : f);
            var added = Fields.Append(new KeyValuePair<string, string>("login_hint", "user@contoso.test"));
            var reordered = Fields.Reverse();

            Assert.That(ConsentToken.Verify(McpOAuthTestKey.Bytes, ConsentToken.NewNonce(), Fields, token), Is.False);
            Assert.That(ConsentToken.Verify(new byte[32], nonce, Fields, token), Is.False);
            Assert.That(ConsentToken.Verify(McpOAuthTestKey.Bytes, nonce, changedValue, token), Is.False);
            Assert.That(ConsentToken.Verify(McpOAuthTestKey.Bytes, nonce, movedSeparator, token), Is.False);
            Assert.That(ConsentToken.Verify(McpOAuthTestKey.Bytes, nonce, added, token), Is.False);
            Assert.That(ConsentToken.Verify(McpOAuthTestKey.Bytes, nonce, reordered, token), Is.False);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("not-a-token")]
        public void AMissingOrMalformedToken_OrNonce_Fails(string? value)
        {
            var nonce = ConsentToken.NewNonce();
            var token = ConsentToken.Create(McpOAuthTestKey.Bytes, nonce, Fields);

            Assert.That(ConsentToken.Verify(McpOAuthTestKey.Bytes, nonce, Fields, value), Is.False);
            Assert.That(ConsentToken.Verify(McpOAuthTestKey.Bytes, value, Fields, token), Is.False);
        }
    }
}
```


- [ ] **Step 2: Run the test build to verify it fails**

Run: `dotnet build SafeExchange.Tests\SafeExchange.Tests.csproj --nologo -v q`
Expected: FAIL, first errors:

```
SafeExchange.Tests\Mcp\OAuth\ConsentCookieTests.cs(19,17): error CS0103: The name 'ConsentCookie' does not exist in the current context
SafeExchange.Tests\Mcp\OAuth\ConsentCookieTests.cs(27,17): error CS0103: The name 'ConsentCookie' does not exist in the current context
```

(and the same for `ConsentToken` and `ConsentPage`).

- [ ] **Step 3: Implement**

Create `SafeExchange.Core\Mcp\OAuth\ConsentCookie.cs`:

```csharp
/// <summary>
/// ConsentCookie
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;

    /// <summary>
    /// The <c>__Host-saex-mcp-consent</c> cookie that carries the consent nonce from the consent page to the consent
    /// form post and the callback: <c>Secure; HttpOnly; SameSite=Lax; Path=/</c> (Lax so that Entra's top-level
    /// redirect to the callback still sends it), valid for <see cref="Lifetime"/>.
    /// </summary>
    public sealed record ConsentCookie(string Value, int MaxAgeSeconds)
    {
        public const string Name = "__Host-saex-mcp-consent";

        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

        public static ConsentCookie Expire { get; } = new(string.Empty, 0);

        public static ConsentCookie Issue(string nonce)
        {
            ArgumentNullException.ThrowIfNull(nonce);
            return new ConsentCookie(nonce, (int)Lifetime.TotalSeconds);
        }

        public string ToSetCookieHeader() => $"{Name}={this.Value}; Max-Age={this.MaxAgeSeconds}; Path=/; Secure; HttpOnly; SameSite=Lax";

        /// <summary>
        /// The value of the first occurrence of the cookie in a <c>Cookie</c> request header (exact, case-sensitive
        /// name); null when it is absent or empty.
        /// </summary>
        public static string? Read(string? cookieHeader)
        {
            foreach (var pair in (cookieHeader ?? string.Empty).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (pair.StartsWith(Name + "=", StringComparison.Ordinal))
                {
                    var value = pair[(Name.Length + 1)..];
                    return value.Length > 0 ? value : null;
                }
            }

            return null;
        }
    }
}
```

Create `SafeExchange.Core\Mcp\OAuth\ConsentPage.cs`:

```csharp
/// <summary>
/// ConsentPage
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Text;

    /// <summary>
    /// The server-rendered pages of the broker: the consent form (hidden fields, Allow and Deny buttons) and plain
    /// message pages. No JavaScript; every value is HTML-encoded. Serve them with <see cref="SecurityHeaders"/>.
    /// </summary>
    public static class ConsentPage
    {
        public const string Title = "Sign in an MCP client to SafeExchange";

        public const string InvalidRequestMessage = "This sign-in request is invalid or expired. Start the sign-in again from your MCP client.";

        public const string TooManyRequestsMessage = "Too many sign-in requests. Wait a minute, then start the sign-in again from your MCP client.";

        private const string Style = "body{font-family:system-ui,sans-serif;max-width:36rem;margin:3rem auto;padding:0 1rem;line-height:1.5}"
            + "button{font-size:1rem;padding:.5rem 1.25rem;margin-right:.75rem}";

        public static IReadOnlyList<KeyValuePair<string, string>> SecurityHeaders { get; } = new KeyValuePair<string, string>[]
        {
            new("Content-Security-Policy", "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'"),
            new("X-Frame-Options", "DENY"),
            new("X-Content-Type-Options", "nosniff"),
            new("Referrer-Policy", "no-referrer"),
        };

        /// <param name="formAction">Absolute URL the form posts to (the authorize endpoint).</param>
        /// <param name="clientOrigin"><c>scheme://host:port</c> of the client's redirect URI.</param>
        /// <param name="fields">Hidden form fields, in order.</param>
        public static string Render(string formAction, string clientOrigin, IEnumerable<KeyValuePair<string, string>> fields)
        {
            ArgumentNullException.ThrowIfNull(formAction);
            ArgumentNullException.ThrowIfNull(clientOrigin);
            ArgumentNullException.ThrowIfNull(fields);

            var body = new StringBuilder()
                .Append("<p>An application on this computer is asking for access to SafeExchange as you. It will receive the result at <b>")
                .Append(Encode(clientOrigin))
                .Append("</b>.</p>")
                .Append("<p>Access requested: read and manage your secrets; stay signed in.</p>")
                .Append("<p>Only continue if you just started a sign-in in an MCP client such as Copilot CLI.</p>")
                .Append("<form method=\"post\" action=\"").Append(Encode(formAction)).Append("\">");
            foreach (var field in fields)
            {
                body.Append("<input type=\"hidden\" name=\"").Append(Encode(field.Key)).Append("\" value=\"").Append(Encode(field.Value)).Append("\">");
            }

            body.Append("<button type=\"submit\" name=\"decision\" value=\"allow\">Continue</button>")
                .Append("<button type=\"submit\" name=\"decision\" value=\"deny\">Cancel</button>")
                .Append("</form>");
            return Page(body.ToString());
        }

        public static string Message(string message)
        {
            ArgumentNullException.ThrowIfNull(message);
            return Page("<p>" + Encode(message) + "</p>");
        }

        private static string Page(string body)
            => "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">"
                + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
                + $"<title>{Title}</title><style>{Style}</style></head>"
                + $"<body><main><h1>{Title}</h1>{body}</main></body></html>";

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }
}
```

Create `SafeExchange.Core\Mcp\OAuth\ConsentToken.cs`:

```csharp
/// <summary>
/// ConsentToken
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;
    using System.Buffers.Text;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;

    /// <summary>
    /// Binds the consent form to the browser that loaded it: the page carries
    /// <c>consent_token = base64url(HMAC-SHA256(key, "saex-mcp-consent\n" + nonce + "\n" + canonical fields))</c>, the
    /// nonce travels only in the <see cref="ConsentCookie"/>, and the canonical fields are the form fields in order as
    /// <c>name=value</c> pairs (both URI-escaped) joined by <c>&amp;</c>. Verification is fixed-time.
    /// </summary>
    public static class ConsentToken
    {
        public const int NonceLength = 43;

        private const string Purpose = "saex-mcp-consent\n";

        public static string NewNonce() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

        public static bool IsNonce([NotNullWhen(true)] string? value)
            => value is { Length: NonceLength } && value.All(IsBase64UrlChar);

        public static string Create(byte[] key, string nonce, IEnumerable<KeyValuePair<string, string>> fields)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(nonce);
            ArgumentNullException.ThrowIfNull(fields);

            var canonical = string.Join('&', fields.Select(f => $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(f.Value)}"));
            return Base64Url.EncodeToString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Purpose + nonce + "\n" + canonical)));
        }

        public static bool Verify(byte[] key, string? nonce, IEnumerable<KeyValuePair<string, string>> fields, string? token)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(fields);
            if (!IsNonce(nonce) || token is null || !token.All(IsBase64UrlChar))
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Create(key, nonce, fields)), Encoding.ASCII.GetBytes(token));
        }

        private static bool IsBase64UrlChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_';
    }
}
```


- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build SafeExchange.slnx --nologo -v q`, then
`dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~ConsentTokenTests|FullyQualifiedName~ConsentCookieTests|FullyQualifiedName~ConsentPageTests"`
and the task filter.
Expected: `Passed:    26`; task filter `Passed:   377`.

- [ ] **Step 5: Commit**

```powershell
git add SafeExchange.Core\Mcp\OAuth\ConsentCookie.cs SafeExchange.Core\Mcp\OAuth\ConsentPage.cs SafeExchange.Core\Mcp\OAuth\ConsentToken.cs SafeExchange.Tests\Mcp\OAuth\ConsentCookieTests.cs SafeExchange.Tests\Mcp\OAuth\ConsentPageTests.cs SafeExchange.Tests\Mcp\OAuth\ConsentTokenTests.cs
git commit -m "feat(mcp-oauth): consent token, consent cookie and server-rendered consent page" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
git status --short
```


---

## Task 4: Consent flow, consent-bound signed state, no prompt passthrough (broker, adapter, trigger)

The broker's `Authorize` answers the consent page, the new `AuthorizeConsent` handles the form and `Callback`
verifies the signed state against the cookie (D1–D4, D6). The adapter and the trigger change in the same task so the
build and every test stay green: the adapter routes `POST` to `AuthorizeConsent`, reads the cookie and writes HTML
with the security headers, `Set-Cookie` and `Retry-After` (the 429/503 answers of later tasks use it). Refusals that
are new in this task already log at Warning (D13); Task 6 moves the existing ones.

**Files:**
- Modify: `SafeExchange.Core.Functions\Mcp\OAuth\McpOAuthHandler.cs`
- Delete: `SafeExchange.Core\Mcp\OAuth\BrokerState.cs`
- Modify: `SafeExchange.Core\Mcp\OAuth\McpOAuthBroker.cs`
- Modify: `SafeExchange.Core\Mcp\OAuth\McpOAuthResult.cs`
- Modify: `SafeExchange.Functions\Functions\SafeMcpOAuth.cs`
- Test (modify): `SafeExchange.Tests\Contract\FunctionRouteTests.cs`
- Test (delete): `SafeExchange.Tests\Mcp\OAuth\BrokerStateTests.cs`
- Test (create): `SafeExchange.Tests\Mcp\OAuth\ConsentForm.cs`
- Test (modify): `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerAuthorizeTests.cs`
- Test (modify): `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerCallbackTests.cs`
- Test (create): `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerConsentTests.cs`
- Test (modify): `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerGatingTests.cs`
- Test (modify): `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTestBase.cs`
- Test (modify): `SafeExchange.Tests\Mcp\OAuth\McpOAuthHandlerTests.cs`
- Test (modify): `SafeExchange.Tests\Mcp\OAuth\McpOAuthHttpTestBase.cs`
- Test (modify): `SafeExchange.Tests\Mcp\OAuth\McpOAuthLoggingTests.cs`

- [ ] **Step 1: Write the failing tests**

`ConsentForm` is what a browser does with the page; the broker test base gains `ValidQuery`, `OpenConsent`,
`AllowConsent` and `AssertInvalidPage`, so the authorize, consent and callback tests share one way to walk the flow.
The old authorize tests that asserted the Entra redirect move to the new consent tests (the redirect now follows the
form). The route test gets the single expected-methods change of ground rule 3.

Modify `SafeExchange.Tests\Contract\FunctionRouteTests.cs`:

Replace:

```csharp
            "SafeExchange-ListSecretMeta|get|api/{apiVersion}/secret-list",
            "SafeExchange-Mcp|delete,get,post|api/mcp",
            "SafeExchange-OAuthAuthorizationServerMetadata|get|api/.well-known/oauth-authorization-server",
            "SafeExchange-OAuthAuthorize|get|api/oauth/authorize",
            "SafeExchange-OAuthCallback|get|api/oauth/callback",
            "SafeExchange-OAuthOpenIdConfiguration|get|api/.well-known/openid-configuration",
            "SafeExchange-OAuthProtectedResource|get|api/.well-known/oauth-protected-resource",
```

with:

```csharp
            "SafeExchange-ListSecretMeta|get|api/{apiVersion}/secret-list",
            "SafeExchange-Mcp|delete,get,post|api/mcp",
            "SafeExchange-OAuthAuthorizationServerMetadata|get|api/.well-known/oauth-authorization-server",
            "SafeExchange-OAuthAuthorize|get,post|api/oauth/authorize",
            "SafeExchange-OAuthCallback|get|api/oauth/callback",
            "SafeExchange-OAuthOpenIdConfiguration|get|api/.well-known/openid-configuration",
            "SafeExchange-OAuthProtectedResource|get|api/.well-known/oauth-protected-resource",
```

Delete `SafeExchange.Tests\Mcp\OAuth\BrokerStateTests.cs`: `git rm SafeExchange.Tests\Mcp\OAuth\BrokerStateTests.cs`.

Create `SafeExchange.Tests\Mcp\OAuth\ConsentForm.cs`:

```csharp
/// <summary>
/// ConsentForm
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Text.RegularExpressions;

    /// <summary>
    /// What a browser does with the consent page: reads its hidden fields and posts them back with a decision.
    /// </summary>
    public static class ConsentForm
    {
        public static List<KeyValuePair<string, string>> HiddenFields(string? html)
            => Regex.Matches(html ?? string.Empty, "<input type=\"hidden\" name=\"([^\"]*)\" value=\"([^\"]*)\">")
                .Select(m => new KeyValuePair<string, string>(WebUtility.HtmlDecode(m.Groups[1].Value), WebUtility.HtmlDecode(m.Groups[2].Value)))
                .ToList();

        public static string Post(IEnumerable<KeyValuePair<string, string>> fields, string? decision = "allow")
            => string.Join('&', fields
                .Concat(decision is null ? Array.Empty<KeyValuePair<string, string>>() : new[] { new KeyValuePair<string, string>("decision", decision) })
                .Select(f => $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(f.Value)}"));

        public static List<KeyValuePair<string, string>> With(this IEnumerable<KeyValuePair<string, string>> fields, string name, string value)
            => fields.Select(f => f.Key == name ? new KeyValuePair<string, string>(name, value) : f).ToList();
    }
}
```

Replace the whole content of `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerAuthorizeTests.cs` with:

```csharp
/// <summary>
/// McpOAuthBrokerAuthorizeTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;

    [TestFixture]
    public class McpOAuthBrokerAuthorizeTests : McpOAuthBrokerTestBase
    {
        [Test]
        public void Authorize_AnswersTheConsentPage_AndSetsTheConsentCookie_WithoutContactingEntra()
        {
            var result = this.Broker.Authorize(ApiBase, ValidQuery());

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(result.Location, Is.Null);
            Assert.That(result.ContentType, Is.EqualTo("text/html; charset=utf-8"));
            Assert.That(result.Cookie!.MaxAgeSeconds, Is.EqualTo(600));
            Assert.That(ConsentToken.IsNonce(result.Cookie.Value), Is.True);
            Assert.That(result.Body, Does.Not.Contain(result.Cookie.Value), "The nonce travels only in the cookie.");
            Assert.That(result.Body, Does.Contain("<form method=\"post\" action=\"https://safeexchange.test/api/oauth/authorize\">"));
            Assert.That(result.Body, Does.Contain("It will receive the result at <b>http://127.0.0.1:53682</b>."));
            var fields = ConsentForm.HiddenFields(result.Body);
            Assert.That(fields.Select(f => f.Key), Is.EqualTo(new[] { "client_id", "redirect_uri", "response_type", "code_challenge", "code_challenge_method", "state", "consent_expires", "consent_token" }));
            Assert.That(fields.Take(6).Select(f => f.Value), Is.EqualTo(new[] { BrokerClientId, ClientRedirect, "code", Challenge, "S256", ClientState }));
            Assert.That(this.Entra.Requests, Is.Empty);
        }

        [Test]
        public void Authorize_KeepsScopeAndLoginHintInTheForm_ButNeverPromptOrUnknownParameters()
        {
            var (fields, _) = this.OpenConsent(ValidQuery(
                ("prompt", "none"),
                ("login_hint", "user@contoso.test"),
                ("scope", "openid profile"),
                ("resource", "https://safeexchange.test/api/mcp"),
                ("nonce", "n-0S6_WzA2Mj"),
                ("redirect_to", "https://evil.example/")));

            Assert.That(fields.Select(f => f.Key), Is.EqualTo(new[] { "client_id", "redirect_uri", "response_type", "code_challenge", "code_challenge_method", "state", "scope", "login_hint", "consent_expires", "consent_token" }));
            Assert.That(fields.Single(f => f.Key == "scope").Value, Is.EqualTo("openid profile"));
            Assert.That(fields.Single(f => f.Key == "login_hint").Value, Is.EqualTo("user@contoso.test"));
        }

        [Test]
        public void Authorize_WithoutClientState_LeavesItOutOfTheForm()
        {
            var query = Query(("client_id", BrokerClientId), ("redirect_uri", ClientRedirect), ("response_type", "code"), ("code_challenge", Challenge), ("code_challenge_method", "S256"));

            var (fields, _) = this.OpenConsent(query);

            Assert.That(fields.Select(f => f.Key), Does.Not.Contain("state"));
        }
        [TestCase("00000000-0000-0000-0000-0000000000ff")]
        [TestCase("")]
        [TestCase(BrokerClientId + " ")]
        public void Authorize_WithAnUnknownClient_AnswersInvalidClient_WithoutRedirecting(string clientId)
        {
            var query = Query(("client_id", clientId), ("redirect_uri", ClientRedirect), ("response_type", "code"), ("code_challenge", Challenge), ("code_challenge_method", "S256"), ("state", ClientState));

            AssertNoRedirect(this.Broker.Authorize(ApiBase, query), "invalid_client");
        }

        [TestCase("https://evil.example/callback")]
        [TestCase("http://evil.example/callback")]
        [TestCase("http://localhost.evil.com/callback")]
        [TestCase("http://127.0.0.1.nip.io/callback")]
        [TestCase("http://user@127.0.0.1:53682/callback")]
        [TestCase("http://127.0.0.1@evil.example/callback")]
        [TestCase("http://127.0.0.1:53682/callback#fragment")]
        [TestCase("//evil.example/callback")]
        [TestCase("/callback")]
        [TestCase("")]
        public void Authorize_WithANonLoopbackRedirect_AnswersInvalidRequest_WithoutRedirecting(string redirectUri)
        {
            var query = Query(("client_id", BrokerClientId), ("redirect_uri", redirectUri), ("response_type", "code"), ("code_challenge", Challenge), ("code_challenge_method", "S256"), ("state", ClientState));

            AssertNoRedirect(this.Broker.Authorize(ApiBase, query), "invalid_request");
        }

        [Test]
        public void Authorize_WithARepeatedParameter_AnswersInvalidRequest_WithoutRedirecting()
        {
            var query = ValidQuery() + "&" + Query(("redirect_uri", "https://evil.example/callback"));

            AssertNoRedirect(this.Broker.Authorize(ApiBase, query), "invalid_request");
        }

        [Test]
        public void Authorize_WithoutAResponseType_RedirectsInvalidRequestToTheClient()
        {
            var query = Query(("client_id", BrokerClientId), ("redirect_uri", ClientRedirect), ("code_challenge", Challenge), ("code_challenge_method", "S256"), ("state", ClientState));

            AssertClientError(this.Broker.Authorize(ApiBase, query), "invalid_request");
        }

        [TestCase("token", "unsupported_response_type")]
        [TestCase("code id_token", "unsupported_response_type")]
        public void Authorize_WithAnotherResponseType_RedirectsTheErrorToTheClient(string responseType, string error)
        {
            var query = Query(("client_id", BrokerClientId), ("redirect_uri", ClientRedirect), ("response_type", responseType), ("code_challenge", Challenge), ("code_challenge_method", "S256"), ("state", ClientState));

            AssertClientError(this.Broker.Authorize(ApiBase, query), error);
        }

        [TestCase(null, "S256")]
        [TestCase(Challenge, null)]
        [TestCase(Challenge, "plain")]
        [TestCase(Challenge, "s256")]
        [TestCase("too-short", "S256")]
        [TestCase("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw+cM", "S256")]
        public void Authorize_WithoutS256Pkce_RedirectsInvalidRequestToTheClient(string? challenge, string? method)
        {
            var parameters = new List<(string, string)> { ("client_id", BrokerClientId), ("redirect_uri", ClientRedirect), ("response_type", "code"), ("state", ClientState) };
            if (challenge is not null)
            {
                parameters.Add(("code_challenge", challenge));
            }

            if (method is not null)
            {
                parameters.Add(("code_challenge_method", method));
            }

            AssertClientError(this.Broker.Authorize(ApiBase, Query(parameters.ToArray())), "invalid_request");
        }

        [Test]
        public void Authorize_WithAStateThatDoesNotFitIn2KB_RedirectsInvalidRequestToTheClient()
        {
            var query = Query(("client_id", BrokerClientId), ("redirect_uri", ClientRedirect), ("response_type", "code"), ("code_challenge", Challenge), ("code_challenge_method", "S256"), ("state", new string('s', 1600)));

            var result = this.Broker.Authorize(ApiBase, query);

            var (target, parameters) = SplitLocation(result.Location);
            Assert.That(target, Is.EqualTo(ClientRedirect));
            Assert.That(parameters["error"], Is.EqualTo("invalid_request"));
        }

        private static void AssertNoRedirect(McpOAuthResult result, string error)
        {
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(result.Location, Is.Null);
            Assert.That(Json(result.Body)["error"]!.GetValue<string>(), Is.EqualTo(error));
        }

        private static void AssertClientError(McpOAuthResult result, string error)
        {
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            var (target, parameters) = SplitLocation(result.Location);
            Assert.That(target, Is.EqualTo(ClientRedirect));
            Assert.That(parameters.Keys, Is.EquivalentTo(new[] { "error", "error_description", "state" }));
            Assert.That(parameters["error"], Is.EqualTo(error));
            Assert.That(parameters["state"], Is.EqualTo(ClientState));
        }
    }
}
```

Replace the whole content of `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerCallbackTests.cs` with:

```csharp
/// <summary>
/// McpOAuthBrokerCallbackTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Net;
    using System.Text;

    [TestFixture]
    public class McpOAuthBrokerCallbackTests : McpOAuthBrokerTestBase
    {
        private const string Code = "1.AQEA-fake-entra-authorization-code";

        private const string Nonce = "bm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm8";

        [Test]
        public void Callback_ForwardsTheCodeAndTheClientsState_ToTheLoopbackRedirect_AndExpiresTheCookie()
        {
            var (state, nonce) = this.AllowConsent(ValidQuery());
            var query = Query(("code", Code), ("state", state), ("session_state", "00000000-0000-0000-0000-0000000000aa"), ("iss", "https://login.microsoftonline.com/x/v2.0"));

            var result = this.Broker.Callback(query, nonce);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(result.Body, Is.Null);
            Assert.That(result.Location, Is.EqualTo($"{ClientRedirect}?code={Uri.EscapeDataString(Code)}&state={ClientState}"));
            Assert.That(result.Cookie, Is.EqualTo(ConsentCookie.Expire));
        }

        [Test]
        public void Callback_AppendsToARedirectThatHasAQuery_AndOmitsAMissingClientState()
        {
            var result = this.Broker.Callback(Query(("code", Code), ("state", Sign("http://localhost:8080/cb?session=7", null))), Nonce);

            Assert.That(result.Location, Is.EqualTo($"http://localhost:8080/cb?session=7&code={Uri.EscapeDataString(Code)}"));
        }

        [Test]
        public void Callback_ForwardsAnEntraError_WithOnlyErrorDescriptionAndState()
        {
            var query = Query(
                ("error", "access_denied"),
                ("error_description", "AADSTS65004: User declined to consent."),
                ("error_uri", "https://login.microsoftonline.com/error?code=65004"),
                ("correlation_id", "00000000-0000-0000-0000-0000000000cc"),
                ("state", Sign(ClientRedirect, "csrf-2")));

            var result = this.Broker.Callback(query, Nonce);

            var (target, parameters) = SplitLocation(result.Location);
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(result.Body, Is.Null);
            Assert.That(target, Is.EqualTo(ClientRedirect));
            Assert.That(parameters.Keys, Is.EquivalentTo(new[] { "error", "error_description", "state" }));
            Assert.That(parameters["error"], Is.EqualTo("access_denied"));
            Assert.That(parameters["error_description"], Is.EqualTo("AADSTS65004: User declined to consent."));
            Assert.That(parameters["state"], Is.EqualTo("csrf-2"));
            Assert.That(result.Cookie, Is.EqualTo(ConsentCookie.Expire));
        }

        [Test]
        public void Callback_WithNeitherCodeNorError_RedirectsServerErrorToTheClient()
        {
            var (target, parameters) = SplitLocation(this.Broker.Callback(Query(("state", Sign(ClientRedirect, "csrf-3"))), Nonce).Location);

            Assert.That(target, Is.EqualTo(ClientRedirect));
            Assert.That(parameters["error"], Is.EqualTo("server_error"));
            Assert.That(parameters["state"], Is.EqualTo("csrf-3"));
        }

        [TestCase(null)]
        [TestCase("not-a-state")]
        [TestCase("eyJyIjoiaHR0cDovLzEyNy4wLjAuMToxL2NiIiwicyI6IngifQ")]
        public void Callback_WithoutAValidState_IsTheInvalidPage(string? state)
        {
            var query = state is null ? Query(("code", Code)) : Query(("code", Code), ("state", state));

            AssertInvalidPage(this.Broker.Callback(query, Nonce));
        }

        [TestCase("https://evil.example/callback")]
        [TestCase("http://localhost.evil.com/callback")]
        [TestCase("http://127.0.0.1.nip.io/callback")]
        public void Callback_WithACraftedUnsignedState_NeverRedirects(string redirectUri)
        {
            var crafted = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"r\":\"{redirectUri}\",\"s\":\"x\",\"n\":\"{Nonce}\",\"e\":4102444800}}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            AssertInvalidPage(this.Broker.Callback(Query(("code", Code), ("state", crafted)), Nonce));
            AssertInvalidPage(this.Broker.Callback(Query(("code", Code), ("state", crafted + ".AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")), Nonce));
        }

        [Test]
        public void Callback_WithARepeatedState_IsTheInvalidPage()
        {
            var query = Query(("code", Code), ("state", Sign(ClientRedirect, "a")), ("state", Sign("http://localhost:1/other", "b")));

            AssertInvalidPage(this.Broker.Callback(query, Nonce));
        }

        [Test]
        public void Callback_WithoutTheCookie_OrWithAnotherNonce_IsTheInvalidPage()
        {
            var query = Query(("code", Code), ("state", Sign(ClientRedirect, "csrf-4")));

            AssertInvalidPage(this.Broker.Callback(query, null));
            AssertInvalidPage(this.Broker.Callback(query, "short"));
            AssertInvalidPage(this.Broker.Callback(query, ConsentToken.NewNonce()));
        }

        [Test]
        public void Callback_WithAnExpiredStateOrOneSignedWithAnotherKey_IsTheInvalidPage()
        {
            Assert.That(SignedBrokerState.TryCreate(McpOAuthTestKey.Bytes, ClientRedirect, "csrf-5", Nonce, DateTimeProvider.UtcNow.AddMinutes(-10), out var expired), Is.True);
            Assert.That(SignedBrokerState.TryCreate(new byte[32], ClientRedirect, "csrf-5", Nonce, DateTimeProvider.UtcNow, out var otherKey), Is.True);

            AssertInvalidPage(this.Broker.Callback(Query(("code", Code), ("state", expired!)), Nonce));
            AssertInvalidPage(this.Broker.Callback(Query(("code", Code), ("state", otherKey!)), Nonce));
        }

        private static string Sign(string redirectUri, string? clientState)
        {
            Assert.That(SignedBrokerState.TryCreate(McpOAuthTestKey.Bytes, redirectUri, clientState, Nonce, DateTimeProvider.UtcNow, out var encoded), Is.True);
            return encoded!;
        }
    }
}
```

Create `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerConsentTests.cs`:

```csharp
/// <summary>
/// McpOAuthBrokerConsentTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;

    /// <summary>
    /// The consent form post: allow redirects to Entra with a signed state bound to the consent cookie, deny redirects
    /// <c>access_denied</c> to the client, and anything that does not match the cookie, the token or the parameters is
    /// the 400 page without a redirect.
    /// </summary>
    [TestFixture]
    public class McpOAuthBrokerConsentTests : McpOAuthBrokerTestBase
    {
        [Test]
        public void Allow_RedirectsToEntra_WithTheBrokersClientCallbackScopeAndSignedState()
        {
            var (fields, nonce) = this.OpenConsent(ValidQuery());

            var result = this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields), nonce);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(result.Body, Is.Null);
            Assert.That(result.Cookie, Is.Null, "The cookie is still needed at the callback.");
            var (target, parameters) = SplitLocation(result.Location);
            Assert.That(target, Is.EqualTo(Tenant + "/oauth2/v2.0/authorize"));
            Assert.That(parameters.Keys, Is.EquivalentTo(new[] { "client_id", "response_type", "redirect_uri", "response_mode", "scope", "code_challenge", "code_challenge_method", "state" }));
            Assert.That(parameters["client_id"], Is.EqualTo(BrokerClientId));
            Assert.That(parameters["response_type"], Is.EqualTo("code"));
            Assert.That(parameters["redirect_uri"], Is.EqualTo(Callback));
            Assert.That(parameters["response_mode"], Is.EqualTo("query"));
            Assert.That(parameters["scope"], Is.EqualTo(ApiScope + " offline_access"));
            Assert.That(parameters["code_challenge"], Is.EqualTo(Challenge));
            Assert.That(parameters["code_challenge_method"], Is.EqualTo("S256"));
            Assert.That(SignedBrokerState.TryVerify(McpOAuthTestKey.Bytes, parameters["state"], DateTimeProvider.UtcNow, out var state), Is.True);
            Assert.That((state!.RedirectUri, state.ClientState, state.Nonce), Is.EqualTo((ClientRedirect, ClientState, nonce)));
            Assert.That(this.Entra.Requests, Is.Empty);
        }

        [TestCase("none")]
        [TestCase("select_account")]
        [TestCase("consent")]
        public void Allow_ForwardsLoginHint_ButNeverPrompt_AndReplacesTheRequestedScope(string prompt)
        {
            var (fields, nonce) = this.OpenConsent(ValidQuery(
                ("prompt", prompt),
                ("login_hint", "user@contoso.test"),
                ("scope", "openid profile https://graph.microsoft.com/Mail.Read api://other/.default"),
                ("resource", "https://safeexchange.test/api/mcp")));
            var posted = fields.Append(new KeyValuePair<string, string>("prompt", prompt));

            var (_, parameters) = SplitLocation(this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(posted), nonce).Location);

            Assert.That(parameters.Keys, Does.Not.Contain("prompt").And.Not.Contain("resource"));
            Assert.That(parameters["login_hint"], Is.EqualTo("user@contoso.test"));
            Assert.That(parameters["scope"], Is.EqualTo(ApiScope + " offline_access"));
        }

        [Test]
        public void Deny_RedirectsAccessDeniedToTheClient_AndExpiresTheCookie()
        {
            var (fields, nonce) = this.OpenConsent(ValidQuery());

            var result = this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields, "deny"), nonce);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            var (target, parameters) = SplitLocation(result.Location);
            Assert.That(target, Is.EqualTo(ClientRedirect));
            Assert.That(parameters.Keys, Is.EquivalentTo(new[] { "error", "error_description", "state" }));
            Assert.That(parameters["error"], Is.EqualTo("access_denied"));
            Assert.That(parameters["state"], Is.EqualTo(ClientState));
            Assert.That(result.Cookie, Is.EqualTo(ConsentCookie.Expire));
        }

        [Test]
        public void WithoutTheCookie_OrWithAnotherSignInsCookie_IsTheInvalidPage()
        {
            var (fields, _) = this.OpenConsent(ValidQuery());
            var (_, otherNonce) = this.OpenConsent(ValidQuery());

            AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields), null));
            AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields), "not-a-nonce"));
            AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields), otherNonce));
        }

        [TestCase("state", "attacker-state")]
        [TestCase("redirect_uri", "http://127.0.0.1:1/other")]
        [TestCase("code_challenge", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
        [TestCase("consent_expires", "4102444800")]
        [TestCase("consent_token", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
        public void ATamperedField_IsTheInvalidPage(string name, string value)
        {
            var (fields, nonce) = this.OpenConsent(ValidQuery());

            AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields.With(name, value)), nonce));
        }

        [Test]
        public void AnAddedLoginHint_IsTheInvalidPage()
        {
            var (fields, nonce) = this.OpenConsent(ValidQuery());

            AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields.Append(new KeyValuePair<string, string>("login_hint", "victim@contoso.test"))), nonce));
        }

        [TestCase("client_id", "00000000-0000-0000-0000-0000000000ff")]
        [TestCase("redirect_uri", "https://evil.example/cb")]
        [TestCase("response_type", "token")]
        [TestCase("code_challenge_method", "plain")]
        public void AnInvalidParameter_IsTheInvalidPage_AndNeverRedirected(string name, string value)
        {
            var (fields, nonce) = this.OpenConsent(ValidQuery());

            AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields.With(name, value)), nonce));
        }

        [TestCase(null)]
        [TestCase("maybe")]
        public void AMissingOrUnknownDecision_IsTheInvalidPage(string? decision)
        {
            var (fields, nonce) = this.OpenConsent(ValidQuery());

            AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields, decision), nonce));
        }

        [Test]
        public void ARepeatedEmptyOrOversizeForm_IsTheInvalidPage()
        {
            var (fields, nonce) = this.OpenConsent(ValidQuery());
            var form = ConsentForm.Post(fields);

            AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, form + "&state=other", nonce));
            AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, string.Empty, nonce));
            AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, null, nonce));
            AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, form + "&pad=" + new string('p', McpOAuthBroker.MaxBodyLength), nonce));
        }

        [Test]
        public void AConsentPageOlderThanTenMinutes_IsTheInvalidPage()
        {
            var (useSpecified, specified) = (DateTimeProvider.UseSpecifiedDateTime, DateTimeProvider.SpecifiedDateTime);
            try
            {
                DateTimeProvider.SpecifiedDateTime = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
                DateTimeProvider.UseSpecifiedDateTime = true;
                var (fields, nonce) = this.OpenConsent(ValidQuery());

                DateTimeProvider.SpecifiedDateTime = DateTimeProvider.SpecifiedDateTime.AddSeconds(599);
                Assert.That(this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields), nonce).StatusCode, Is.EqualTo(HttpStatusCode.Found));
                DateTimeProvider.SpecifiedDateTime = DateTimeProvider.SpecifiedDateTime.AddSeconds(1);
                AssertInvalidPage(this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields), nonce));
            }
            finally
            {
                (DateTimeProvider.UseSpecifiedDateTime, DateTimeProvider.SpecifiedDateTime) = (useSpecified, specified);
            }
        }
    }
}
```

Modify `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerGatingTests.cs`:

Replace:

```csharp
                broker.Metadata(ApiBase),
                broker.Register("{\"redirect_uris\":[\"http://127.0.0.1:1/cb\"]}"),
                broker.Authorize(ApiBase, $"client_id={BrokerClientId}&redirect_uri=http%3A%2F%2F127.0.0.1%3A1%2Fcb&response_type=code&code_challenge={Challenge}&code_challenge_method=S256"),
                broker.Callback("code=c&state=eyJyIjoiaHR0cDovLzEyNy4wLjAuMToxL2NiIn0"),
                await broker.TokenAsync(ApiBase, "grant_type=refresh_token&refresh_token=r"),
            };
    }
```

with:

```csharp
                broker.Metadata(ApiBase),
                broker.Register("{\"redirect_uris\":[\"http://127.0.0.1:1/cb\"]}"),
                broker.Authorize(ApiBase, $"client_id={BrokerClientId}&redirect_uri=http%3A%2F%2F127.0.0.1%3A1%2Fcb&response_type=code&code_challenge={Challenge}&code_challenge_method=S256"),
                broker.AuthorizeConsent(ApiBase, $"client_id={BrokerClientId}&decision=allow", "bm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm8"),
                broker.Callback("code=c&state=eyJyIjoiaHR0cDovLzEyNy4wLjAuMToxL2NiIn0", "bm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm8"),
                await broker.TokenAsync(ApiBase, "grant_type=refresh_token&refresh_token=r"),
            };
    }
```

Modify `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTestBase.cs` (3 edits):

Edit 1 of 3. Replace:

```csharp
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json.Nodes;

    /// <summary>
```

with:

```csharp
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Text.Json.Nodes;

    /// <summary>
```

Edit 2 of 3. Replace:

```csharp

        protected const string ClientRedirect = "http://127.0.0.1:53682/callback";

        protected const string Callback = "https://safeexchange.test/api/oauth/callback";

        // RFC 7636 appendix B.
```

with:

```csharp

        protected const string ClientRedirect = "http://127.0.0.1:53682/callback";

        protected const string ClientState = "rmcp-csrf-token-123";

        protected const string Callback = "https://safeexchange.test/api/oauth/callback";

        // RFC 7636 appendix B.
```

Edit 3 of 3. Replace:

```csharp
        protected static string Query(params (string Name, string Value)[] parameters)
            => string.Join('&', parameters.Select(p => $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(p.Value)}"));

        /// <summary>
        /// Splits a redirect URL into the part before <c>?</c> and its decoded parameters.
        /// </summary>
```

with:

```csharp
        protected static string Query(params (string Name, string Value)[] parameters)
            => string.Join('&', parameters.Select(p => $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(p.Value)}"));

        protected static string ValidQuery(params (string Name, string Value)[] extra)
            => Query(new[]
            {
                ("client_id", BrokerClientId),
                ("redirect_uri", ClientRedirect),
                ("response_type", "code"),
                ("code_challenge", Challenge),
                ("code_challenge_method", "S256"),
                ("state", ClientState),
            }.Concat(extra).ToArray());

        /// <summary>
        /// Opens the consent page for <paramref name="query"/>: its hidden form fields and the consent cookie's nonce.
        /// </summary>
        protected (List<KeyValuePair<string, string>> Fields, string Nonce) OpenConsent(string query)
        {
            var page = this.Broker.Authorize(ApiBase, query);
            Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Arrange: the consent page.");
            return (ConsentForm.HiddenFields(page.Body), page.Cookie!.Value);
        }

        /// <summary>
        /// Opens the consent page and allows it: the signed state Entra receives and the consent cookie's nonce.
        /// </summary>
        protected (string State, string Nonce) AllowConsent(string query)
        {
            var (fields, nonce) = this.OpenConsent(query);
            var (_, parameters) = SplitLocation(this.Broker.AuthorizeConsent(ApiBase, ConsentForm.Post(fields), nonce).Location);
            return (parameters["state"], nonce);
        }

        protected static void AssertInvalidPage(McpOAuthResult result)
        {
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(result.Location, Is.Null);
            Assert.That(result.ContentType, Is.EqualTo("text/html; charset=utf-8"));
            Assert.That(result.Body, Does.Contain(ConsentPage.InvalidRequestMessage));
        }

        /// <summary>
        /// Splits a redirect URL into the part before <c>?</c> and its decoded parameters.
        /// </summary>
```

Modify `SafeExchange.Tests\Mcp\OAuth\McpOAuthHandlerTests.cs` (6 edits):

Edit 1 of 6. Replace:

```csharp
    using System.Threading.Tasks;

    /// <summary>
    /// The Functions adapter: request URL and body in, status, <c>Location</c>, JSON body and
    /// <c>Cache-Control: no-store</c> out; a bare 404 while the broker is off.
    /// </summary>
    [TestFixture]
    public class McpOAuthHandlerTests : McpOAuthHttpTestBase
```

with:

```csharp
    using System.Threading.Tasks;

    /// <summary>
    /// The Functions adapter: request URL, body and cookie in; status, <c>Location</c>, <c>Set-Cookie</c>, a JSON or HTML
    /// body (HTML with the security headers) and <c>Cache-Control: no-store</c> out; a bare 404 while the broker is off.
    /// </summary>
    [TestFixture]
    public class McpOAuthHandlerTests : McpOAuthHttpTestBase
```

Edit 2 of 6. Replace:

```csharp
        }

        [Test]
        public async Task Authorize_AnswersFound_WithTheEntraLocationAndNoBody()
        {
            var query = $"oauth/authorize?client_id={BrokerClientId}&redirect_uri=http%3A%2F%2F127.0.0.1%3A53682%2Fcallback&response_type=code"
                + $"&code_challenge={Challenge}&code_challenge_method=S256&state=abc&resource=https%3A%2F%2Fsafeexchange.test%2Fapi%2Fmcp";

            var response = await this.OAuthHandler.Authorize(OAuthRequest("get", query));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(response.Headers.GetValues("Cache-Control"), Is.EqualTo(new[] { "no-store" }));
            Assert.That(response.Headers.GetValues("Location").Single(), Does.StartWith($"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/authorize?client_id={BrokerClientId}&"));
            Assert.That(response.Headers.Contains("Content-Type"), Is.False);
            Assert.That(BodyOf(response), Is.Empty);
        }

        [Test]
        public async Task Authorize_WithANonLoopbackRedirect_AnswersBadRequest_WithoutALocation()
        {
```

with:

```csharp
        }

        [Test]
        public async Task Authorize_AnswersTheConsentPage_AsUncacheableHtmlWithTheSecurityHeaders_AndSetsTheCookie()
        {
            var response = await this.OAuthHandler.Authorize(OAuthRequest("get", AuthorizeQuery + "&resource=https%3A%2F%2Fsafeexchange.test%2Fapi%2Fmcp"));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            AssertHtmlNoStore(response);
            Assert.That(response.Headers.GetValues("Set-Cookie").Single(), Does.Match("^__Host-saex-mcp-consent=[A-Za-z0-9_-]{43}; Max-Age=600; Path=/; Secure; HttpOnly; SameSite=Lax$"));
            Assert.That(response.Headers.Contains("Location"), Is.False);
            Assert.That(BodyOf(response), Does.Contain("<form method=\"post\" action=\"https://safeexchange.test/api/oauth/authorize\">"));
        }

        [Test]
        public async Task AuthorizePost_WithTheCookie_RedirectsToEntra()
        {
            var page = await this.OAuthHandler.Authorize(OAuthRequest("get", AuthorizeQuery));
            var form = ConsentForm.Post(ConsentForm.HiddenFields(BodyOf(page)));

            var response = await this.OAuthHandler.Authorize(OAuthRequest("post", "oauth/authorize", form, "theme=dark; " + CookieFrom(page)));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(response.Headers.GetValues("Cache-Control"), Is.EqualTo(new[] { "no-store" }));
            Assert.That(response.Headers.GetValues("Location").Single(), Does.StartWith($"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/authorize?client_id={BrokerClientId}&"));
            Assert.That(response.Headers.Contains("Set-Cookie"), Is.False);
            Assert.That(response.Headers.Contains("Content-Type"), Is.False);
            Assert.That(BodyOf(response), Is.Empty);
        }

        [Test]
        public async Task AuthorizePost_WithoutTheCookie_IsTheInvalidPage()
        {
            var page = await this.OAuthHandler.Authorize(OAuthRequest("get", AuthorizeQuery));

            var response = await this.OAuthHandler.Authorize(OAuthRequest("post", "oauth/authorize", ConsentForm.Post(ConsentForm.HiddenFields(BodyOf(page)))));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            AssertHtmlNoStore(response);
            Assert.That(response.Headers.Contains("Location"), Is.False);
            Assert.That(BodyOf(response), Does.Contain(ConsentPage.InvalidRequestMessage));
        }

        [Test]
        public async Task Authorize_WithANonLoopbackRedirect_AnswersBadRequest_WithoutALocation()
        {
```

Edit 3 of 6. Replace:

```csharp
        }

        [Test]
        public async Task Callback_AnswersFound_ToTheClientsLoopbackRedirect()
        {
            Assert.That(new BrokerState("http://127.0.0.1:53682/callback", "abc").TryEncode(out var state), Is.True);

            var response = await this.OAuthHandler.Callback(OAuthRequest("get", $"oauth/callback?code=the-code&state={state}"));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(response.Headers.GetValues("Location"), Is.EqualTo(new[] { "http://127.0.0.1:53682/callback?code=the-code&state=abc" }));
            Assert.That(response.Headers.GetValues("Cache-Control"), Is.EqualTo(new[] { "no-store" }));
        }

        [Test]
        public async Task Token_RelaysEntrasStatusAndBody_AsUncacheableJson()
        {
```

with:

```csharp
        }

        [Test]
        public async Task Callback_ForwardsToTheClientsLoopbackRedirect_AndExpiresTheCookie()
        {
            var nonce = ConsentToken.NewNonce();
            Assert.That(SignedBrokerState.TryCreate(McpOAuthTestKey.Bytes, "http://127.0.0.1:53682/callback", "abc", nonce, DateTime.UtcNow, out var state), Is.True);
            var request = OAuthRequest("get", $"oauth/callback?code=the-code&state={state}", cookie: "theme=dark");
            request.Headers.Add("Cookie", $"{ConsentCookie.Name}={nonce}");

            var response = await this.OAuthHandler.Callback(request);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(response.Headers.GetValues("Location"), Is.EqualTo(new[] { "http://127.0.0.1:53682/callback?code=the-code&state=abc" }));
            Assert.That(response.Headers.GetValues("Cache-Control"), Is.EqualTo(new[] { "no-store" }));
            Assert.That(response.Headers.GetValues("Set-Cookie"), Is.EqualTo(new[] { "__Host-saex-mcp-consent=; Max-Age=0; Path=/; Secure; HttpOnly; SameSite=Lax" }));
        }
        [Test]
        public async Task Token_RelaysEntrasStatusAndBody_AsUncacheableJson()
        {
```

Edit 4 of 6. Replace:

```csharp
            Assert.That(this.Entra.Requests, Is.Empty);
        }

        [TestCase("False", BrokerClientId)]
        [TestCase("True", "")]
        public async Task EveryRoute_IsABareNotFound_WhileTheBrokerIsOff(string mcpEndpoint, string clientId)
        {
            this.BuildProvider(("Features:McpEndpoint", mcpEndpoint), ("Mcp:OAuth:ClientId", clientId));
            var handler = this.OAuthHandler;

            var responses = new[]
```

with:

```csharp
            Assert.That(this.Entra.Requests, Is.Empty);
        }

        [TestCase("False", BrokerClientId, McpOAuthTestKey.Value)]
        [TestCase("True", "", McpOAuthTestKey.Value)]
        [TestCase("True", BrokerClientId, "")]
        public async Task EveryRoute_IsABareNotFound_WhileTheBrokerIsOff(string mcpEndpoint, string clientId, string stateKey)
        {
            this.BuildProvider(("Features:McpEndpoint", mcpEndpoint), ("Mcp:OAuth:ClientId", clientId), ("Mcp:OAuth:StateKey", stateKey));
            var handler = this.OAuthHandler;

            var responses = new[]
```

Edit 5 of 6. Replace:

```csharp
                await handler.Metadata(OAuthRequest("get", ".well-known/openid-configuration")),
                await handler.Register(OAuthRequest("post", "oauth/register", "{\"redirect_uris\":[\"http://127.0.0.1:1/cb\"]}")),
                await handler.Authorize(OAuthRequest("get", $"oauth/authorize?client_id={BrokerClientId}")),
                await handler.Callback(OAuthRequest("get", "oauth/callback?code=c")),
                await handler.Token(OAuthRequest("post", "oauth/token", "grant_type=refresh_token&refresh_token=r")),
            };
```

with:

```csharp
                await handler.Metadata(OAuthRequest("get", ".well-known/openid-configuration")),
                await handler.Register(OAuthRequest("post", "oauth/register", "{\"redirect_uris\":[\"http://127.0.0.1:1/cb\"]}")),
                await handler.Authorize(OAuthRequest("get", $"oauth/authorize?client_id={BrokerClientId}")),
                await handler.Authorize(OAuthRequest("post", "oauth/authorize", $"client_id={BrokerClientId}&decision=allow")),
                await handler.Callback(OAuthRequest("get", "oauth/callback?code=c")),
                await handler.Token(OAuthRequest("post", "oauth/token", "grant_type=refresh_token&refresh_token=r")),
            };
```

Edit 6 of 6. Replace:

```csharp
            Assert.That(this.Entra.Requests, Is.Empty);
        }

        private static void AssertJsonNoStore(HttpResponseData response)
        {
            Assert.That(response.Headers.GetValues("Content-Type"), Is.EqualTo(new[] { "application/json" }));
```

with:

```csharp
            Assert.That(this.Entra.Requests, Is.Empty);
        }

        private const string AuthorizeQuery = "oauth/authorize?client_id=" + BrokerClientId + "&redirect_uri=http%3A%2F%2F127.0.0.1%3A53682%2Fcallback"
            + "&response_type=code&code_challenge=" + Challenge + "&code_challenge_method=S256&state=abc";

        private static void AssertHtmlNoStore(HttpResponseData response)
        {
            Assert.That(response.Headers.GetValues("Content-Type"), Is.EqualTo(new[] { "text/html; charset=utf-8" }));
            Assert.That(response.Headers.GetValues("Cache-Control"), Is.EqualTo(new[] { "no-store" }));
            Assert.That(response.Headers.GetValues("Content-Security-Policy"), Is.EqualTo(new[] { "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'" }));
            Assert.That(response.Headers.GetValues("X-Frame-Options"), Is.EqualTo(new[] { "DENY" }));
            Assert.That(response.Headers.GetValues("X-Content-Type-Options"), Is.EqualTo(new[] { "nosniff" }));
            Assert.That(response.Headers.GetValues("Referrer-Policy"), Is.EqualTo(new[] { "no-referrer" }));
        }

        private static void AssertJsonNoStore(HttpResponseData response)
        {
            Assert.That(response.Headers.GetValues("Content-Type"), Is.EqualTo(new[] { "application/json" }));
```

Modify `SafeExchange.Tests\Mcp\OAuth\McpOAuthHttpTestBase.cs` (3 edits):

Edit 1 of 3. Replace:

```csharp

namespace SafeExchange.Tests.Mcp.OAuth
{
    using Microsoft.Extensions.DependencyInjection;
    using NUnit.Framework;
    using SafeExchange.Core.Functions.Mcp.OAuth;
```

with:

```csharp

namespace SafeExchange.Tests.Mcp.OAuth
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.DependencyInjection;
    using NUnit.Framework;
    using SafeExchange.Core.Functions.Mcp.OAuth;
```

Edit 2 of 3. Replace:

```csharp
    using SafeExchange.Tests.Utilities;
    using System;
    using System.IO;
    using System.Text;

    /// <summary>
```

with:

```csharp
    using SafeExchange.Tests.Utilities;
    using System;
    using System.IO;
    using System.Linq;
    using System.Text;

    /// <summary>
```

Edit 3 of 3. Replace:

```csharp
            services.AddScoped<McpOAuthHandler>();
        }

        protected static TestHttpRequestData OAuthRequest(string method, string pathAndQuery, string? body = null)
        {
            var request = TestFactory.CreateHttpRequestData(method);
            request.SetUrl(new Uri(ApiBase, pathAndQuery));
            request.SetBodyAsStream(new MemoryStream(Encoding.UTF8.GetBytes(body ?? string.Empty)));
            return request;
        }
    }
}
```

with:

```csharp
            services.AddScoped<McpOAuthHandler>();
        }

        protected static TestHttpRequestData OAuthRequest(string method, string pathAndQuery, string? body = null, string? cookie = null)
        {
            var request = TestFactory.CreateHttpRequestData(method);
            request.SetUrl(new Uri(ApiBase, pathAndQuery));
            request.SetBodyAsStream(new MemoryStream(Encoding.UTF8.GetBytes(body ?? string.Empty)));
            if (cookie is not null)
            {
                request.Headers.Add("Cookie", cookie);
            }

            return request;
        }

        /// <summary>
        /// The <c>name=value</c> part of the response's <c>Set-Cookie</c>, as a browser sends it back.
        /// </summary>
        protected static string CookieFrom(HttpResponseData response)
            => response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }
}
```

Replace the whole content of `SafeExchange.Tests\Mcp\OAuth\McpOAuthLoggingTests.cs` with:

```csharp
/// <summary>
/// McpOAuthLoggingTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// Runs register, authorize (page, refused and allowed consent), callback (refused, success and Entra error) and token
    /// (code, refresh, Entra error) through the adapter with every log category captured, and checks that no code,
    /// verifier, challenge, token, client state, consent nonce, consent token, signed state or Entra error description
    /// reached a log, while routes and outcomes (refusals at Warning) did.
    /// </summary>
    [TestFixture]
    public class McpOAuthLoggingTests : McpOAuthHttpTestBase
    {
        private const string ClientState = "Client-State-For-The-Log-Test";

        private const string Code = "Authorization-Code-For-The-Log-Test";

        private const string Verifier = "Code-Verifier-For-The-Log-Test-0123456789abcdef";

        private const string Challenge = "Code-Challenge-For-The-Log-Test-0123456789abcd";

        private const string EntraDescription = "AADSTS70008 Description-For-The-Log-Test";

        [Test]
        public async Task AFullSignIn_LogsOutcomesButNoCodeVerifierTokenStateCookieOrNonce()
        {
            var handler = this.OAuthHandler;
            await handler.Register(OAuthRequest("post", "oauth/register", "{\"redirect_uris\":[\"http://127.0.0.1:53682/callback\"]}"));
            var page = await handler.Authorize(OAuthRequest("get",
                $"oauth/authorize?client_id={BrokerClientId}&redirect_uri=http%3A%2F%2F127.0.0.1%3A53682%2Fcallback&response_type=code"
                + $"&code_challenge={Challenge}&code_challenge_method=S256&state={ClientState}"));
            var cookie = CookieFrom(page);
            var nonce = cookie[(ConsentCookie.Name.Length + 1)..];
            var fields = ConsentForm.HiddenFields(BodyOf(page));
            var consentToken = fields.Single(f => f.Key == "consent_token").Value;
            await handler.Authorize(OAuthRequest("post", "oauth/authorize", ConsentForm.Post(fields), "theme=dark"));
            await handler.Callback(OAuthRequest("get", $"oauth/callback?code={Code}&state=forged"));
            var allow = await handler.Authorize(OAuthRequest("post", "oauth/authorize", ConsentForm.Post(fields), cookie));
            Assert.That(OAuthParameters.TryParse(new Uri(allow.Headers.GetValues("Location").Single()).Query, out var entraParameters), Is.True);
            var signedState = entraParameters!["state"];
            await handler.Callback(OAuthRequest("get", $"oauth/callback?code={Code}&state={signedState}"));
            var callback = await handler.Callback(OAuthRequest("get", $"oauth/callback?code={Code}&state={signedState}", cookie: cookie));
            await handler.Callback(OAuthRequest("get", $"oauth/callback?error=access_denied&error_description={Uri.EscapeDataString(EntraDescription)}&state={signedState}", cookie: cookie));
            await handler.Token(OAuthRequest("post", "oauth/token", $"grant_type=authorization_code&code={Code}&code_verifier={Verifier}&client_id={BrokerClientId}"));
            await handler.Token(OAuthRequest("post", "oauth/token", $"grant_type=refresh_token&refresh_token={FakeEntraTokenClient.RefreshToken}&client_id={BrokerClientId}"));
            this.Entra.Response = new EntraTokenResponse(HttpStatusCode.BadRequest, $"{{\"error\":\"invalid_grant\",\"error_description\":\"{EntraDescription}\"}}");
            await handler.Token(OAuthRequest("post", "oauth/token", $"grant_type=refresh_token&refresh_token={FakeEntraTokenClient.RefreshToken}&client_id={BrokerClientId}"));
            Assert.That(callback.Headers.GetValues("Location").Single(), Does.Contain(Code), "Arrange: the flow moved the code.");

            var logs = string.Join('\n', this.Logs.Entries);
            Assert.That(logs, Does.Contain("MCP OAuth register answered 201"));
            Assert.That(logs, Does.Contain("MCP OAuth authorize answered the consent page."));
            Assert.That(logs, Does.Contain("Warning SafeExchange.Core.Mcp.OAuth.McpOAuthBroker: MCP OAuth authorize refused the request: missing_cookie."));
            Assert.That(logs, Does.Contain("Warning SafeExchange.Core.Mcp.OAuth.McpOAuthBroker: MCP OAuth callback refused the request: invalid_state."));
            Assert.That(logs, Does.Contain("Warning SafeExchange.Core.Mcp.OAuth.McpOAuthBroker: MCP OAuth callback refused the request: missing_cookie."));
            Assert.That(logs, Does.Contain("MCP OAuth authorize redirected to Entra."));
            Assert.That(logs, Does.Contain("MCP OAuth callback forwarded the authorization code to the client."));
            Assert.That(logs, Does.Contain("MCP OAuth callback forwarded Entra error access_denied to the client."));
            Assert.That(logs, Does.Contain("MCP OAuth token (authorization_code) relayed Entra status 200"));
            Assert.That(logs, Does.Contain("MCP OAuth token (refresh_token) relayed Entra status 400 (error: invalid_grant)."));
            foreach (var forbidden in new[] { Code, Verifier, Challenge, ClientState, nonce, consentToken, signedState, FakeEntraTokenClient.AccessToken, FakeEntraTokenClient.RefreshToken, EntraDescription })
            {
                Assert.That(logs, Does.Not.Contain(forbidden), $"'{forbidden}' was logged.");
            }
        }    }
}
```


- [ ] **Step 2: Run the test build to verify it fails**

Run: `dotnet build SafeExchange.Tests\SafeExchange.Tests.csproj --nologo -v q`
Expected: FAIL, first errors:

```
SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTestBase.cs(96,63): error CS1061: 'McpOAuthResult' does not contain a definition for 'Cookie' and no accessible extension method 'Cookie' accepting a first argument of type 'McpOAuthResult' could be found (are you missing a using directive or an assembly reference?)
SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTestBase.cs(105,61): error CS1061: 'McpOAuthBroker' does not contain a definition for 'AuthorizeConsent' …
SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTestBase.cs(113,32): error CS1061: 'McpOAuthResult' does not contain a definition for 'ContentType' …
SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerCallbackTests.cs(27,38): error CS1501: No overload for method 'Callback' takes 2 arguments
```

- [ ] **Step 3: Implement**

`McpOAuthResult` gains the content type, the cookie and `Retry-After`; the broker file and the adapter are shown whole
because most of their authorize/callback code is new.

Replace the whole content of `SafeExchange.Core.Functions\Mcp\OAuth\McpOAuthHandler.cs` with:

```csharp
/// <summary>
/// McpOAuthHandler
/// </summary>

namespace SafeExchange.Core.Functions.Mcp.OAuth
{
    using Microsoft.Azure.Functions.Worker.Http;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Globalization;
    using System.IO;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// Functions adapters for the anonymous OAuth broker routes: request URL, body and consent cookie into
    /// <see cref="McpOAuthBroker"/>, its result out as status, <c>Location</c>, <c>Set-Cookie</c>, <c>Retry-After</c>,
    /// a JSON or HTML body (HTML with <see cref="ConsentPage.SecurityHeaders"/>) and <c>Cache-Control: no-store</c>.
    /// A disabled broker answers a bare 404.
    /// </summary>
    public sealed class McpOAuthHandler
    {
        private readonly McpOAuthBroker broker;

        public McpOAuthHandler(McpOAuthBroker broker)
        {
            this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
        }

        public Task<HttpResponseData> Metadata(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return WriteAsync(request, this.broker.Metadata(McpHandler.ApiBaseUri(request.Url)));
        }

        public async Task<HttpResponseData> Register(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return await WriteAsync(request, this.broker.Register(await ReadBodyAsync(request)));
        }

        /// <summary>
        /// <c>GET</c> answers the consent page; <c>POST</c> is the consent form.
        /// </summary>
        public async Task<HttpResponseData> Authorize(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var apiBaseUri = McpHandler.ApiBaseUri(request.Url);
            var result = string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
                ? this.broker.AuthorizeConsent(apiBaseUri, await ReadBodyAsync(request), ReadConsentCookie(request))
                : this.broker.Authorize(apiBaseUri, request.Url.Query);
            return await WriteAsync(request, result);
        }

        public Task<HttpResponseData> Callback(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return WriteAsync(request, this.broker.Callback(request.Url.Query, ReadConsentCookie(request)));
        }

        public async Task<HttpResponseData> Token(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var result = await this.broker.TokenAsync(McpHandler.ApiBaseUri(request.Url), await ReadBodyAsync(request), request.FunctionContext.CancellationToken);
            return await WriteAsync(request, result);
        }

        // HTTP/2 may split Cookie into several header fields; they are joined with "; " (RFC 9113 §8.2.3).
        private static string? ReadConsentCookie(HttpRequestData request)
            => request.Headers.TryGetValues("Cookie", out var values) ? ConsentCookie.Read(string.Join("; ", values)) : null;

        // Reads one character more than the broker accepts, so an oversize body is refused without buffering all of it.
        private static async Task<string> ReadBodyAsync(HttpRequestData request)
        {
            using var reader = new StreamReader(request.Body ?? Stream.Null);
            var buffer = new char[McpOAuthBroker.MaxBodyLength + 1];
            var length = 0;
            int read;
            while (length < buffer.Length && (read = await reader.ReadAsync(buffer.AsMemory(length))) > 0)
            {
                length += read;
            }

            return new string(buffer, 0, length);
        }

        private static async Task<HttpResponseData> WriteAsync(HttpRequestData request, McpOAuthResult result)
        {
            var response = request.CreateResponse(result.StatusCode);
            if (result == McpOAuthResult.NotFound)
            {
                return response;
            }

            response.Headers.Add("Cache-Control", "no-store");
            if (result.Location is not null)
            {
                response.Headers.Add("Location", result.Location);
            }

            if (result.Cookie is not null)
            {
                response.Headers.Add("Set-Cookie", result.Cookie.ToSetCookieHeader());
            }

            if (result.RetryAfterSeconds is int retryAfter)
            {
                response.Headers.Add("Retry-After", retryAfter.ToString(CultureInfo.InvariantCulture));
            }

            if (result.Body is not null)
            {
                response.Headers.Add("Content-Type", result.ContentType);
                if (result.IsHtml)
                {
                    foreach (var header in ConsentPage.SecurityHeaders)
                    {
                        response.Headers.Add(header.Key, header.Value);
                    }
                }

                await response.WriteStringAsync(result.Body);
            }

            return response;
        }
    }
}
```

Delete `SafeExchange.Core\Mcp\OAuth\BrokerState.cs`: `git rm SafeExchange.Core\Mcp\OAuth\BrokerState.cs`.

Replace the whole content of `SafeExchange.Core\Mcp\OAuth\McpOAuthBroker.cs` with:

```csharp
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
```

Replace the whole content of `SafeExchange.Core\Mcp\OAuth\McpOAuthResult.cs` with:

```csharp
/// <summary>
/// McpOAuthResult
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System.Net;
    using System.Text.Json.Nodes;

    /// <summary>
    /// A host-agnostic broker response: a status, an optional body (JSON unless <see cref="ContentType"/> says HTML), an
    /// optional redirect <see cref="Location"/>, an optional <see cref="ConsentCookie"/> to set and an optional
    /// <c>Retry-After</c> in seconds.
    /// </summary>
    public sealed record McpOAuthResult(HttpStatusCode StatusCode, string? Body = null, string? Location = null)
    {
        public const string JsonContentType = "application/json";

        public const string HtmlContentType = "text/html; charset=utf-8";

        public static McpOAuthResult NotFound { get; } = new(HttpStatusCode.NotFound);

        public string ContentType { get; init; } = JsonContentType;

        public ConsentCookie? Cookie { get; init; }

        public int? RetryAfterSeconds { get; init; }

        public bool IsHtml => this.ContentType == HtmlContentType;

        public static McpOAuthResult Json(HttpStatusCode statusCode, string body) => new(statusCode, body);

        public static McpOAuthResult Html(HttpStatusCode statusCode, string html) => new(statusCode, html) { ContentType = HtmlContentType };

        public static McpOAuthResult Redirect(string location) => new(HttpStatusCode.Found, null, location);

        public static McpOAuthResult Error(HttpStatusCode statusCode, string error, string description)
            => Json(statusCode, new JsonObject { ["error"] = error, ["error_description"] = description }.ToJsonString());
    }
}
```

Modify `SafeExchange.Functions\Functions\SafeMcpOAuth.cs`:

Replace:

```csharp

        [Function(McpRoutes.OAuthAuthorizeFunctionName)]
        public Task<HttpResponseData> Authorize(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = McpRoutes.ApiRoutePrefix + McpRoutes.OAuthAuthorize)]
            HttpRequestData request)
            => this.handler.Authorize(request);
```

with:

```csharp

        [Function(McpRoutes.OAuthAuthorizeFunctionName)]
        public Task<HttpResponseData> Authorize(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = McpRoutes.ApiRoutePrefix + McpRoutes.OAuthAuthorize)]
            HttpRequestData request)
            => this.handler.Authorize(request);
```


- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build SafeExchange.slnx --nologo -v q`, then the task filter and the phase filter.
Expected: `0 Error(s)`; task filter `Passed:   379`; phase filter `Passed:   612` (with the emulator; see ground rule 6).

- [ ] **Step 5: Commit**

```powershell
git add SafeExchange.Core.Functions\Mcp\OAuth\McpOAuthHandler.cs SafeExchange.Core\Mcp\OAuth\McpOAuthBroker.cs SafeExchange.Core\Mcp\OAuth\McpOAuthResult.cs SafeExchange.Functions\Functions\SafeMcpOAuth.cs SafeExchange.Tests\Contract\FunctionRouteTests.cs SafeExchange.Tests\Mcp\OAuth\ConsentForm.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerAuthorizeTests.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerCallbackTests.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerConsentTests.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerGatingTests.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTestBase.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthHandlerTests.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthHttpTestBase.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthLoggingTests.cs
git commit -m "feat(mcp-oauth): user consent page, consent-bound signed state, no prompt passthrough" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
git status --short
```


---

## Task 5: Token-bucket throttle and Entra call gate

Pure building blocks plus the policy service and their registrations (D8, D9). The adapter starts using the throttle
in Task 8, the broker the gate in Task 6.

**Files:**
- Modify: `SafeExchange.Core\Mcp\McpServiceRegistration.cs`
- Create: `SafeExchange.Core\Mcp\OAuth\ClientRateLimiter.cs`
- Create: `SafeExchange.Core\Mcp\OAuth\EntraCallGate.cs`
- Create: `SafeExchange.Core\Mcp\OAuth\McpOAuthThrottle.cs`
- Test (create): `SafeExchange.Tests\Mcp\OAuth\ClientRateLimiterTests.cs`
- Test (create): `SafeExchange.Tests\Mcp\OAuth\EntraCallGateTests.cs`
- Test (modify): `SafeExchange.Tests\Mcp\OAuth\EntraTokenClientTests.cs`
- Test (create): `SafeExchange.Tests\Mcp\OAuth\McpOAuthThrottleTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `SafeExchange.Tests\Mcp\OAuth\ClientRateLimiterTests.cs`:

```csharp
/// <summary>
/// ClientRateLimiterTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System;

    [TestFixture]
    public class ClientRateLimiterTests
    {
        private static readonly DateTime Start = new(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);

        [Test]
        public void TheBurst_IsAllowed_ThenTheNextRequestWaitsForTheRefill()
        {
            var limiter = new ClientRateLimiter(10);

            for (var i = 0; i < 3; i++)
            {
                Assert.That(limiter.TryAcquire("a", 60, 3, Start, out _), Is.True, $"request {i + 1}");
            }

            Assert.That(limiter.TryAcquire("a", 60, 3, Start, out var retryAfter), Is.False);
            Assert.That(retryAfter.TotalSeconds, Is.EqualTo(1).Within(0.001));
            Assert.That(limiter.TryAcquire("a", 60, 3, Start.AddSeconds(1), out _), Is.True);
            Assert.That(limiter.TryAcquire("a", 60, 3, Start.AddSeconds(1), out _), Is.False);
        }

        [Test]
        public void AnIdleBucket_RefillsOnlyUpToTheBurst()
        {
            var limiter = new ClientRateLimiter(10);
            Assert.That(limiter.TryAcquire("a", 60, 2, Start, out _), Is.True);

            var later = Start.AddHours(1);
            Assert.That(limiter.TryAcquire("a", 60, 2, later, out _), Is.True);
            Assert.That(limiter.TryAcquire("a", 60, 2, later, out _), Is.True);
            Assert.That(limiter.TryAcquire("a", 60, 2, later, out _), Is.False);
        }

        [Test]
        public void ASlowRate_GivesALongRetryAfter()
        {
            var limiter = new ClientRateLimiter(10);
            Assert.That(limiter.TryAcquire("a", 1, 1, Start, out _), Is.True);

            Assert.That(limiter.TryAcquire("a", 1, 1, Start.AddSeconds(15), out var retryAfter), Is.False);
            Assert.That(retryAfter.TotalSeconds, Is.EqualTo(45).Within(0.001));
        }

        [Test]
        public void EveryKey_HasItsOwnBucket()
        {
            var limiter = new ClientRateLimiter(10);
            Assert.That(limiter.TryAcquire("a", 60, 1, Start, out _), Is.True);
            Assert.That(limiter.TryAcquire("a", 60, 1, Start, out _), Is.False);

            Assert.That(limiter.TryAcquire("b", 60, 1, Start, out _), Is.True);
        }

        [Test]
        public void TheStore_IsBounded_AndDropsTheLeastRecentlyUsedBucket()
        {
            var limiter = new ClientRateLimiter(2);
            Assert.That(limiter.TryAcquire("a", 60, 1, Start, out _), Is.True);
            Assert.That(limiter.TryAcquire("b", 60, 1, Start, out _), Is.True);
            Assert.That(limiter.TryAcquire("a", 60, 1, Start, out _), Is.False, "a is now the most recently used");

            Assert.That(limiter.TryAcquire("c", 60, 1, Start, out _), Is.True);

            Assert.That(limiter.Count, Is.EqualTo(2));
            Assert.That(limiter.TryAcquire("a", 60, 1, Start, out _), Is.False, "a was kept");
            Assert.That(limiter.TryAcquire("b", 60, 1, Start, out _), Is.True, "b was dropped and starts with a full bucket");
        }

        [Test]
        public void AClockGoingBackwards_DoesNotRefill()
        {
            var limiter = new ClientRateLimiter(10);
            Assert.That(limiter.TryAcquire("a", 60, 1, Start, out _), Is.True);

            Assert.That(limiter.TryAcquire("a", 60, 1, Start.AddMinutes(-5), out _), Is.False);
            Assert.That(limiter.TryAcquire("a", 60, 1, Start.AddSeconds(1), out _), Is.True);
        }
    }
}
```

Create `SafeExchange.Tests\Mcp\OAuth\EntraCallGateTests.cs`:

```csharp
/// <summary>
/// EntraCallGateTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Threading.Tasks;

    [TestFixture]
    public class EntraCallGateTests
    {
        [Test]
        public async Task AtMostTheConfiguredCallsRun_AndAFreedSlotIsReused()
        {
            var gate = new EntraCallGate(2, TimeSpan.FromMilliseconds(20));

            Assert.That(await gate.TryEnterAsync(), Is.True);
            Assert.That(await gate.TryEnterAsync(), Is.True);
            Assert.That(await gate.TryEnterAsync(), Is.False);
            gate.Exit();
            Assert.That(await gate.TryEnterAsync(), Is.True);
        }

        [Test]
        public async Task AWaitingCall_GetsASlotFreedWithinTheWait()
        {
            var gate = new EntraCallGate(1, TimeSpan.FromSeconds(10));
            Assert.That(await gate.TryEnterAsync(), Is.True);

            var waiting = gate.TryEnterAsync();
            gate.Exit();

            Assert.That(await waiting, Is.True);
        }
    }
}
```

Modify `SafeExchange.Tests\Mcp\OAuth\EntraTokenClientTests.cs`:

Replace:

```csharp

            Assert.That(provider.GetRequiredService<IEntraTokenClient>(), Is.InstanceOf<EntraTokenClient>());
            Assert.That(provider.GetRequiredService<McpOAuthBroker>(), Is.SameAs(provider.GetRequiredService<McpOAuthBroker>()));
            Assert.That(provider.GetRequiredService<IHttpClientFactory>().CreateClient(EntraTokenClient.HttpClientName).Timeout, Is.EqualTo(TimeSpan.FromSeconds(10)));
            var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(EntraTokenClient.HttpClientName);
            while (handler is DelegatingHandler delegating)
```

with:

```csharp

            Assert.That(provider.GetRequiredService<IEntraTokenClient>(), Is.InstanceOf<EntraTokenClient>());
            Assert.That(provider.GetRequiredService<McpOAuthBroker>(), Is.SameAs(provider.GetRequiredService<McpOAuthBroker>()));
            Assert.That(provider.GetRequiredService<McpOAuthThrottle>(), Is.SameAs(provider.GetRequiredService<McpOAuthThrottle>()));
            Assert.That(provider.GetRequiredService<EntraCallGate>(), Is.SameAs(provider.GetRequiredService<EntraCallGate>()));
            Assert.That(provider.GetRequiredService<IHttpClientFactory>().CreateClient(EntraTokenClient.HttpClientName).Timeout, Is.EqualTo(TimeSpan.FromSeconds(10)));
            var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(EntraTokenClient.HttpClientName);
            while (handler is DelegatingHandler delegating)
```

Create `SafeExchange.Tests\Mcp\OAuth\McpOAuthThrottleTests.cs`:

```csharp
/// <summary>
/// McpOAuthThrottleTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using SafeExchange.Tests.Utilities;
    using System.Linq;
    using System.Net;
    using System.Text.Json.Nodes;

    /// <summary>
    /// Per endpoint and client address: the burst passes, then 429 with <c>Retry-After</c>; JSON <c>slow_down</c> for
    /// register and token, the HTML page for the browser endpoints. Refill is 1 per minute here, so it does not refill
    /// while a test runs.
    /// </summary>
    [TestFixture]
    public class McpOAuthThrottleTests
    {
        private const string Address = "203.0.113.7";

        private RecordingLoggerProvider logs = null!;

        private McpOAuthThrottle throttle = null!;

        [SetUp]
        public void SetUp()
        {
            var options = new McpOAuthOptions
            {
                RateLimit = new McpOAuthRateLimitOptions { TokenPermitsPerMinute = 1, TokenBurst = 1, PermitsPerMinute = 1, Burst = 2 },
            };
            this.logs = new RecordingLoggerProvider();
            this.throttle = new McpOAuthThrottle(
                Mock.Of<IOptionsMonitor<McpOAuthOptions>>(x => x.CurrentValue == options),
                new LoggerFactory(new[] { this.logs }).CreateLogger<McpOAuthThrottle>());
        }

        [Test]
        public void Token_AfterItsBurst_IsSlowDownJson_WithRetryAfter_AndAWarning()
        {
            Assert.That(this.throttle.Check(McpOAuthThrottle.Token, Address), Is.Null);

            var limited = this.throttle.Check(McpOAuthThrottle.Token, Address)!;

            Assert.That(limited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(limited.ContentType, Is.EqualTo("application/json"));
            Assert.That(JsonNode.Parse(limited.Body!)!["error"]!.GetValue<string>(), Is.EqualTo("slow_down"));
            Assert.That(limited.RetryAfterSeconds, Is.InRange(59, 60));
            Assert.That(this.logs.Entries.Single(), Does.StartWith("Warning SafeExchange.Core.Mcp.OAuth.McpOAuthThrottle: MCP OAuth token rate limit exceeded; retry after"));
            Assert.That(this.logs.Entries.Single(), Does.Not.Contain(Address));
        }

        [TestCase(McpOAuthThrottle.Authorize)]
        [TestCase(McpOAuthThrottle.Callback)]
        public void TheBrowserEndpoints_AfterTheirBurst_AreTheTooManyRequestsPage(string endpoint)
        {
            Assert.That(this.throttle.Check(endpoint, Address), Is.Null);
            Assert.That(this.throttle.Check(endpoint, Address), Is.Null);

            var limited = this.throttle.Check(endpoint, Address)!;

            Assert.That(limited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(limited.ContentType, Is.EqualTo("text/html; charset=utf-8"));
            Assert.That(limited.Body, Does.Contain(ConsentPage.TooManyRequestsMessage));
            Assert.That(limited.RetryAfterSeconds, Is.InRange(59, 60));
        }

        [Test]
        public void Register_AfterItsBurst_IsSlowDownJson()
        {
            this.throttle.Check(McpOAuthThrottle.Register, Address);
            this.throttle.Check(McpOAuthThrottle.Register, Address);

            var limited = this.throttle.Check(McpOAuthThrottle.Register, Address)!;

            Assert.That(limited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(JsonNode.Parse(limited.Body!)!["error"]!.GetValue<string>(), Is.EqualTo("slow_down"));
        }

        [Test]
        public void EveryEndpointAndAddress_HasItsOwnBucket_AndNoAddressSharesOne()
        {
            Assert.That(this.throttle.Check(McpOAuthThrottle.Token, Address), Is.Null);
            Assert.That(this.throttle.Check(McpOAuthThrottle.Token, Address), Is.Not.Null);

            Assert.That(this.throttle.Check(McpOAuthThrottle.Token, "198.51.100.1"), Is.Null);
            Assert.That(this.throttle.Check(McpOAuthThrottle.Register, Address), Is.Null);
            Assert.That(this.throttle.Check(McpOAuthThrottle.Token, null), Is.Null);
            Assert.That(this.throttle.Check(McpOAuthThrottle.Token, string.Empty), Is.Not.Null);
        }
    }
}
```


- [ ] **Step 2: Run the test build to verify it fails**

Run: `dotnet build SafeExchange.Tests\SafeExchange.Tests.csproj --nologo -v q`
Expected: FAIL, first errors:

```
SafeExchange.Tests\Mcp\OAuth\McpOAuthThrottleTests.cs(29,17): error CS0246: The type or namespace name 'McpOAuthThrottle' could not be found (are you missing a using directive or an assembly reference?)
SafeExchange.Tests\Mcp\OAuth\McpOAuthThrottleTests.cs(59,19): error CS0103: The name 'McpOAuthThrottle' does not exist in the current context
```

(and the same for `ClientRateLimiter` and `EntraCallGate`).

- [ ] **Step 3: Implement**

Modify `SafeExchange.Core\Mcp\McpServiceRegistration.cs` (3 edits):

Edit 1 of 3. Replace:

```csharp
namespace SafeExchange.Core.Mcp
{
    using Microsoft.Extensions.DependencyInjection;
    using SafeExchange.Core.Mcp.OAuth;
    using SafeExchange.Core.Mcp.Tools;
    using System;
```

with:

```csharp
namespace SafeExchange.Core.Mcp
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core.Mcp.OAuth;
    using SafeExchange.Core.Mcp.Tools;
    using System;
```

Edit 2 of 3. Replace:

```csharp
    {
        /// <summary>
        /// Registers the MCP tools (scoped, like the services they call), their registry, the dispatcher, the
        /// protected-resource facts and the OAuth broker with its Entra token client. Needs the SafeExchange services
        /// and the <c>Authentication</c>, <c>Features</c> and <c>Mcp:OAuth</c> options.
        /// </summary>
        public static IServiceCollection AddSafeExchangeMcp(this IServiceCollection services)
        {
```

with:

```csharp
    {
        /// <summary>
        /// Registers the MCP tools (scoped, like the services they call), their registry, the dispatcher, the
        /// protected-resource facts and the OAuth broker with its Entra token client, Entra call gate and rate limits.
        /// Needs the SafeExchange services and the <c>Authentication</c>, <c>Features</c> and <c>Mcp:OAuth</c> options.
        /// </summary>
        public static IServiceCollection AddSafeExchangeMcp(this IServiceCollection services)
        {
```

Edit 3 of 3. Replace:

```csharp
            services.AddHttpClient(EntraTokenClient.HttpClientName, client => client.Timeout = EntraTokenClient.Timeout)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
            services.AddSingleton<IEntraTokenClient, EntraTokenClient>();
            services.AddSingleton<McpOAuthBroker>();
            return services;
        }
```

with:

```csharp
            services.AddHttpClient(EntraTokenClient.HttpClientName, client => client.Timeout = EntraTokenClient.Timeout)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
            services.AddSingleton<IEntraTokenClient, EntraTokenClient>();
            services.AddSingleton(sp => new EntraCallGate(
                sp.GetRequiredService<IOptionsMonitor<McpOAuthOptions>>().CurrentValue.RateLimit.MaxConcurrentEntraCalls,
                EntraCallGate.DefaultWait));
            services.AddSingleton<McpOAuthThrottle>();
            services.AddSingleton<McpOAuthBroker>();
            return services;
        }
```

Create `SafeExchange.Core\Mcp\OAuth\ClientRateLimiter.cs`:

```csharp
/// <summary>
/// ClientRateLimiter
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// In-memory token buckets keyed by an arbitrary string (endpoint + client address). A bucket starts full
    /// (<c>burst</c> tokens), refills at <c>permitsPerMinute</c> and each request takes one token. At most
    /// <c>maxKeys</c> buckets are kept; the least recently used one is dropped to make room. Thread-safe; the caller
    /// passes the clock.
    /// </summary>
    public sealed class ClientRateLimiter
    {
        private readonly int maxKeys;

        private readonly Dictionary<string, LinkedListNode<Bucket>> buckets = new(StringComparer.Ordinal);

        // Most recently used first.
        private readonly LinkedList<Bucket> recency = new();

        private readonly object sync = new();

        public ClientRateLimiter(int maxKeys)
        {
            this.maxKeys = Math.Max(1, maxKeys);
        }

        public int Count
        {
            get
            {
                lock (this.sync)
                {
                    return this.buckets.Count;
                }
            }
        }

        public bool TryAcquire(string key, int permitsPerMinute, int burst, DateTime utcNow, out TimeSpan retryAfter)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentOutOfRangeException.ThrowIfLessThan(permitsPerMinute, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(burst, 1);

            var perSecond = permitsPerMinute / 60.0;
            lock (this.sync)
            {
                if (this.buckets.TryGetValue(key, out var node))
                {
                    this.recency.Remove(node);
                    this.recency.AddFirst(node);
                    var bucket = node.Value;
                    if (utcNow > bucket.UpdatedAt)
                    {
                        bucket.Tokens = Math.Min(burst, bucket.Tokens + ((utcNow - bucket.UpdatedAt).TotalSeconds * perSecond));
                        bucket.UpdatedAt = utcNow;
                    }
                }
                else
                {
                    if (this.buckets.Count >= this.maxKeys)
                    {
                        this.buckets.Remove(this.recency.Last!.Value.Key);
                        this.recency.RemoveLast();
                    }

                    node = this.recency.AddFirst(new Bucket(key, burst, utcNow));
                    this.buckets[key] = node;
                }

                if (node.Value.Tokens >= 1)
                {
                    node.Value.Tokens -= 1;
                    retryAfter = TimeSpan.Zero;
                    return true;
                }

                retryAfter = TimeSpan.FromSeconds((1 - node.Value.Tokens) / perSecond);
                return false;
            }
        }

        private sealed class Bucket
        {
            public Bucket(string key, double tokens, DateTime updatedAt)
            {
                this.Key = key;
                this.Tokens = tokens;
                this.UpdatedAt = updatedAt;
            }

            public string Key { get; }

            public double Tokens { get; set; }

            public DateTime UpdatedAt { get; set; }
        }
    }
}
```

Create `SafeExchange.Core\Mcp\OAuth\EntraCallGate.cs`:

```csharp
/// <summary>
/// EntraCallGate
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Caps the concurrent outbound Entra token calls of this instance (<c>Mcp:OAuth:RateLimit:MaxConcurrentEntraCalls</c>,
    /// default 16). A call that finds no free slot within <see cref="DefaultWait"/> is turned away (503,
    /// <c>Retry-After: 2</c>) instead of queueing behind a slow Entra.
    /// </summary>
    public sealed class EntraCallGate
    {
        public const int RetryAfterSeconds = 2;

        public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(2);

        private readonly SemaphoreSlim slots;

        private readonly TimeSpan wait;

        public EntraCallGate(int maxConcurrentCalls, TimeSpan wait)
        {
            var slotCount = Math.Max(1, maxConcurrentCalls);
            this.slots = new SemaphoreSlim(slotCount, slotCount);
            this.wait = wait;
        }

        /// <summary>
        /// True when a slot was taken; the caller must then call <see cref="Exit"/> exactly once.
        /// </summary>
        public Task<bool> TryEnterAsync(CancellationToken cancellationToken = default) => this.slots.WaitAsync(this.wait, cancellationToken);

        public void Exit() => this.slots.Release();
    }
}
```

Create `SafeExchange.Core\Mcp\OAuth\McpOAuthThrottle.cs`:

```csharp
/// <summary>
/// McpOAuthThrottle
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using System;
    using System.Net;

    /// <summary>
    /// Per-instance, per-client-address rate limits of the anonymous broker endpoints (<c>Mcp:OAuth:RateLimit</c>;
    /// token 30/min burst 30, the others 60/min burst 60). Best effort: every instance counts on its own, and requests
    /// without a client address share the bucket <c>unknown</c>. Logs each refusal at Warning without the address.
    /// </summary>
    public sealed class McpOAuthThrottle
    {
        public const string Register = "register";

        public const string Authorize = "authorize";

        public const string Callback = "callback";

        public const string Token = "token";

        private const string UnknownAddress = "unknown";

        private readonly IOptionsMonitor<McpOAuthOptions> oauth;

        private readonly ClientRateLimiter limiter;

        private readonly ILogger log;

        public McpOAuthThrottle(IOptionsMonitor<McpOAuthOptions> oauth, ILogger<McpOAuthThrottle> log)
        {
            this.oauth = oauth ?? throw new ArgumentNullException(nameof(oauth));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.limiter = new ClientRateLimiter(oauth.CurrentValue.RateLimit.MaxClients);
        }

        /// <summary>
        /// Null when the request may proceed; otherwise the 429 answer (JSON <c>slow_down</c> for register and token,
        /// the HTML page for authorize and callback) with <c>Retry-After</c>.
        /// </summary>
        public McpOAuthResult? Check(string endpoint, string? clientAddress)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            var limits = this.oauth.CurrentValue.RateLimit;
            var (permitsPerMinute, burst, isBrowserEndpoint) = endpoint switch
            {
                Token => (limits.TokenPermitsPerMinute, limits.TokenBurst, false),
                Register => (limits.PermitsPerMinute, limits.Burst, false),
                Authorize or Callback => (limits.PermitsPerMinute, limits.Burst, true),
                _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Not a broker endpoint."),
            };

            var key = endpoint + "|" + (string.IsNullOrEmpty(clientAddress) ? UnknownAddress : clientAddress);
            if (this.limiter.TryAcquire(key, Math.Max(1, permitsPerMinute), Math.Max(1, burst), DateTimeProvider.UtcNow, out var retryAfter))
            {
                return null;
            }

            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            this.log.LogWarning("MCP OAuth {Endpoint} rate limit exceeded; retry after {RetryAfterSeconds} s.", endpoint, seconds);
            var result = isBrowserEndpoint
                ? McpOAuthResult.Html(HttpStatusCode.TooManyRequests, ConsentPage.Message(ConsentPage.TooManyRequestsMessage))
                : McpOAuthResult.Error(HttpStatusCode.TooManyRequests, "slow_down", "Too many requests; retry after the Retry-After delay.");
            return result with { RetryAfterSeconds = seconds };
        }
    }
}
```


- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build SafeExchange.slnx --nologo -v q`, then
`dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~ClientRateLimiterTests|FullyQualifiedName~EntraCallGateTests|FullyQualifiedName~McpOAuthThrottleTests|FullyQualifiedName~EntraTokenClientTests"`
and the task filter.
Expected: `Passed:    16`; task filter `Passed:   392`.

- [ ] **Step 5: Commit**

```powershell
git add SafeExchange.Core\Mcp\McpServiceRegistration.cs SafeExchange.Core\Mcp\OAuth\ClientRateLimiter.cs SafeExchange.Core\Mcp\OAuth\EntraCallGate.cs SafeExchange.Core\Mcp\OAuth\McpOAuthThrottle.cs SafeExchange.Tests\Mcp\OAuth\ClientRateLimiterTests.cs SafeExchange.Tests\Mcp\OAuth\EntraCallGateTests.cs SafeExchange.Tests\Mcp\OAuth\EntraTokenClientTests.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthThrottleTests.cs
git commit -m "feat(mcp-oauth): per-client token-bucket throttle and Entra call gate" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
git status --short
```


---

## Task 6: Robust JSON (C), strict token relay (G), Entra call cap and Warning-level logs (F)

**Files:**
- Modify: `SafeExchange.Core\Mcp\OAuth\McpOAuthBroker.cs`
- Test (modify): `SafeExchange.Tests\Mcp\OAuth\FakeEntraTokenClient.cs`
- Test (create): `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerRobustnessTests.cs`
- Test (modify): `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTestBase.cs`

- [ ] **Step 1: Write the failing tests**

`FakeEntraTokenClient` gains `Hold` (a task the fake awaits after recording the form) so a test can keep one Entra
call in flight; the broker test base passes a gate (default: 16 slots, 2 s).

Replace the whole content of `SafeExchange.Tests\Mcp\OAuth\FakeEntraTokenClient.cs` with:

```csharp
/// <summary>
/// FakeEntraTokenClient
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Records every form the broker forwards and answers with <see cref="Response"/> (or throws <see cref="Failure"/>),
    /// after <see cref="Hold"/> completes when it is set.
    /// </summary>
    public sealed class FakeEntraTokenClient : IEntraTokenClient
    {
        public const string AccessToken = "eyJ0eXAiOiJKV1QifQ.eyJzdWIiOiJmYWtlLWVudHJhIn0.c2lnbmF0dXJlLWZvci10ZXN0cw";

        public const string RefreshToken = "0.AAAA-fake-entra-refresh-token-for-tests";

        public static readonly string SuccessBody =
            "{\"token_type\":\"Bearer\",\"scope\":\"api://safeexchange.test/user_impersonation\",\"expires_in\":3599,"
            + $"\"access_token\":\"{AccessToken}\",\"refresh_token\":\"{RefreshToken}\"}}";

        public List<Dictionary<string, string>> Requests { get; } = new();

        public EntraTokenResponse Response { get; set; } = new(HttpStatusCode.OK, SuccessBody);

        public Exception? Failure { get; set; }

        public Task? Hold { get; set; }

        public async Task<EntraTokenResponse> RedeemAsync(IReadOnlyList<KeyValuePair<string, string>> form, CancellationToken cancellationToken = default)
        {
            this.Requests.Add(form.ToDictionary(p => p.Key, p => p.Value));
            if (this.Hold is not null)
            {
                await this.Hold;
            }

            if (this.Failure is not null)
            {
                throw this.Failure;
            }

            return this.Response;
        }
    }
}
```

Create `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerRobustnessTests.cs`:

```csharp
/// <summary>
/// McpOAuthBrokerRobustnessTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Threading.Tasks;

    /// <summary>
    /// Malformed JSON never escapes as an exception, Entra's token answer is relayed only when it is a JSON object with
    /// status 200, 400 or 401, the Entra call gate turns away calls beyond its cap, and refusals log at Warning.
    /// </summary>
    [TestFixture]
    public class McpOAuthBrokerRobustnessTests : McpOAuthBrokerTestBase
    {
        private static readonly string RefreshBody = $"grant_type=refresh_token&refresh_token={FakeEntraTokenClient.RefreshToken}&client_id={BrokerClientId}";

        [TestCase("{\"redirect_uris\":[\"http://127.0.0.1/\\uD800\"]}", "invalid_redirect_uri")]
        [TestCase("{\"redirect_uris\":[\"http://127.0.0.1/cb\"],\"token_endpoint_auth_method\":\"\\uDC00\"}", "invalid_client_metadata")]
        [TestCase("{\"redirect_uris\":[\"http://127.0.0.1/cb\"],\"grant_types\":[\"\\uD800\"]}", "invalid_client_metadata")]
        public void Register_WithAnInvalidUtf16Escape_IsRefused(string body, string error)
        {
            var result = this.Broker.Register(body);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(Json(result.Body)["error"]!.GetValue<string>(), Is.EqualTo(error));
        }

        [TestCase(HttpStatusCode.OK, "{\"access_token\":\"a\"}")]
        [TestCase(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}")]
        [TestCase(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_client\"}")]
        [TestCase(HttpStatusCode.BadRequest, "{\"error\":\"\\uD800\"}")]
        public async Task AJsonObjectWith200_400Or401_IsRelayedUnchanged(HttpStatusCode status, string body)
        {
            this.Entra.Response = new EntraTokenResponse(status, body);

            var result = await this.Broker.TokenAsync(ApiBase, RefreshBody);

            Assert.That(result, Is.EqualTo(McpOAuthResult.Json(status, body)));
        }

        [TestCase(HttpStatusCode.InternalServerError, "{\"error\":\"server_error\"}")]
        [TestCase(HttpStatusCode.TooManyRequests, "{\"error\":\"temporarily_unavailable\"}")]
        [TestCase(HttpStatusCode.Forbidden, "{\"error\":\"access_denied\"}")]
        [TestCase(HttpStatusCode.Found, "")]
        [TestCase(HttpStatusCode.OK, "<html><body>Sign in to the proxy</body></html>")]
        [TestCase(HttpStatusCode.OK, "[]")]
        [TestCase(HttpStatusCode.BadRequest, "")]
        public async Task AnythingElse_IsABadGateway_AndLogsTheStatus(HttpStatusCode status, string body)
        {
            this.Entra.Response = new EntraTokenResponse(status, body);

            var result = await this.Broker.TokenAsync(ApiBase, RefreshBody);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(Json(result.Body)["error"]!.GetValue<string>(), Is.EqualTo("server_error"));
            Assert.That(this.Logs.Entries, Has.One.StartsWith(
                $"Warning SafeExchange.Core.Mcp.OAuth.McpOAuthBroker: MCP OAuth token (refresh_token) got an unexpected Entra response with status {(int)status}."));
        }

        [Test]
        public async Task WhenEveryEntraCallSlotIsBusy_TheTokenRequestIsTemporarilyUnavailable()
        {
            var broker = this.CreateBroker(gate: new EntraCallGate(1, TimeSpan.FromMilliseconds(10)));
            var hold = new TaskCompletionSource();
            this.Entra.Hold = hold.Task;
            var first = broker.TokenAsync(ApiBase, RefreshBody);

            var second = await broker.TokenAsync(ApiBase, RefreshBody);

            Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(Json(second.Body)["error"]!.GetValue<string>(), Is.EqualTo("temporarily_unavailable"));
            Assert.That(second.RetryAfterSeconds, Is.EqualTo(2));
            Assert.That(this.Logs.Entries, Has.Some.StartsWith("Warning SafeExchange.Core.Mcp.OAuth.McpOAuthBroker: MCP OAuth token (refresh_token) found every Entra call slot busy"));
            hold.SetResult();
            Assert.That((await first).StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(this.Entra.Requests, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task AFailedEntraCall_FreesItsSlot()
        {
            var broker = this.CreateBroker(gate: new EntraCallGate(1, TimeSpan.FromMilliseconds(10)));
            this.Entra.Failure = new HttpRequestException("No such host is known.");
            Assert.That((await broker.TokenAsync(ApiBase, RefreshBody)).StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));

            this.Entra.Failure = null;

            Assert.That((await broker.TokenAsync(ApiBase, RefreshBody)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public async Task AnUnreachableEntra_LogsTheRequestErrorAndTheInnerExceptionType()
        {
            this.Entra.Failure = new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known. (login.microsoftonline.com:443)", new SocketException(11001));
            await this.Broker.TokenAsync(ApiBase, RefreshBody);
            this.Entra.Failure = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.", new TimeoutException());
            await this.Broker.TokenAsync(ApiBase, RefreshBody);

            var logs = string.Join('\n', this.Logs.Entries);
            Assert.That(logs, Does.Contain("Warning SafeExchange.Core.Mcp.OAuth.McpOAuthBroker: MCP OAuth token (refresh_token) could not reach Entra: HttpRequestException (NameResolutionError, inner SocketException)."));
            Assert.That(logs, Does.Contain("Warning SafeExchange.Core.Mcp.OAuth.McpOAuthBroker: MCP OAuth token (refresh_token) could not reach Entra: TaskCanceledException (none, inner TimeoutException)."));
            Assert.That(logs, Does.Not.Contain("login.microsoftonline.com:443"));
        }

        [Test]
        public void RefusalsAndRedirectedErrors_LogAtWarning()
        {
            this.Broker.Register("not json");
            this.Broker.Authorize(ApiBase, ValidQuery().Replace("response_type=code", "response_type=token"));

            Assert.That(this.Logs.Entries, Has.Some.StartsWith("Warning SafeExchange.Core.Mcp.OAuth.McpOAuthBroker: MCP OAuth register refused the request with invalid_client_metadata."));
            Assert.That(this.Logs.Entries, Has.Some.StartsWith("Warning SafeExchange.Core.Mcp.OAuth.McpOAuthBroker: MCP OAuth authorize redirected unsupported_response_type to the client."));
            Assert.That(this.Logs.Entries.Where(e => e.StartsWith("Information", StringComparison.Ordinal)), Is.Empty);
        }
    }
}
```

Modify `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTestBase.cs` (2 edits):

Edit 1 of 2. Replace:

```csharp
            this.Broker = this.CreateBroker();
        }

        protected McpOAuthBroker CreateBroker(bool mcpEnabled = true, string clientId = BrokerClientId, string authority = Tenant, string stateKey = McpOAuthTestKey.Value)
        {
            var authentication = Mock.Of<IOptionsMonitor<AuthenticationConfiguration>>(x => x.CurrentValue == new AuthenticationConfiguration
            {
```

with:

```csharp
            this.Broker = this.CreateBroker();
        }

        protected McpOAuthBroker CreateBroker(bool mcpEnabled = true, string clientId = BrokerClientId, string authority = Tenant, string stateKey = McpOAuthTestKey.Value, EntraCallGate? gate = null)
        {
            var authentication = Mock.Of<IOptionsMonitor<AuthenticationConfiguration>>(x => x.CurrentValue == new AuthenticationConfiguration
            {
```

Edit 2 of 2. Replace:

```csharp
            var oauth = Mock.Of<IOptionsMonitor<McpOAuthOptions>>(x => x.CurrentValue == new McpOAuthOptions { ClientId = clientId, StateKey = stateKey });
            var features = Mock.Of<IOptionsMonitor<Features>>(x => x.CurrentValue == new Features { McpEndpoint = mcpEnabled });
            var loggerFactory = new LoggerFactory(new[] { this.Logs });
            return new McpOAuthBroker(new McpProtectedResource(authentication, oauth), oauth, authentication, features, this.Entra, loggerFactory.CreateLogger<McpOAuthBroker>());
        }

        protected static JsonObject Json(string? body) => JsonNode.Parse(body!)!.AsObject();
```

with:

```csharp
            var oauth = Mock.Of<IOptionsMonitor<McpOAuthOptions>>(x => x.CurrentValue == new McpOAuthOptions { ClientId = clientId, StateKey = stateKey });
            var features = Mock.Of<IOptionsMonitor<Features>>(x => x.CurrentValue == new Features { McpEndpoint = mcpEnabled });
            var loggerFactory = new LoggerFactory(new[] { this.Logs });
            return new McpOAuthBroker(
                new McpProtectedResource(authentication, oauth),
                oauth,
                authentication,
                features,
                this.Entra,
                gate ?? new EntraCallGate(16, EntraCallGate.DefaultWait),
                loggerFactory.CreateLogger<McpOAuthBroker>());
        }

        protected static JsonObject Json(string? body) => JsonNode.Parse(body!)!.AsObject();
```


- [ ] **Step 2: Run the test build to verify it fails**

Run: `dotnet build SafeExchange.Tests\SafeExchange.Tests.csproj --nologo -v q`
Expected: FAIL:

```
SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTestBase.cs(70,24): error CS1729: 'McpOAuthBroker' does not contain a constructor that takes 7 arguments
```

(Behaviourally, before Step 3 `Register_WithAnInvalidUtf16Escape_IsRefused` throws
`System.InvalidOperationException : Cannot read incomplete UTF-16 JSON text as string with missing low surrogate.`.)

- [ ] **Step 3: Implement**

Modify `SafeExchange.Core\Mcp\OAuth\McpOAuthBroker.cs` (12 edits):

Edit 1 of 12. Replace:

```csharp

        private readonly IEntraTokenClient tokenClient;

        private readonly ILogger log;

        public McpOAuthBroker(
```

with:

```csharp

        private readonly IEntraTokenClient tokenClient;

        private readonly EntraCallGate entraCallGate;

        private readonly ILogger log;

        public McpOAuthBroker(
```

Edit 2 of 12. Replace:

```csharp
            IOptionsMonitor<AuthenticationConfiguration> authentication,
            IOptionsMonitor<Features> features,
            IEntraTokenClient tokenClient,
            ILogger<McpOAuthBroker> log)
        {
            this.protectedResource = protectedResource ?? throw new ArgumentNullException(nameof(protectedResource));
```

with:

```csharp
            IOptionsMonitor<AuthenticationConfiguration> authentication,
            IOptionsMonitor<Features> features,
            IEntraTokenClient tokenClient,
            EntraCallGate entraCallGate,
            ILogger<McpOAuthBroker> log)
        {
            this.protectedResource = protectedResource ?? throw new ArgumentNullException(nameof(protectedResource));
```

Edit 3 of 12. Replace:

```csharp
            this.authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
            this.features = features ?? throw new ArgumentNullException(nameof(features));
            this.tokenClient = tokenClient ?? throw new ArgumentNullException(nameof(tokenClient));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }
```

with:

```csharp
            this.authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
            this.features = features ?? throw new ArgumentNullException(nameof(features));
            this.tokenClient = tokenClient ?? throw new ArgumentNullException(nameof(tokenClient));
            this.entraCallGate = entraCallGate ?? throw new ArgumentNullException(nameof(entraCallGate));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }
```

Edit 4 of 12. Replace:

```csharp
                return this.Refuse("register", "invalid_redirect_uri", "redirect_uris must list 1 to 10 loopback http URIs (127.0.0.1, localhost or [::1]) without fragment or user info.");
            }

            if (request["token_endpoint_auth_method"] is JsonNode method && !(method is JsonValue value && value.TryGetValue<string>(out var name) && name == "none"))
            {
                return this.Refuse("register", "invalid_client_metadata", "Only public clients (token_endpoint_auth_method none) can register.");
            }
```

with:

```csharp
                return this.Refuse("register", "invalid_redirect_uri", "redirect_uris must list 1 to 10 loopback http URIs (127.0.0.1, localhost or [::1]) without fragment or user info.");
            }

            if (request["token_endpoint_auth_method"] is JsonNode method && !(TryGetString(method, out var name) && name == "none"))
            {
                return this.Refuse("register", "invalid_client_metadata", "Only public clients (token_endpoint_auth_method none) can register.");
            }
```

Edit 5 of 12. Replace:

```csharp
        /// Forwards an <c>authorization_code</c> (with <c>code</c> and <c>code_verifier</c>) or <c>refresh_token</c>
        /// grant to Entra's fixed token endpoint with the broker's client ID, callback and scope; everything else the
        /// client sent (<c>resource</c>, <c>client_secret</c>, its own <c>redirect_uri</c> and <c>scope</c>) is dropped.
        /// Entra's status and body are returned unchanged.
        /// </summary>
        public async Task<McpOAuthResult> TokenAsync(Uri apiBaseUri, string? body, CancellationToken cancellationToken = default)
        {
```

with:

```csharp
        /// Forwards an <c>authorization_code</c> (with <c>code</c> and <c>code_verifier</c>) or <c>refresh_token</c>
        /// grant to Entra's fixed token endpoint with the broker's client ID, callback and scope; everything else the
        /// client sent (<c>resource</c>, <c>client_secret</c>, its own <c>redirect_uri</c> and <c>scope</c>) is dropped.
        /// Entra's status and body are returned unchanged when the status is 200, 400 or 401 and the body is a JSON
        /// object; anything else is 502 <c>server_error</c>. At most <see cref="EntraCallGate"/> calls run at once; beyond
        /// that the answer is 503 <c>temporarily_unavailable</c> with <c>Retry-After</c>.
        /// </summary>
        public async Task<McpOAuthResult> TokenAsync(Uri apiBaseUri, string? body, CancellationToken cancellationToken = default)
        {
```

Edit 6 of 12. Replace:

```csharp

            form.Add(new("scope", string.Join(' ', this.Scopes)));

            EntraTokenResponse response;
            try
            {
```

with:

```csharp

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
```

Edit 7 of 12. Replace:

```csharp
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
```

with:

```csharp
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
```

Edit 8 of 12. Replace:

```csharp

        private McpOAuthResult Refuse(string endpoint, string error, string description)
        {
            this.log.LogInformation("MCP OAuth {Endpoint} refused the request with {Error}.", endpoint, error);
            return McpOAuthResult.Error(HttpStatusCode.BadRequest, error, description);
        }
```

with:

```csharp

        private McpOAuthResult Refuse(string endpoint, string error, string description)
        {
            this.log.LogWarning("MCP OAuth {Endpoint} refused the request with {Error}.", endpoint, error);
            return McpOAuthResult.Error(HttpStatusCode.BadRequest, error, description);
        }
```

Edit 9 of 12. Replace:

```csharp

            foreach (var item in array)
            {
                if (item is not JsonValue value || !value.TryGetValue<string>(out var text))
                {
                    return false;
                }
```

with:

```csharp

            foreach (var item in array)
            {
                if (!TryGetString(item, out var text))
                {
                    return false;
                }
```

Edit 10 of 12. Replace:

```csharp
            return true;
        }

        private static bool IsAbsentOrSubset(JsonObject request, string name, IReadOnlyCollection<string> allowed)
            => request[name] is null || (TryGetStrings(request, name, out var values) && values.All(allowed.Contains));
```

with:

```csharp
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
```

Edit 11 of 12. Replace:

```csharp

        private McpOAuthResult RedirectError(string endpoint, string redirectUri, string? clientState, string error, string description)
        {
            this.log.LogInformation("MCP OAuth {Endpoint} redirected {Error} to the client.", endpoint, error);
            return McpOAuthResult.Redirect(AppendQuery(redirectUri, new KeyValuePair<string, string?>[]
            {
                new("error", error),
```

with:

```csharp

        private McpOAuthResult RedirectError(string endpoint, string redirectUri, string? clientState, string error, string description)
        {
            this.log.LogWarning("MCP OAuth {Endpoint} redirected {Error} to the client.", endpoint, error);
            return McpOAuthResult.Redirect(AppendQuery(redirectUri, new KeyValuePair<string, string?>[]
            {
                new("error", error),
```

Edit 12 of 12. Replace:

```csharp
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
```

with:

```csharp
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
```


- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build SafeExchange.slnx --nologo -v q`, then the task filter.
Expected: `0 Error(s)`; `Passed:   410`.

- [ ] **Step 5: Commit**

```powershell
git add SafeExchange.Core\Mcp\OAuth\McpOAuthBroker.cs SafeExchange.Tests\Mcp\OAuth\FakeEntraTokenClient.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerRobustnessTests.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTestBase.cs
git commit -m "fix(mcp-oauth): robust JSON reads, strict token relay, Entra call cap and Warning-level refusals" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
git status --short
```


---

## Task 7: https-only discovery and broker; uncacheable protected-resource metadata (E)

**Files:**
- Modify: `SafeExchange.Core.Functions\Mcp\OAuth\McpOAuthHandler.cs`
- Modify: `SafeExchange.Core.Functions\Mcp\OAuthProtectedResourceHandler.cs`
- Create: `SafeExchange.Core\Mcp\McpBaseUriGuard.cs`
- Modify: `SafeExchange.Core\Mcp\McpServiceRegistration.cs`
- Modify: `SafeExchange.Core\Mcp\OAuth\McpOAuthBroker.cs`
- Test (create): `SafeExchange.Tests\Mcp\McpBaseUriGuardTests.cs`
- Test (modify): `SafeExchange.Tests\Mcp\McpMetadataTests.cs`
- Test (modify): `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerCallbackTests.cs`
- Test (modify): `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerGatingTests.cs`
- Test (modify): `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerRegisterTests.cs`
- Test (modify): `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerRobustnessTests.cs`
- Test (modify): `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTestBase.cs`

- [ ] **Step 1: Write the failing tests**

`Register` and `Callback` gain the API base parameter (D11), so their call sites in the broker tests change
mechanically (scripted below); the gating test runs every endpoint against a plain-http base.

Create `SafeExchange.Tests\Mcp\McpBaseUriGuardTests.cs`:

```csharp
/// <summary>
/// McpBaseUriGuardTests
/// </summary>

namespace SafeExchange.Tests.Mcp
{
    using Microsoft.Extensions.Logging;
    using NUnit.Framework;
    using SafeExchange.Core.Mcp;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Linq;

    [TestFixture]
    public class McpBaseUriGuardTests
    {
        [TestCase("https://safeexchange.test/api/")]
        [TestCase("https://safeexchange.test:8443/api/")]
        [TestCase("http://localhost:7071/api/")]
        [TestCase("http://127.0.0.1:7071/api/")]
        [TestCase("http://[::1]:7071/api/")]
        public void HttpsAndLoopbackHttp_AreAllowed(string apiBaseUri)
        {
            var logs = new RecordingLoggerProvider();

            Assert.That(Create(logs).IsAllowed(new Uri(apiBaseUri)), Is.True);
            Assert.That(logs.Entries, Is.Empty);
        }

        [Test]
        public void PlainHttpToAnyOtherHost_IsRefused_AndLoggedOnceAtWarning()
        {
            var logs = new RecordingLoggerProvider();
            var guard = Create(logs);

            Assert.That(guard.IsAllowed(new Uri("http://safeexchange.test/api/")), Is.False);
            Assert.That(guard.IsAllowed(new Uri("http://10.0.0.4/api/")), Is.False);

            Assert.That(logs.Entries.Single(), Does.StartWith(
                "Warning SafeExchange.Core.Mcp.McpBaseUriGuard: MCP OAuth discovery and broker answer 404 for the non-https base http://safeexchange.test/api/"));
        }

        private static McpBaseUriGuard Create(RecordingLoggerProvider logs)
            => new(new LoggerFactory(new[] { logs }).CreateLogger<McpBaseUriGuard>());
    }
}
```

Modify `SafeExchange.Tests\Mcp\McpMetadataTests.cs` (2 edits):

Edit 1 of 2. Replace:

```csharp

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Headers.GetValues("Content-Type"), Is.EqualTo(new[] { "application/json" }));
            Assert.That(BodyOf(response), Is.EqualTo(
                "{\"resource\":\"https://safeexchange.test/api/mcp\","
                + $"\"authorization_servers\":[\"https://login.microsoftonline.com/{TenantId}/v2.0\"],"
```

with:

```csharp

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Headers.GetValues("Content-Type"), Is.EqualTo(new[] { "application/json" }));
            Assert.That(response.Headers.GetValues("Cache-Control"), Is.EqualTo(new[] { "no-store" }));
            Assert.That(BodyOf(response), Is.EqualTo(
                "{\"resource\":\"https://safeexchange.test/api/mcp\","
                + $"\"authorization_servers\":[\"https://login.microsoftonline.com/{TenantId}/v2.0\"],"
```

Edit 2 of 2. Replace:

```csharp
            Assert.That(BodyOf(response), Is.Empty);
        }

        private static TestHttpRequestData MetadataRequest()
        {
            var request = TestFactory.CreateHttpRequestData("get");
            request.SetUrl(new Uri("https://safeexchange.test/api/.well-known/oauth-protected-resource"));
            return request;
        }
    }
```

with:

```csharp
            Assert.That(BodyOf(response), Is.Empty);
        }

        [Test]
        public async Task Metadata_OverPlainHttpToANonLoopbackHost_AnswersNotFound()
        {
            var response = await this.MetadataHandler.Run(MetadataRequest("http://safeexchange.test"));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(BodyOf(response), Is.Empty);
        }

        [Test]
        public async Task Metadata_OverPlainHttpToLocalhost_IsServed()
        {
            var response = await this.MetadataHandler.Run(MetadataRequest("http://localhost:7071"));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(BodyOf(response), Does.StartWith("{\"resource\":\"http://localhost:7071/api/mcp\","));
        }

        private static TestHttpRequestData MetadataRequest(string origin = "https://safeexchange.test")
        {
            var request = TestFactory.CreateHttpRequestData("get");
            request.SetUrl(new Uri(origin + "/api/.well-known/oauth-protected-resource"));
            return request;
        }
    }
```

In `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerCallbackTests.cs`, replace every `this.Broker.Callback(` with `this.Broker.Callback(ApiBase, ` (every call site; nothing else changes):

```powershell
$p = 'SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerCallbackTests.cs'; $c = [IO.File]::ReadAllText($p); [IO.File]::WriteAllText($p, $c.Replace('this.Broker.Callback(', 'this.Broker.Callback(ApiBase, '))
```

Modify `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerGatingTests.cs` (3 edits):

Edit 1 of 3. Replace:

```csharp
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System.Collections.Generic;
    using System.Net;
    using System.Threading.Tasks;
```

with:

```csharp
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;
```

Edit 2 of 3. Replace:

```csharp
            var broker = this.CreateBroker(mcpEnabled, clientId, authority, stateKey);

            Assert.That(broker.IsEnabled, Is.False);
            foreach (var result in await AllEndpointsAsync(broker))
            {
                Assert.That(result, Is.EqualTo(McpOAuthResult.NotFound));
            }
```

with:

```csharp
            var broker = this.CreateBroker(mcpEnabled, clientId, authority, stateKey);

            Assert.That(broker.IsEnabled, Is.False);
            foreach (var result in await AllEndpointsAsync(broker, ApiBase))
            {
                Assert.That(result, Is.EqualTo(McpOAuthResult.NotFound));
            }
```

Edit 3 of 3. Replace:

```csharp
        }

        [Test]
        public void TheBroker_IsEnabled_WithMcpOnAClientIdAndAnAuthority()
        {
            Assert.That(this.Broker.IsEnabled, Is.True);
        }

        private static async Task<List<McpOAuthResult>> AllEndpointsAsync(McpOAuthBroker broker)
            => new List<McpOAuthResult>
            {
                broker.Metadata(ApiBase),
                broker.Register("{\"redirect_uris\":[\"http://127.0.0.1:1/cb\"]}"),
                broker.Authorize(ApiBase, $"client_id={BrokerClientId}&redirect_uri=http%3A%2F%2F127.0.0.1%3A1%2Fcb&response_type=code&code_challenge={Challenge}&code_challenge_method=S256"),
                broker.AuthorizeConsent(ApiBase, $"client_id={BrokerClientId}&decision=allow", "bm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm8"),
                broker.Callback("code=c&state=eyJyIjoiaHR0cDovLzEyNy4wLjAuMToxL2NiIn0", "bm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm8"),
                await broker.TokenAsync(ApiBase, "grant_type=refresh_token&refresh_token=r"),
            };
    }
}
```

with:

```csharp
        }

        [Test]
        public void TheBroker_IsEnabled_WithMcpOnAClientIdAStateKeyAndAnAuthority()
        {
            Assert.That(this.Broker.IsEnabled, Is.True);
        }

        [Test]
        public async Task EveryEndpoint_IsNotFound_ForAPlainHttpBase_AndOneWarningIsLogged()
        {
            foreach (var result in await AllEndpointsAsync(this.Broker, new Uri("http://safeexchange.test/api/")))
            {
                Assert.That(result, Is.EqualTo(McpOAuthResult.NotFound));
            }

            Assert.That(this.Entra.Requests, Is.Empty);
            Assert.That(this.Logs.Entries.Single(), Does.StartWith("Warning SafeExchange.Core.Mcp.McpBaseUriGuard: "));
        }

        [Test]
        public void APlainHttpLoopbackBase_IsServed_ForLocalDevelopment()
        {
            var result = this.Broker.Metadata(new Uri("http://localhost:7071/api/"));

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(result.Body, Does.StartWith("{\"issuer\":\"http://localhost:7071/api\","));
        }

        private static async Task<List<McpOAuthResult>> AllEndpointsAsync(McpOAuthBroker broker, Uri apiBase)
            => new List<McpOAuthResult>
            {
                broker.Metadata(apiBase),
                broker.Register(apiBase, "{\"redirect_uris\":[\"http://127.0.0.1:1/cb\"]}"),
                broker.Authorize(apiBase, $"client_id={BrokerClientId}&redirect_uri=http%3A%2F%2F127.0.0.1%3A1%2Fcb&response_type=code&code_challenge={Challenge}&code_challenge_method=S256"),
                broker.AuthorizeConsent(apiBase, $"client_id={BrokerClientId}&decision=allow", "bm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm8"),
                broker.Callback(apiBase, "code=c&state=eyJyIjoiaHR0cDovLzEyNy4wLjAuMToxL2NiIn0", "bm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm8"),
                await broker.TokenAsync(apiBase, "grant_type=refresh_token&refresh_token=r"),
            };
    }
}
```

In `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerRegisterTests.cs`, replace every `this.Broker.Register(` with `this.Broker.Register(ApiBase, ` (every call site; nothing else changes):

```powershell
$p = 'SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerRegisterTests.cs'; $c = [IO.File]::ReadAllText($p); [IO.File]::WriteAllText($p, $c.Replace('this.Broker.Register(', 'this.Broker.Register(ApiBase, '))
```

In `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerRobustnessTests.cs`, replace every `this.Broker.Register(` with `this.Broker.Register(ApiBase, ` (every call site; nothing else changes):

```powershell
$p = 'SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerRobustnessTests.cs'; $c = [IO.File]::ReadAllText($p); [IO.File]::WriteAllText($p, $c.Replace('this.Broker.Register(', 'this.Broker.Register(ApiBase, '))
```

Modify `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTestBase.cs`:

Replace:

```csharp
                features,
                this.Entra,
                gate ?? new EntraCallGate(16, EntraCallGate.DefaultWait),
                loggerFactory.CreateLogger<McpOAuthBroker>());
        }
```

with:

```csharp
                features,
                this.Entra,
                gate ?? new EntraCallGate(16, EntraCallGate.DefaultWait),
                new McpBaseUriGuard(loggerFactory.CreateLogger<McpBaseUriGuard>()),
                loggerFactory.CreateLogger<McpOAuthBroker>());
        }
```


- [ ] **Step 2: Run the test build to verify it fails**

Run: `dotnet build SafeExchange.Tests\SafeExchange.Tests.csproj --nologo -v q`
Expected: FAIL, first error:

```
SafeExchange.Tests\Mcp\McpBaseUriGuardTests.cs(43,24): error CS0246: The type or namespace name 'McpBaseUriGuard' could not be found (are you missing a using directive or an assembly reference?)
```

- [ ] **Step 3: Implement**

Modify `SafeExchange.Core.Functions\Mcp\OAuth\McpOAuthHandler.cs` (2 edits):

Edit 1 of 2. Replace:

```csharp
        public async Task<HttpResponseData> Register(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return await WriteAsync(request, this.broker.Register(await ReadBodyAsync(request)));
        }

        /// <summary>
```

with:

```csharp
        public async Task<HttpResponseData> Register(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return await WriteAsync(request, this.broker.Register(McpHandler.ApiBaseUri(request.Url), await ReadBodyAsync(request)));
        }

        /// <summary>
```

Edit 2 of 2. Replace:

```csharp
        public Task<HttpResponseData> Callback(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return WriteAsync(request, this.broker.Callback(request.Url.Query, ReadConsentCookie(request)));
        }

        public async Task<HttpResponseData> Token(HttpRequestData request)
```

with:

```csharp
        public Task<HttpResponseData> Callback(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return WriteAsync(request, this.broker.Callback(McpHandler.ApiBaseUri(request.Url), request.Url.Query, ReadConsentCookie(request)));
        }

        public async Task<HttpResponseData> Token(HttpRequestData request)
```

Replace the whole content of `SafeExchange.Core.Functions\Mcp\OAuthProtectedResourceHandler.cs` with:

```csharp
/// <summary>
/// OAuthProtectedResourceHandler
/// </summary>

namespace SafeExchange.Core.Functions.Mcp
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Options;
    using ModelContextProtocol;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Mcp;
    using System;
    using System.Net;
    using System.Text.Json;
    using System.Threading.Tasks;

    /// <summary>
    /// Anonymous RFC 9728 protected-resource metadata for MCP clients, <c>Cache-Control: no-store</c> (404 while the MCP
    /// endpoint is disabled or for a base URL that <see cref="McpBaseUriGuard"/> refuses).
    /// </summary>
    public sealed class OAuthProtectedResourceHandler
    {
        private readonly McpProtectedResource protectedResource;

        private readonly IOptionsMonitor<Features> features;

        private readonly McpBaseUriGuard baseUriGuard;

        public OAuthProtectedResourceHandler(McpProtectedResource protectedResource, IOptionsMonitor<Features> features, McpBaseUriGuard baseUriGuard)
        {
            this.protectedResource = protectedResource ?? throw new ArgumentNullException(nameof(protectedResource));
            this.features = features ?? throw new ArgumentNullException(nameof(features));
            this.baseUriGuard = baseUriGuard ?? throw new ArgumentNullException(nameof(baseUriGuard));
        }

        public async Task<HttpResponseData> Run(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var apiBaseUri = McpHandler.ApiBaseUri(request.Url);
            if (!this.features.CurrentValue.McpEndpoint || !this.baseUriGuard.IsAllowed(apiBaseUri))
            {
                return request.CreateResponse(HttpStatusCode.NotFound);
            }

            var response = request.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            response.Headers.Add("Cache-Control", "no-store");
            await response.WriteStringAsync(JsonSerializer.Serialize(this.protectedResource.ToMetadata(apiBaseUri), McpJsonUtilities.DefaultOptions));
            return response;
        }
    }
}
```

Create `SafeExchange.Core\Mcp\McpBaseUriGuard.cs`:

```csharp
/// <summary>
/// McpBaseUriGuard
/// </summary>

namespace SafeExchange.Core.Mcp
{
    using Microsoft.Extensions.Logging;
    using System;
    using System.Threading;

    /// <summary>
    /// The OAuth discovery documents and the broker publish URLs derived from the request URL, so they are served only
    /// for an <c>https</c> base, or plain <c>http</c> to a loopback host (local development). Anything else is answered
    /// as "not configured" (404); the first refusal per instance logs a Warning.
    /// </summary>
    public sealed class McpBaseUriGuard
    {
        private readonly ILogger log;

        private int warned;

        public McpBaseUriGuard(ILogger<McpBaseUriGuard> log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public bool IsAllowed(Uri apiBaseUri)
        {
            ArgumentNullException.ThrowIfNull(apiBaseUri);
            if (apiBaseUri.Scheme == Uri.UriSchemeHttps || (apiBaseUri.Scheme == Uri.UriSchemeHttp && apiBaseUri.IsLoopback))
            {
                return true;
            }

            if (Interlocked.Exchange(ref this.warned, 1) == 0)
            {
                this.log.LogWarning("MCP OAuth discovery and broker answer 404 for the non-https base {ApiBaseUri}; serve the app over https.", apiBaseUri.AbsoluteUri);
            }

            return false;
        }
    }
}
```

Modify `SafeExchange.Core\Mcp\McpServiceRegistration.cs`:

Replace:

```csharp
            services.AddScoped<McpToolRegistry>();
            services.AddScoped<McpRequestDispatcher>();
            services.AddSingleton<McpProtectedResource>();

            services.AddHttpClient(EntraTokenClient.HttpClientName, client => client.Timeout = EntraTokenClient.Timeout)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
```

with:

```csharp
            services.AddScoped<McpToolRegistry>();
            services.AddScoped<McpRequestDispatcher>();
            services.AddSingleton<McpProtectedResource>();
            services.AddSingleton<McpBaseUriGuard>();

            services.AddHttpClient(EntraTokenClient.HttpClientName, client => client.Timeout = EntraTokenClient.Timeout)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
```

Modify `SafeExchange.Core\Mcp\OAuth\McpOAuthBroker.cs` (9 edits):

Edit 1 of 9. Replace:

```csharp

        private readonly EntraCallGate entraCallGate;

        private readonly ILogger log;

        public McpOAuthBroker(
```

with:

```csharp

        private readonly EntraCallGate entraCallGate;

        private readonly McpBaseUriGuard baseUriGuard;

        private readonly ILogger log;

        public McpOAuthBroker(
```

Edit 2 of 9. Replace:

```csharp
            IOptionsMonitor<Features> features,
            IEntraTokenClient tokenClient,
            EntraCallGate entraCallGate,
            ILogger<McpOAuthBroker> log)
        {
            this.protectedResource = protectedResource ?? throw new ArgumentNullException(nameof(protectedResource));
```

with:

```csharp
            IOptionsMonitor<Features> features,
            IEntraTokenClient tokenClient,
            EntraCallGate entraCallGate,
            McpBaseUriGuard baseUriGuard,
            ILogger<McpOAuthBroker> log)
        {
            this.protectedResource = protectedResource ?? throw new ArgumentNullException(nameof(protectedResource));
```

Edit 3 of 9. Replace:

```csharp
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
```

with:

```csharp
            this.features = features ?? throw new ArgumentNullException(nameof(features));
            this.tokenClient = tokenClient ?? throw new ArgumentNullException(nameof(tokenClient));
            this.entraCallGate = entraCallGate ?? throw new ArgumentNullException(nameof(entraCallGate));
            this.baseUriGuard = baseUriGuard ?? throw new ArgumentNullException(nameof(baseUriGuard));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// False (every endpoint answers 404) unless <c>Features.McpEndpoint</c> is on and the broker is configured. Each
        /// endpoint also answers 404 for a base URL that <see cref="McpBaseUriGuard"/> refuses (plain http, not loopback).
        /// </summary>
        public bool IsEnabled => this.features.CurrentValue.McpEndpoint && this.protectedResource.IsBrokerConfigured;

        public McpOAuthResult Metadata(Uri apiBaseUri)
        {
            ArgumentNullException.ThrowIfNull(apiBaseUri);
            if (!this.IsEnabledFor(apiBaseUri))
            {
                return McpOAuthResult.NotFound;
            }
```

Edit 4 of 9. Replace:

```csharp
        /// RFC 7591 registration: every client gets the configured Entra public client ID; only loopback redirect
        /// URIs, the code flow and <c>token_endpoint_auth_method: none</c> are accepted.
        /// </summary>
        public McpOAuthResult Register(string? body)
        {
            if (!this.IsEnabled)
            {
                return McpOAuthResult.NotFound;
            }
```

with:

```csharp
        /// RFC 7591 registration: every client gets the configured Entra public client ID; only loopback redirect
        /// URIs, the code flow and <c>token_endpoint_auth_method: none</c> are accepted.
        /// </summary>
        public McpOAuthResult Register(Uri apiBaseUri, string? body)
        {
            ArgumentNullException.ThrowIfNull(apiBaseUri);
            if (!this.IsEnabledFor(apiBaseUri))
            {
                return McpOAuthResult.NotFound;
            }
```

Edit 5 of 9. Replace:

```csharp
        public McpOAuthResult Authorize(Uri apiBaseUri, string? query)
        {
            ArgumentNullException.ThrowIfNull(apiBaseUri);
            if (!this.TryGetStateKey(out var key))
            {
                return McpOAuthResult.NotFound;
            }
```

with:

```csharp
        public McpOAuthResult Authorize(Uri apiBaseUri, string? query)
        {
            ArgumentNullException.ThrowIfNull(apiBaseUri);
            if (!this.TryGetStateKey(apiBaseUri, out var key))
            {
                return McpOAuthResult.NotFound;
            }
```

Edit 6 of 9. Replace:

```csharp
        {
            ArgumentNullException.ThrowIfNull(apiBaseUri);
            var endpoints = this.EnabledEndpoints;
            if (endpoints is null || !this.TryGetStateKey(out var key))
            {
                return McpOAuthResult.NotFound;
            }
```

with:

```csharp
        {
            ArgumentNullException.ThrowIfNull(apiBaseUri);
            var endpoints = this.EnabledEndpoints;
            if (endpoints is null || !this.TryGetStateKey(apiBaseUri, out var key))
            {
                return McpOAuthResult.NotFound;
            }
```

Edit 7 of 9. Replace:

```csharp
        /// <c>error_description</c> and <c>state</c>) to the loopback redirect and expires the cookie. Without a valid
        /// state and cookie there is nowhere safe to go, so the answer is the 400 page.
        /// </summary>
        public McpOAuthResult Callback(string? query, string? consentCookie)
        {
            if (!this.TryGetStateKey(out var key))
            {
                return McpOAuthResult.NotFound;
            }
```

with:

```csharp
        /// <c>error_description</c> and <c>state</c>) to the loopback redirect and expires the cookie. Without a valid
        /// state and cookie there is nowhere safe to go, so the answer is the 400 page.
        /// </summary>
        public McpOAuthResult Callback(Uri apiBaseUri, string? query, string? consentCookie)
        {
            ArgumentNullException.ThrowIfNull(apiBaseUri);
            if (!this.TryGetStateKey(apiBaseUri, out var key))
            {
                return McpOAuthResult.NotFound;
            }
```

Edit 8 of 9. Replace:

```csharp
        public async Task<McpOAuthResult> TokenAsync(Uri apiBaseUri, string? body, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(apiBaseUri);
            if (!this.IsEnabled)
            {
                return McpOAuthResult.NotFound;
            }
```

with:

```csharp
        public async Task<McpOAuthResult> TokenAsync(Uri apiBaseUri, string? body, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(apiBaseUri);
            if (!this.IsEnabledFor(apiBaseUri))
            {
                return McpOAuthResult.NotFound;
            }
```

Edit 9 of 9. Replace:

```csharp
        private IReadOnlyList<string> Scopes
            => this.protectedResource.Scopes.Append(OfflineAccessScope).Distinct(StringComparer.Ordinal).ToList();

        private bool TryGetStateKey([NotNullWhen(true)] out byte[]? key)
        {
            key = null;
            return this.IsEnabled && McpOAuthStateKey.TryDecode(this.oauth.CurrentValue.StateKey, out key);
        }

        // The checks shared by the authorize request and the consent form post, in RFC 6749 order.
```

with:

```csharp
        private IReadOnlyList<string> Scopes
            => this.protectedResource.Scopes.Append(OfflineAccessScope).Distinct(StringComparer.Ordinal).ToList();

        private bool IsEnabledFor(Uri apiBaseUri) => this.IsEnabled && this.baseUriGuard.IsAllowed(apiBaseUri);

        private bool TryGetStateKey(Uri apiBaseUri, [NotNullWhen(true)] out byte[]? key)
        {
            key = null;
            return this.IsEnabledFor(apiBaseUri) && McpOAuthStateKey.TryDecode(this.oauth.CurrentValue.StateKey, out key);
        }

        // The checks shared by the authorize request and the consent form post, in RFC 6749 order.
```


- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build SafeExchange.slnx --nologo -v q`, then the task filter and the phase filter.
Expected: `0 Error(s)`; task filter `Passed:   420`; phase filter `Passed:   653` (with the emulator).

- [ ] **Step 5: Commit**

```powershell
git add SafeExchange.Core.Functions\Mcp\OAuth\McpOAuthHandler.cs SafeExchange.Core.Functions\Mcp\OAuthProtectedResourceHandler.cs SafeExchange.Core\Mcp\McpBaseUriGuard.cs SafeExchange.Core\Mcp\McpServiceRegistration.cs SafeExchange.Core\Mcp\OAuth\McpOAuthBroker.cs SafeExchange.Tests\Mcp\McpBaseUriGuardTests.cs SafeExchange.Tests\Mcp\McpMetadataTests.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerCallbackTests.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerGatingTests.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerRegisterTests.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerRobustnessTests.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTestBase.cs
git commit -m "fix(mcp-oauth): https-only discovery and broker; no-store protected-resource metadata" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
git status --short
```


---

## Task 8: Adapter throttling per client address; strict UTF-8 bodies

The adapter asks `McpOAuthThrottle` first (D7, D8) and decodes bodies strictly (D12). The authorize `POST`, cookies
and HTML headers were wired in Task 4.

**Files:**
- Modify: `SafeExchange.Core.Functions\Mcp\OAuth\McpOAuthHandler.cs`
- Create: `SafeExchange.Core\Mcp\OAuth\ClientAddress.cs`
- Test (create): `SafeExchange.Tests\Mcp\OAuth\ClientAddressTests.cs`
- Test (create): `SafeExchange.Tests\Mcp\OAuth\McpOAuthHandlerThrottleTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `SafeExchange.Tests\Mcp\OAuth\ClientAddressTests.cs`:

```csharp
/// <summary>
/// ClientAddressTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;

    [TestFixture]
    public class ClientAddressTests
    {
        [TestCase("203.0.113.7", "203.0.113.7")]
        [TestCase("203.0.113.7:50123", "203.0.113.7")]
        [TestCase("198.51.100.1, 203.0.113.7:50123", "203.0.113.7")]
        [TestCase("spoofed-by-the-client,203.0.113.7:50123", "203.0.113.7")]
        [TestCase("[2001:db8::1]:50123", "2001:db8::1")]
        [TestCase("2001:DB8:0:0:0:0:0:1", "2001:db8::1")]
        [TestCase("198.51.100.1, [2001:db8::1]:443", "2001:db8::1")]
        public void TheLastForwardedForEntry_IsTheClientAddress_WithoutItsPort(string header, string expected)
        {
            Assert.That(ClientAddress.FromForwardedFor(header), Is.EqualTo(expected));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" , ")]
        [TestCase("unknown")]
        [TestCase("203.0.113.7, not-an-address")]
        [TestCase("[2001:db8::1")]
        public void AMissingOrUnparsableLastEntry_IsNoAddress(string? header)
        {
            Assert.That(ClientAddress.FromForwardedFor(header), Is.Null);
        }
    }
}
```

Create `SafeExchange.Tests\Mcp\OAuth\McpOAuthHandlerThrottleTests.cs`:

```csharp
/// <summary>
/// McpOAuthHandlerThrottleTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using Microsoft.Azure.Functions.Worker.Http;
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// The adapter throttles each broker route per client address (the last <c>X-Forwarded-For</c> entry, which the App
    /// Service front end appends) before the broker runs, and refuses a body that is not valid UTF-8.
    /// </summary>
    [TestFixture]
    public class McpOAuthHandlerThrottleTests : McpOAuthHttpTestBase
    {
        private const string RefreshBody = "grant_type=refresh_token&refresh_token=r&client_id=" + BrokerClientId;

        [SetUp]
        public void ThrottleSetUp()
        {
            this.BuildProvider(
                ("Mcp:OAuth:ClientId", BrokerClientId),
                ("Mcp:OAuth:StateKey", McpOAuthTestKey.Value),
                ("Mcp:OAuth:RateLimit:TokenPermitsPerMinute", "1"),
                ("Mcp:OAuth:RateLimit:TokenBurst", "1"),
                ("Mcp:OAuth:RateLimit:PermitsPerMinute", "1"),
                ("Mcp:OAuth:RateLimit:Burst", "1"));
        }

        [Test]
        public async Task Token_BeyondTheBurst_IsSlowDown_WithRetryAfter_AndDoesNotReachEntra()
        {
            var first = await this.OAuthHandler.Token(Forwarded(OAuthRequest("post", "oauth/token", RefreshBody), "198.51.100.1, 203.0.113.7:50123"));
            var second = await this.OAuthHandler.Token(Forwarded(OAuthRequest("post", "oauth/token", RefreshBody), "192.0.2.99, 203.0.113.7:50124"));

            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(second.Headers.GetValues("Retry-After").Single(), Is.EqualTo("60"));
            Assert.That(second.Headers.GetValues("Content-Type"), Is.EqualTo(new[] { "application/json" }));
            Assert.That(second.Headers.GetValues("Cache-Control"), Is.EqualTo(new[] { "no-store" }));
            Assert.That(BodyOf(second), Does.StartWith("{\"error\":\"slow_down\","));
            Assert.That(this.Entra.Requests, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task AnotherClientAddress_HasItsOwnBucket()
        {
            await this.OAuthHandler.Token(Forwarded(OAuthRequest("post", "oauth/token", RefreshBody), "203.0.113.7:50123"));

            var other = await this.OAuthHandler.Token(Forwarded(OAuthRequest("post", "oauth/token", RefreshBody), "203.0.113.8:50123"));

            Assert.That(other.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public async Task Authorize_BeyondTheBurst_IsTheTooManyRequestsPage()
        {
            var query = $"oauth/authorize?client_id={BrokerClientId}&redirect_uri=http%3A%2F%2F127.0.0.1%3A53682%2Fcallback&response_type=code"
                + "&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256";
            await this.OAuthHandler.Authorize(OAuthRequest("get", query));

            var limited = await this.OAuthHandler.Authorize(OAuthRequest("get", query));

            Assert.That(limited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(limited.Headers.GetValues("Retry-After").Single(), Is.EqualTo("60"));
            Assert.That(limited.Headers.GetValues("Content-Type"), Is.EqualTo(new[] { "text/html; charset=utf-8" }));
            Assert.That(limited.Headers.GetValues("X-Frame-Options"), Is.EqualTo(new[] { "DENY" }));
            Assert.That(limited.Headers.Contains("Set-Cookie"), Is.False);
            Assert.That(BodyOf(limited), Does.Contain(ConsentPage.TooManyRequestsMessage));
        }

        [Test]
        public async Task ABodyThatIsNotUtf8_IsRefused()
        {
            var invalid = new byte[] { 0x7B, 0x22, 0x72, 0x22, 0x3A, 0x22, 0xC3, 0x28, 0x22, 0x7D };

            var register = await this.OAuthHandler.Register(RawRequest("oauth/register", invalid));
            var token = await this.OAuthHandler.Token(RawRequest("oauth/token", invalid));

            Assert.That(register.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(BodyOf(register), Does.StartWith("{\"error\":\"invalid_client_metadata\","));
            Assert.That(token.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(BodyOf(token), Does.StartWith("{\"error\":\"invalid_request\","));
        }

        private static HttpRequestData Forwarded(TestHttpRequestData request, string forwardedFor)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
            return request;
        }

        private static TestHttpRequestData RawRequest(string path, byte[] body)
        {
            var request = TestFactory.CreateHttpRequestData("post");
            request.SetUrl(new Uri(ApiBase, path));
            request.SetBodyAsStream(new MemoryStream(body));
            return request;
        }
    }
}
```


- [ ] **Step 2: Run the test build to verify it fails**

Run: `dotnet build SafeExchange.Tests\SafeExchange.Tests.csproj --nologo -v q`
Expected: FAIL:

```
SafeExchange.Tests\Mcp\OAuth\ClientAddressTests.cs(22,25): error CS0103: The name 'ClientAddress' does not exist in the current context
SafeExchange.Tests\Mcp\OAuth\ClientAddressTests.cs(33,25): error CS0103: The name 'ClientAddress' does not exist in the current context
```

- [ ] **Step 3: Implement**

Replace the whole content of `SafeExchange.Core.Functions\Mcp\OAuth\McpOAuthHandler.cs` with:

```csharp
/// <summary>
/// McpOAuthHandler
/// </summary>

namespace SafeExchange.Core.Functions.Mcp.OAuth
{
    using Microsoft.Azure.Functions.Worker.Http;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Globalization;
    using System.IO;
    using System.Net;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Functions adapters for the anonymous OAuth broker routes: each request is first checked against
    /// <see cref="McpOAuthThrottle"/> (client address = last <c>X-Forwarded-For</c> entry), then the request URL, the
    /// UTF-8 body and the consent cookie go into <see cref="McpOAuthBroker"/>, and its result goes out as status,
    /// <c>Location</c>, <c>Set-Cookie</c>, <c>Retry-After</c>, a JSON or HTML body (HTML with
    /// <see cref="ConsentPage.SecurityHeaders"/>) and <c>Cache-Control: no-store</c>. A disabled broker answers a bare 404.
    /// </summary>
    public sealed class McpOAuthHandler
    {
        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private readonly McpOAuthBroker broker;

        private readonly McpOAuthThrottle throttle;

        public McpOAuthHandler(McpOAuthBroker broker, McpOAuthThrottle throttle)
        {
            this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
            this.throttle = throttle ?? throw new ArgumentNullException(nameof(throttle));
        }

        public Task<HttpResponseData> Metadata(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return WriteAsync(request, this.broker.Metadata(McpHandler.ApiBaseUri(request.Url)));
        }

        public async Task<HttpResponseData> Register(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var result = this.Throttle(request, McpOAuthThrottle.Register)
                ?? this.broker.Register(McpHandler.ApiBaseUri(request.Url), await ReadBodyAsync(request));
            return await WriteAsync(request, result);
        }

        /// <summary>
        /// <c>GET</c> answers the consent page; <c>POST</c> is the consent form.
        /// </summary>
        public async Task<HttpResponseData> Authorize(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var apiBaseUri = McpHandler.ApiBaseUri(request.Url);
            var result = this.Throttle(request, McpOAuthThrottle.Authorize)
                ?? (string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
                    ? this.broker.AuthorizeConsent(apiBaseUri, await ReadBodyAsync(request), ReadConsentCookie(request))
                    : this.broker.Authorize(apiBaseUri, request.Url.Query));
            return await WriteAsync(request, result);
        }

        public Task<HttpResponseData> Callback(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var result = this.Throttle(request, McpOAuthThrottle.Callback)
                ?? this.broker.Callback(McpHandler.ApiBaseUri(request.Url), request.Url.Query, ReadConsentCookie(request));
            return WriteAsync(request, result);
        }

        public async Task<HttpResponseData> Token(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var result = this.Throttle(request, McpOAuthThrottle.Token)
                ?? await this.broker.TokenAsync(McpHandler.ApiBaseUri(request.Url), await ReadBodyAsync(request), request.FunctionContext.CancellationToken);
            return await WriteAsync(request, result);
        }

        // A disabled broker is not throttled: it answers 404 to everything.
        private McpOAuthResult? Throttle(HttpRequestData request, string endpoint)
            => this.broker.IsEnabled ? this.throttle.Check(endpoint, ReadClientAddress(request)) : null;

        private static string? ReadClientAddress(HttpRequestData request)
            => request.Headers.TryGetValues("X-Forwarded-For", out var values) ? ClientAddress.FromForwardedFor(string.Join(",", values)) : null;

        // HTTP/2 may split Cookie into several header fields; they are joined with "; " (RFC 9113 §8.2.3).
        private static string? ReadConsentCookie(HttpRequestData request)
            => request.Headers.TryGetValues("Cookie", out var values) ? ConsentCookie.Read(string.Join("; ", values)) : null;

        // Reads one character more than the broker accepts, so an oversize body is refused without buffering all of it.
        // A body that is not valid UTF-8 reads as null, which the broker refuses like an empty one.
        private static async Task<string?> ReadBodyAsync(HttpRequestData request)
        {
            using var reader = new StreamReader(request.Body ?? Stream.Null, StrictUtf8);
            var buffer = new char[McpOAuthBroker.MaxBodyLength + 1];
            var length = 0;
            int read;
            try
            {
                while (length < buffer.Length && (read = await reader.ReadAsync(buffer.AsMemory(length))) > 0)
                {
                    length += read;
                }
            }
            catch (DecoderFallbackException)
            {
                return null;
            }

            return new string(buffer, 0, length);
        }

        private static async Task<HttpResponseData> WriteAsync(HttpRequestData request, McpOAuthResult result)
        {
            var response = request.CreateResponse(result.StatusCode);
            if (result == McpOAuthResult.NotFound)
            {
                return response;
            }

            response.Headers.Add("Cache-Control", "no-store");
            if (result.Location is not null)
            {
                response.Headers.Add("Location", result.Location);
            }

            if (result.Cookie is not null)
            {
                response.Headers.Add("Set-Cookie", result.Cookie.ToSetCookieHeader());
            }

            if (result.RetryAfterSeconds is int retryAfter)
            {
                response.Headers.Add("Retry-After", retryAfter.ToString(CultureInfo.InvariantCulture));
            }

            if (result.Body is not null)
            {
                response.Headers.Add("Content-Type", result.ContentType);
                if (result.IsHtml)
                {
                    foreach (var header in ConsentPage.SecurityHeaders)
                    {
                        response.Headers.Add(header.Key, header.Value);
                    }
                }

                await response.WriteStringAsync(result.Body);
            }

            return response;
        }
    }
}
```

Create `SafeExchange.Core\Mcp\OAuth\ClientAddress.cs`:

```csharp
/// <summary>
/// ClientAddress
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;
    using System.Linq;
    using System.Net;

    /// <summary>
    /// The client address for rate limiting: the last <c>X-Forwarded-For</c> entry, which the App Service front end
    /// appends (<c>ip:port</c> or <c>[ipv6]:port</c>); the entries before it are whatever the client sent. Null when the
    /// last entry is missing or not an IP address.
    /// </summary>
    public static class ClientAddress
    {
        public static string? FromForwardedFor(string? forwardedFor)
        {
            var entry = (forwardedFor ?? string.Empty).Split(',').Last().Trim();
            if (entry.StartsWith('['))
            {
                var end = entry.IndexOf(']');
                entry = end > 0 ? entry[1..end] : string.Empty;
            }
            else if (entry.Count(c => c == ':') == 1)
            {
                entry = entry[..entry.IndexOf(':')];
            }

            return IPAddress.TryParse(entry, out var address) ? address.ToString() : null;
        }
    }
}
```


- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build SafeExchange.slnx --nologo -v q`, then the task filter and the phase filter.
Expected: `0 Error(s)`; task filter `Passed:   437`; phase filter `Passed:   670` (with the emulator).

- [ ] **Step 5: Commit**

```powershell
git add SafeExchange.Core.Functions\Mcp\OAuth\McpOAuthHandler.cs SafeExchange.Core\Mcp\OAuth\ClientAddress.cs SafeExchange.Tests\Mcp\OAuth\ClientAddressTests.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthHandlerThrottleTests.cs
git commit -m "feat(mcp-oauth): throttle the broker routes per client address; refuse non-UTF-8 bodies" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
git status --short
```


---

## Task 9: ARM parameter and Key Vault secret for the state key

No code tests cover the template; it is validated as JSON and, optionally, through the Bicep compiler (D5). The
parameter files are **not** changed.

**Files:**
- Modify: `deployment\current\arm\services-template.arm.json`

- [ ] **Step 1: Edit the template**

Modify `deployment\current\arm\services-template.arm.json` (2 edits):

Edit 1 of 2. Replace:

```json
                "description": "Application (client) ID of the Entra public client app used by the MCP OAuth broker (/api/oauth/*, /api/.well-known/oauth-authorization-server). Empty: the Mcp--OAuth--ClientId secret is not written (an existing value is kept; without one the broker answers 404). See docs/mcp.md."
            }
        },
        "settings-limits-maxAppsPerRegistrar": {
            "defaultValue": "3",
            "type": "string",
```

with:

```json
                "description": "Application (client) ID of the Entra public client app used by the MCP OAuth broker (/api/oauth/*, /api/.well-known/oauth-authorization-server). Empty: the Mcp--OAuth--ClientId secret is not written (an existing value is kept; without one the broker answers 404). See docs/mcp.md."
            }
        },
        "settings-mcp-oauthStateKey": {
            "defaultValue": "[base64(concat(newGuid(), newGuid()))]",
            "type": "securestring",
            "metadata": {
                "description": "HMAC key (base64, at least 32 bytes) that signs the MCP OAuth broker's consent token and state. Written to the Mcp--OAuth--StateKey secret only when settings-mcp-oauthClientId is set. Leave it unset: the default is new on every deployment (a redeploy rotates the key; only sign-ins in progress at that moment fail). See docs/mcp.md."
            }
        },
        "settings-limits-maxAppsPerRegistrar": {
            "defaultValue": "3",
            "type": "string",
```

Edit 2 of 2. Replace:

```json
                "value": "[parameters('settings-mcp-oauthClientId')]"
            }
        },
        {
            "type": "Microsoft.KeyVault/vaults/secrets",
            "apiVersion": "2023-02-01",
```

with:

```json
                "value": "[parameters('settings-mcp-oauthClientId')]"
            }
        },
        {
            "condition": "[not(empty(parameters('settings-mcp-oauthClientId')))]",
            "type": "Microsoft.KeyVault/vaults/secrets",
            "apiVersion": "2023-02-01",
            "name": "[concat(parameters('safeexchange_kv_name'), '/Mcp--OAuth--StateKey')]",
            "location": "[parameters('default_location')]",
            "dependsOn": [
                "[resourceId('Microsoft.KeyVault/vaults', parameters('safeexchange_kv_name'))]"
            ],
            "properties": {
                "attributes": {
                    "enabled": true
                },
                "contentType": "text/plain",
                "value": "[parameters('settings-mcp-oauthStateKey')]"
            }
        },
        {
            "type": "Microsoft.KeyVault/vaults/secrets",
            "apiVersion": "2023-02-01",
```


- [ ] **Step 2: Validate the template**

Run:

```powershell
$t = Get-Content deployment\current\arm\services-template.arm.json -Raw | ConvertFrom-Json
$p = $t.parameters.'settings-mcp-oauthStateKey'; "$($p.type) | $($p.defaultValue)"
$t.resources | Where-Object { $_.name -like '*Mcp--OAuth--*' } | ForEach-Object { "$($_.name) | $($_.condition) | $($_.properties.value)" }
foreach ($f in Get-ChildItem deployment\current\arm\services-parameters*.arm.json) { $j = Get-Content $f.FullName -Raw | ConvertFrom-Json; "$($f.Name): stateKey param present = $([bool]($j.parameters.PSObject.Properties.Name -contains 'settings-mcp-oauthStateKey'))" }
```

Expected:

```
securestring | [base64(concat(newGuid(), newGuid()))]
[concat(parameters('safeexchange_kv_name'), '/Mcp--OAuth--ClientId')] | [not(empty(parameters('settings-mcp-oauthClientId')))] | [parameters('settings-mcp-oauthClientId')]
[concat(parameters('safeexchange_kv_name'), '/Mcp--OAuth--StateKey')] | [not(empty(parameters('settings-mcp-oauthClientId')))] | [parameters('settings-mcp-oauthStateKey')]
services-parameters-prd.arm.json: stateKey param present = False
services-parameters-test.arm.json: stateKey param present = False
services-parameters.arm.json: stateKey param present = False
```

- [ ] **Step 3 (optional): check the `newGuid()` placement with the Bicep compiler**

Run (writes only to `arm-check\` in the worktree and removes it; needs the Azure CLI with Bicep):

```powershell
New-Item -ItemType Directory -Force arm-check | Out-Null
Copy-Item deployment\current\arm\services-template.arm.json arm-check\t.json
az bicep decompile --file arm-check\t.json --force 2>$null
Select-String -Path arm-check\t.bicep -Pattern 'param settings_mcp_oauthStateKey' | ForEach-Object Line
az bicep build --file arm-check\t.bicep --stdout 2>&1 | Select-String 'BCP065|Error'; "exit: $LASTEXITCODE"
Remove-Item -Recurse -Force arm-check
```

Expected: `param settings_mcp_oauthStateKey string = base64(concat(newGuid(), newGuid()))`, no `BCP065`/`Error` line,
`exit: 0` (decompile prints unrelated `no-unnecessary-dependson` linter warnings).

- [ ] **Step 4: Commit**

```powershell
git add deployment\current\arm\services-template.arm.json
git commit -m "feat(mcp-oauth): ARM securestring parameter and Key Vault secret for the broker state key" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
git status --short
```


---

## Task 10: Documentation (D, G runbook, consent UX, rate limits, https, local development)

**Files:**
- Modify: `docs\api-endpoints.md`
- Modify: `docs\mcp.md`

- [ ] **Step 1: Edit the docs**

Modify `docs\api-endpoints.md` (2 edits):

Edit 1 of 2. Replace:

```markdown

## MCP

SafeExchange exposes a first-party [Model Context Protocol](https://modelcontextprotocol.io) endpoint so AI agents can use secrets and files under the same permissions and audit as people. It is off unless `Features.McpEndpoint` is enabled; when off, the anonymous metadata and OAuth broker routes respond `404`, and `/api/mcp` responds `404` to authenticated callers (an unauthenticated call gets the normal `401`, without the `WWW-Authenticate` challenge, because authentication runs first). The OAuth broker routes additionally need `Mcp:OAuth:ClientId`. See [mcp.md](mcp.md) for setup, authentication, the OAuth broker, the tool list and the file transfer flows.

| Method | Path | Description |
|--------|------|-------------|
```

with:

```markdown

## MCP

SafeExchange exposes a first-party [Model Context Protocol](https://modelcontextprotocol.io) endpoint so AI agents can use secrets and files under the same permissions and audit as people. It is off unless `Features.McpEndpoint` is enabled; when off, the anonymous metadata and OAuth broker routes respond `404`, and `/api/mcp` responds `404` to authenticated callers (an unauthenticated call gets the normal `401`, without the `WWW-Authenticate` challenge, because authentication runs first). The OAuth broker routes additionally need `Mcp:OAuth:ClientId` and `Mcp:OAuth:StateKey`, are throttled per client address (`429` with `Retry-After`) and answer `404` over plain http except to localhost. See [mcp.md](mcp.md) for setup, authentication, the OAuth broker, the tool list and the file transfer flows.

| Method | Path | Description |
|--------|------|-------------|
```

Edit 2 of 2. Replace:

```markdown
| GET | `/api/.well-known/openid-configuration` | Anonymous authorization-server metadata of the OAuth broker (issuer `https://<host>/api`) |
| GET | `/api/.well-known/oauth-authorization-server` | Same document as `openid-configuration` |
| POST | `/api/oauth/register` | Anonymous RFC 7591 client registration (JSON). Loopback `redirect_uris` only; answers `201` with the configured Entra public `client_id` |
| GET | `/api/oauth/authorize` | Anonymous. Validates the authorization request (S256 PKCE, loopback `redirect_uri`, known `client_id`) and redirects (`302`) to Entra |
| GET | `/api/oauth/callback` | Anonymous. Entra's redirect target; redirects (`302`) the code or error to the client's loopback `redirect_uri` |
| POST | `/api/oauth/token` | Anonymous (form). Forwards `authorization_code` / `refresh_token` grants to Entra and relays Entra's status and JSON body (`Cache-Control: no-store`) |

When MCP is enabled, `401` responses additionally carry `WWW-Authenticate: Bearer resource_metadata="https://<host>/api/.well-known/oauth-protected-resource"` (the body is unchanged).
```

with:

```markdown
| GET | `/api/.well-known/openid-configuration` | Anonymous authorization-server metadata of the OAuth broker (issuer `https://<host>/api`) |
| GET | `/api/.well-known/oauth-authorization-server` | Same document as `openid-configuration` |
| POST | `/api/oauth/register` | Anonymous RFC 7591 client registration (JSON). Loopback `redirect_uris` only; answers `201` with the configured Entra public `client_id` |
| GET | `/api/oauth/authorize` | Anonymous. Validates the authorization request (S256 PKCE, loopback `redirect_uri`, known `client_id`) and answers the consent page (`200` HTML) with the `__Host-saex-mcp-consent` cookie |
| POST | `/api/oauth/authorize` | Anonymous (form from the consent page). Continue redirects (`302`) to Entra, Cancel redirects `access_denied` to the client; a missing cookie or tampered form answers a `400` page |
| GET | `/api/oauth/callback` | Anonymous. Entra's redirect target; verifies the signed `state` and the consent cookie, then redirects (`302`) the code or error to the client's loopback `redirect_uri` |
| POST | `/api/oauth/token` | Anonymous (form). Forwards `authorization_code` / `refresh_token` grants to Entra and relays Entra's status and JSON body (`Cache-Control: no-store`) |

When MCP is enabled, `401` responses additionally carry `WWW-Authenticate: Bearer resource_metadata="https://<host>/api/.well-known/oauth-protected-resource"` (the body is unchanged).
```

Modify `docs\mcp.md` (7 edits):

Edit 1 of 7. Replace:

```markdown
| `Features:McpEndpoint` | `Features--McpEndpoint` (`True` / `False`) | `settings-features-mcpEndpoint` | `False` |
| `Authentication:McpScopes` (optional) | `Authentication--McpScopes` | — (set the secret by hand) | empty |
| `Mcp:OAuth:ClientId` (optional) | `Mcp--OAuth--ClientId` | `settings-mcp-oauthClientId` (written only when not empty) | empty (OAuth broker off) |

- With the flag off, `GET /api/.well-known/oauth-protected-resource` and the [OAuth broker](#oauth-broker-for-entra-id) routes (all anonymous) answer `404`. `POST /api/mcp` answers `404` to authenticated callers; an unauthenticated call gets the normal `401` from authentication, because authentication runs before the handler checks the flag (it is deliberately not skipped based on the flag). Either way `401` responses carry no `WWW-Authenticate` challenge while the flag is off.
- The flag is read live (`IOptionsMonitor`), so no redeploy is needed. Key Vault configuration is reloaded every 5 minutes; restart the Function app to apply a change immediately.
- Production should keep the flag off until it is enabled explicitly. Staging (`test` environment) is where it is validated first (see [Staging validation](#staging-validation)).
- The `deployment\current\arm` templates define `settings-features-mcpEndpoint` (default `False`) and the `Features--McpEndpoint` secret, like the other `Features--*` settings, and `settings-mcp-oauthClientId` (default empty) for the `Mcp--OAuth--ClientId` secret, which is written only when the parameter is not empty (like `AADClient--ClientSecret`), so a value set by hand survives later deployments.

**Derived defaults.** The protected-resource metadata and `file_transfer_info` are derived from the request URL and the existing `Authentication` section:

| Value | Derived from |
|-------|--------------|
| `resource` | The MCP URL of the host that served the request: `https://<host>/api/mcp` |
| `authorization_servers` | `https://<host>/api` (the [OAuth broker](#oauth-broker-for-entra-id)) when `Mcp:OAuth:ClientId` is set; otherwise `Authentication:Authority`, with `/v2.0` appended when missing |
| API resource (for scopes) | First `api://…` entry of `Authentication:ValidAudiences`; if there is none, the first entry |
| `scopes_supported` (user scope) | `Authentication:McpScopes` (comma-separated) when set, otherwise `{API resource}/user_impersonation` |
| S2S application scope | `{API resource}/.default` |
```

with:

```markdown
| `Features:McpEndpoint` | `Features--McpEndpoint` (`True` / `False`) | `settings-features-mcpEndpoint` | `False` |
| `Authentication:McpScopes` (optional) | `Authentication--McpScopes` | — (set the secret by hand) | empty |
| `Mcp:OAuth:ClientId` (optional) | `Mcp--OAuth--ClientId` | `settings-mcp-oauthClientId` (written only when not empty) | empty (OAuth broker off) |
| `Mcp:OAuth:StateKey` (with the client ID) | `Mcp--OAuth--StateKey` | `settings-mcp-oauthStateKey` (`securestring`; leave it unset; written only when `settings-mcp-oauthClientId` is not empty) | empty (OAuth broker off); the ARM default is a new random key per deployment |
| `Mcp:OAuth:RateLimit:*` (optional) | `Mcp--OAuth--RateLimit--*` | — (set the secret by hand) | see [Rate limits](#rate-limits) |

- With the flag off, `GET /api/.well-known/oauth-protected-resource` and the [OAuth broker](#oauth-broker-for-entra-id) routes (all anonymous) answer `404`. `POST /api/mcp` answers `404` to authenticated callers; an unauthenticated call gets the normal `401` from authentication, because authentication runs before the handler checks the flag (it is deliberately not skipped based on the flag). Either way `401` responses carry no `WWW-Authenticate` challenge while the flag is off.
- The flag is read live (`IOptionsMonitor`), so no redeploy is needed. Key Vault configuration is reloaded every 5 minutes; restart the Function app to apply a change immediately.
- Production should keep the flag off until it is enabled explicitly. Staging (`test` environment) is where it is validated first (see [Staging validation](#staging-validation)).
- The `deployment\current\arm` templates define `settings-features-mcpEndpoint` (default `False`) and the `Features--McpEndpoint` secret, like the other `Features--*` settings, and `settings-mcp-oauthClientId` (default empty) for the `Mcp--OAuth--ClientId` secret, which is written only when the parameter is not empty (like `AADClient--ClientSecret`), so a value set by hand survives later deployments. Under the same condition the template writes `Mcp--OAuth--StateKey` from `settings-mcp-oauthStateKey`, a `securestring` whose default is `[base64(concat(newGuid(), newGuid()))]` (72 random-GUID bytes; ARM allows `newGuid()` only inside a parameter default). The parameter is deliberately absent from the parameter files, so every deployment rotates the key; only sign-ins in progress at that moment fail and are simply started again.

**Derived defaults.** The protected-resource metadata and `file_transfer_info` are derived from the request URL and the existing `Authentication` section:

| Value | Derived from |
|-------|--------------|
| `resource` | The MCP URL of the host that served the request: `https://<host>/api/mcp` |
| `authorization_servers` | `https://<host>/api` (the [OAuth broker](#oauth-broker-for-entra-id)) when `Mcp:OAuth:ClientId` and a valid `Mcp:OAuth:StateKey` are set; otherwise `Authentication:Authority`, with `/v2.0` appended when missing |
| API resource (for scopes) | First `api://…` entry of `Authentication:ValidAudiences`; if there is none, the first entry |
| `scopes_supported` (user scope) | `Authentication:McpScopes` (comma-separated) when set, otherwise `{API resource}/user_impersonation` |
| S2S application scope | `{API resource}/.default` |
```

Edit 2 of 7. Replace:

```markdown

### OAuth broker for Entra ID

With `Mcp:OAuth:ClientId` set (and `Features.McpEndpoint` on), SafeExchange presents a small, standards-shaped authorization server to MCP clients and delegates every real decision to Entra. It never issues, stores or inspects tokens: clients receive Entra's tokens, which the backend validates as before.

| Route (anonymous) | Purpose |
|-------------------|---------|
| `GET /api/.well-known/openid-configuration`, `GET /api/.well-known/oauth-authorization-server` | Authorization-server metadata (same JSON): issuer `https://<host>/api`, the three endpoints below, `code` + S256 PKCE, `token_endpoint_auth_methods_supported: ["none"]`, scopes = the user scope + `offline_access` |
| `POST /api/oauth/register` | RFC 7591 registration. Always answers `201` with the configured Entra public `client_id` and `token_endpoint_auth_method: none` |
| `GET /api/oauth/authorize` | Validates the request and redirects (`302`) to Entra's `/oauth2/v2.0/authorize` with the broker's client ID, the callback below, the API scope + `offline_access`, the client's S256 challenge and a wrapped `state` |
| `GET /api/oauth/callback` | Entra's redirect target. Forwards `code` and the client's `state` (or `error`, `error_description`, `state`) to the client's loopback redirect URI |
| `POST /api/oauth/token` | Forwards an `authorization_code` or `refresh_token` grant to Entra's `/oauth2/v2.0/token` and relays Entra's status and JSON body (`Cache-Control: no-store`) |

Rules the broker enforces:

- **Loopback redirects only.** Client `redirect_uri`s must be `http` to `127.0.0.1`, `localhost` or `[::1]` (any port and path, no fragment, no user info, at most 2048 characters), checked at register, authorize and again at callback. A code can only reach the signing-in user's own machine, and PKCE means only the client holding the verifier can redeem it, so the broker is not an open redirect.
- **One client.** `client_id` must equal `Mcp:OAuth:ClientId`. An unknown client or a bad `redirect_uri` is answered with `400` JSON and never redirected; other authorize errors (`response_type` other than `code`, missing S256 PKCE, a `state` too long) are redirected to the client as `error=…`.
- **Rewritten requests.** `resource`, `client_secret` and unknown parameters are dropped; `scope` is replaced by the API scope plus `offline_access`; the token request always uses the broker's client ID and callback and is posted to the token endpoint derived from `Authentication:Authority` (nothing the caller sends changes the URL). A repeated parameter is refused.
- **State.** Entra receives `base64url({"r": clientRedirect, "s": clientState})` (at most 2 KB). It carries no authority, so it is not signed or encrypted; it is size- and format-checked and the redirect is re-validated on decode.
- **Logging.** The broker logs route, outcome and Entra error codes only, never codes, verifiers, tokens or state. The Functions platform's own request telemetry may still record request URLs, which for the callback include the single-use, PKCE-bound code.
- **Off by default.** Every broker route answers `404` unless `Features.McpEndpoint` is on, `Mcp:OAuth:ClientId` is set and `Authentication:Authority` is an https URL.

#### Discovery at the host root
```

with:

````markdown

### OAuth broker for Entra ID

With `Mcp:OAuth:ClientId` and `Mcp:OAuth:StateKey` set (and `Features.McpEndpoint` on), SafeExchange presents a small, standards-shaped authorization server to MCP clients and delegates every real decision to Entra. It never issues, stores or inspects tokens: clients receive Entra's tokens, which the backend validates as before.

| Route (anonymous) | Purpose |
|-------------------|---------|
| `GET /api/.well-known/openid-configuration`, `GET /api/.well-known/oauth-authorization-server` | Authorization-server metadata (same JSON): issuer `https://<host>/api`, the three endpoints below, `code` + S256 PKCE, `token_endpoint_auth_methods_supported: ["none"]`, scopes = the user scope + `offline_access` |
| `POST /api/oauth/register` | RFC 7591 registration. Always answers `201` with the configured Entra public `client_id` and `token_endpoint_auth_method: none` |
| `GET /api/oauth/authorize` | Validates the request and answers the SafeExchange **consent page** (`200` HTML) and the `__Host-saex-mcp-consent` cookie; nothing goes to Entra yet |
| `POST /api/oauth/authorize` | The consent form. **Continue** redirects (`302`) to Entra's `/oauth2/v2.0/authorize` with the broker's client ID, the callback below, the API scope + `offline_access`, the client's S256 challenge, `login_hint` when present and a signed `state`; **Cancel** redirects `error=access_denied` to the client |
| `GET /api/oauth/callback` | Entra's redirect target. Verifies the signed `state` and the consent cookie, then forwards `code` and the client's `state` (or `error`, `error_description`, `state`) to the client's loopback redirect URI and clears the cookie |
| `POST /api/oauth/token` | Forwards an `authorization_code` or `refresh_token` grant to Entra's `/oauth2/v2.0/token` and relays Entra's status and JSON body (`Cache-Control: no-store`) |

Rules the broker enforces:

- **Loopback redirects only.** Client `redirect_uri`s must be `http` to `127.0.0.1`, `localhost` or `[::1]` (any port and path, no fragment, no user info, at most 2048 characters), checked at register, authorize, consent and again at callback. A code can only reach the signing-in user's own machine, and PKCE means only the client holding the verifier can redeem it, so the broker is not an open redirect.
- **One client.** `client_id` must equal `Mcp:OAuth:ClientId`. An unknown client or a bad `redirect_uri` is answered with `400` JSON and never redirected; other authorize errors (`response_type` other than `code`, missing S256 PKCE, a `state` too long) are redirected to the client as `error=…`.
- **User consent.** Every client shares one static, pre-authorised Entra client, so Entra itself never asks the user whether *this* application may sign in (the MCP "confused deputy" risk). The broker therefore shows its own consent page first: *"Sign in an MCP client to SafeExchange — An application on this computer is asking for access to SafeExchange as you. It will receive the result at **http://127.0.0.1:&lt;port&gt;**. Access requested: read and manage your secrets; stay signed in. Only continue if you just started a sign-in in an MCP client such as Copilot CLI."* with **Continue** and **Cancel**. The page has no JavaScript, HTML-encodes every value and is served with `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'`, `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer` and `Cache-Control: no-store`. Approvals are not remembered: every sign-in shows the page.
- **Consent binding.** The page sets `__Host-saex-mcp-consent` = a random 32-byte nonce (`Secure; HttpOnly; SameSite=Lax; Path=/; Max-Age=600`) and its form carries the validated parameters, an expiry and `consent_token` = HMAC-SHA256(`Mcp:OAuth:StateKey`, nonce + parameters). The post is accepted only with the same browser's cookie, an untampered token and within 10 minutes; anything else answers a `400` page (*"This sign-in request is invalid or expired. Start the sign-in again from your MCP client."*) and never redirects.
- **No `prompt` passthrough.** `prompt` (for example `prompt=none`, which would issue a code silently) is never forwarded to Entra, from the authorize request or the form; `login_hint` is.
- **Signed state.** Entra receives `base64url({"r": clientRedirect, "s": clientState, "n": nonce, "e": expiry}) + "." + base64url(HMAC-SHA256)` (at most 2 KB, valid 10 minutes). The callback checks the HMAC in fixed time, the expiry, that `n` equals the consent cookie and that the redirect is still loopback; otherwise it answers the `400` page.
- **Rewritten requests.** `resource`, `client_secret` and unknown parameters are dropped; `scope` is replaced by the API scope plus `offline_access`; the token request always uses the broker's client ID and callback and is posted to the token endpoint derived from `Authentication:Authority` (nothing the caller sends changes the URL). A repeated parameter is refused, and so is JSON with an invalid escape or a body that is not UTF-8.
- **Token relay.** Entra's answer is relayed unchanged only when its status is `200`, `400` or `401` and its body is a JSON object; anything else (a `5xx`, a proxy's HTML page) is logged with its status and answered `502` `{"error":"server_error"}`. At most 16 Entra calls run at once per instance; a token request that finds no free slot within 2 s answers `503` `{"error":"temporarily_unavailable"}` with `Retry-After: 2`.
- **Rate limits.** See [Rate limits](#rate-limits).
- **https only.** The broker and the protected-resource metadata answer `404` when the derived base URL is plain `http` to anything but `localhost`, `127.0.0.1` or `[::1]` (local development), and log one Warning per instance.
- **Logging.** The broker logs route, outcome and Entra error codes only, never codes, verifiers, tokens, state, cookies or nonces; refusals, consent and state failures, rate-limit hits and Entra problems log at Warning. The Functions platform's own request telemetry may still record request URLs, which for the callback include the single-use, PKCE-bound code.
- **Off by default.** Every broker route answers `404` unless `Features.McpEndpoint` is on, `Mcp:OAuth:ClientId` is set, `Mcp:OAuth:StateKey` is base64 of at least 32 bytes and `Authentication:Authority` is an https URL.

#### Rate limits

The anonymous broker routes are throttled in memory, per instance, per client address with token buckets (configuration `Mcp:OAuth:RateLimit`, Key Vault `Mcp--OAuth--RateLimit--<Name>`):

| Setting | Default | Applies to |
|---------|---------|------------|
| `TokenPermitsPerMinute` / `TokenBurst` | `30` / `30` | `POST /api/oauth/token` |
| `PermitsPerMinute` / `Burst` | `60` / `60` | each of `register`, `authorize` (GET and POST together) and `callback` |
| `MaxClients` | `10000` | buckets kept per instance (least recently used dropped) |
| `MaxConcurrentEntraCalls` | `16` | concurrent outbound Entra token calls per instance |

Over the limit, `register` and `token` answer `429` `{"error":"slow_down"}` and the browser routes a `429` page, all with `Retry-After` in seconds. The client address is the **last** `X-Forwarded-For` entry, which the App Service front end appends (the entries before it are whatever the client sent, so they are ignored); a request without one shares the bucket `unknown`. This is best effort: each instance counts on its own and the buckets reset on restart. Edge rate limiting (Front Door or API Management in front of the Function app) is the stronger option and a planned follow-up.

#### Local development

`func start` serves the broker on `http://localhost:7071` (loopback http is allowed). Set the three values in `SafeExchange.Functions\local.settings.json` (`Values`) or as environment variables:

```powershell
$env:Features__McpEndpoint = 'True'
$env:Mcp__OAuth__ClientId  = '<broker client app ID>'
$env:Mcp__OAuth__StateKey  = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

The broker client's redirect URIs then also need `http://localhost:7071/api/oauth/callback`.

#### Discovery at the host root
````

Edit 3 of 7. Replace:

````markdown

The broker client can be a separate public client app (below) or the API app itself: mark it as a public client (`--is-fallback-public-client true`) and add the redirect URI `https://<host>/api/oauth/callback` to its public-client (mobile and desktop) redirect URIs. The staging environment uses the API app as the broker client. A separate client needs the pre-authorisation in step 3; the API app itself does not.

Create a public client app for the broker and pre-authorise it on the API app, so users get no consent prompt. In PowerShell, signed in with `az login` as someone who can manage both apps:

```powershell
$apiAppId = '<API app (client) ID>'                      # the app whose api:// URI is in Authentication:ValidAudiences
````

with:

````markdown

The broker client can be a separate public client app (below) or the API app itself: mark it as a public client (`--is-fallback-public-client true`) and add the redirect URI `https://<host>/api/oauth/callback` to its public-client (mobile and desktop) redirect URIs. The staging environment uses the API app as the broker client. A separate client needs the pre-authorisation in step 3; the API app itself does not.

Create a public client app for the broker and pre-authorise it on the API app, so users get no Entra consent prompt (the broker shows its own [consent page](#oauth-broker-for-entra-id) instead). In PowerShell, signed in with `az login` as someone who can manage both apps:

```powershell
$apiAppId = '<API app (client) ID>'                      # the app whose api:// URI is in Authentication:ValidAudiences
````

Edit 4 of 7. Replace:

````markdown
$clientAppId
```

Then give SafeExchange the client ID: set `settings-mcp-oauthClientId` to `$clientAppId` in `deployment\current\arm\services-parameters-<env>.arm.json` and run `deployment\deploy.ps1` (it is not a secret), or write the secret directly with `az keyvault secret set --vault-name <key vault> --name Mcp--OAuth--ClientId --value $clientAppId` (an empty parameter never overwrites it). Key Vault configuration reloads every 5 minutes; restart the Function app to apply it at once. Check:

```powershell
curl.exe -s "https://$apiHost/api/.well-known/oauth-protected-resource"   # authorization_servers = ["https://<host>/api"]
curl.exe -s "https://$apiHost/api/.well-known/openid-configuration"       # issuer = "https://<host>/api"
```

To turn the broker off again, delete or disable the `Mcp--OAuth--ClientId` secret (or turn `Features.McpEndpoint` off).

### Fallback: bearer header from the Azure CLI
````

with:

````markdown
$clientAppId
```

Then give SafeExchange the client ID: set `settings-mcp-oauthClientId` to `$clientAppId` in `deployment\current\arm\services-parameters-<env>.arm.json` and run `deployment\deploy.ps1` (it is not a secret); the deployment also writes a fresh `Mcp--OAuth--StateKey`. Or write both secrets directly (an empty parameter never overwrites them):

```powershell
az keyvault secret set --vault-name <key vault> --name Mcp--OAuth--ClientId --value $clientAppId
az keyvault secret set --vault-name <key vault> --name Mcp--OAuth--StateKey --value ([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
```

Key Vault configuration reloads every 5 minutes; restart the Function app to apply it at once. Check:

```powershell
curl.exe -s "https://$apiHost/api/.well-known/oauth-protected-resource"   # authorization_servers = ["https://<host>/api"]
curl.exe -s "https://$apiHost/api/.well-known/openid-configuration"       # issuer = "https://<host>/api"
```

**Turning the broker off.** Either set the `Features--McpEndpoint` secret to `False` (the MCP endpoint, its metadata and the broker all answer `404`), or delete or disable the `Mcp--OAuth--ClientId` secret (only the broker goes; `authorization_servers` falls back to Entra). Redeploying with an empty `settings-mcp-oauthClientId` does **not** turn it off: an empty parameter leaves an existing secret in place. Key Vault configuration reloads every 5 minutes; restart the Function app to apply it at once.

### Fallback: bearer header from the Azure CLI
````

Edit 5 of 7. Replace:

```markdown
- **No MCP-specific size caps** (owner decision): the existing backend limits and the Functions default request size limit apply. Trade-off: `/api/mcp` buffers the JSON request body in memory (several copies per request), so very large in-band chunks can pressure worker memory. Keep chunks at most 256 KB and use `file_transfer_info` for large files.
- **Caller resolution costs one set of database lookups per MCP message**, the same as one REST call; that is what lets the kill switch apply on the next call.
- No `Origin` check is done: the endpoint is remote and needs a bearer token (no cookies), so DNS-rebinding does not apply.

## Client configuration
```

with:

```markdown
- **No MCP-specific size caps** (owner decision): the existing backend limits and the Functions default request size limit apply. Trade-off: `/api/mcp` buffers the JSON request body in memory (several copies per request), so very large in-band chunks can pressure worker memory. Keep chunks at most 256 KB and use `file_transfer_info` for large files.
- **Caller resolution costs one set of database lookups per MCP message**, the same as one REST call; that is what lets the kill switch apply on the next call.
- No `Origin` check is done: the endpoint is remote and needs a bearer token (no cookies), so DNS-rebinding does not apply.
- **Token redemptions reach Entra from the Function app.** The broker posts authorization-code and refresh-token redemptions to Entra itself, so Entra sees the Function app's outbound IP address, not the user's. IP- or location-based Conditional Access policies and Identity Protection sign-in risk evaluate that address for these requests (the interactive sign-in in the browser still comes from the user's own address).
- **Broker throttling is per instance and best effort** (see [Rate limits](#rate-limits)); put edge rate limiting in front of the Function app for a hard limit.

## Client configuration
```

Edit 6 of 7. Replace:

````markdown
}
```

If the server needs OAuth, Copilot opens a browser to sign in (or asks you to run `/mcp auth safeexchange`). With the [OAuth broker](#oauth-broker-for-entra-id) configured, Copilot registers through `/api/oauth/register`, the browser signs in with Entra and the token is stored and refreshed by Copilot. Without the broker, OAuth discovery does not work against Entra (see [Authentication](#authentication)); send a static token as a header:

```json
{
````

with:

````markdown
}
```

If the server needs OAuth, Copilot opens a browser to sign in (or asks you to run `/mcp auth safeexchange`). With the [OAuth broker](#oauth-broker-for-entra-id) configured, Copilot registers through `/api/oauth/register`, the browser shows the SafeExchange consent page (choose **Continue** only if you just started the sign-in), signs in with Entra and the token is stored and refreshed by Copilot. Without the broker, OAuth discovery does not work against Entra (see [Authentication](#authentication)); send a static token as a header:

```json
{
````

Edit 7 of 7. Replace:

```markdown

1. **REST regression** with the current web app (v3) and an older web app build (v2): create a secret, upload and download an attachment (hashed and legacy), grant and revoke access, audit view, pinned items, S2S apps pages.
2. **MCP as an S2S agent:** register an app and grant it on a test secret; move a small file in-band; move a large synthetic file (tens of MB) through `file_transfer_info` and REST with the agent's own token; all SHA-256 values match; disable the app and confirm the next MCP call returns `403`.
3. **MCP as a user from Copilot CLI** via OAuth discovery through the broker (after the [Entra setup](#entra-setup-once-per-environment)): `/mcp auth safeexchange` opens the browser, signs in without a consent prompt and the next tool call succeeds; repeat after the access token expires to see the refresh. If a client cannot complete OAuth, use the `az account get-access-token` header fallback and record the outcome per client.
4. **Audit:** events from MCP show `channel = mcp` and the client label, REST events show nothing new, and the audit chain verifies including events from before the change.
5. **Log scan** (Application Insights and Functions logs) for the whole run: no secret values, file content, tokens, authorization codes or code verifiers in traces (request URLs of `/api/oauth/callback` are platform telemetry, see the broker's logging rule).
```

with:

```markdown

1. **REST regression** with the current web app (v3) and an older web app build (v2): create a secret, upload and download an attachment (hashed and legacy), grant and revoke access, audit view, pinned items, S2S apps pages.
2. **MCP as an S2S agent:** register an app and grant it on a test secret; move a small file in-band; move a large synthetic file (tens of MB) through `file_transfer_info` and REST with the agent's own token; all SHA-256 values match; disable the app and confirm the next MCP call returns `403`.
3. **MCP as a user from Copilot CLI** via OAuth discovery through the broker (after the [Entra setup](#entra-setup-once-per-environment)): `/mcp auth safeexchange` opens the browser on the SafeExchange consent page; **Continue** signs in with Entra without an Entra consent prompt and the next tool call succeeds (**Cancel** makes the client report `access_denied`); repeat after the access token expires to see the refresh. If a client cannot complete OAuth, use the `az account get-access-token` header fallback and record the outcome per client.
4. **Audit:** events from MCP show `channel = mcp` and the client label, REST events show nothing new, and the audit chain verifies including events from before the change.
5. **Log scan** (Application Insights and Functions logs) for the whole run: no secret values, file content, tokens, authorization codes or code verifiers in traces (request URLs of `/api/oauth/callback` are platform telemetry, see the broker's logging rule).
```


- [ ] **Step 2: Check the anchors and the wording**

Run: `Select-String -Path docs\mcp.md -Pattern '\(#rate-limits\)|^#### Rate limits|^#### Local development|Turning the broker off|Token redemptions reach Entra' | ForEach-Object { "$($_.LineNumber): $($_.Line.Substring(0, [Math]::Min(90, $_.Line.Length)))" }`
Expected: seven lines — the `#### Rate limits` and `#### Local development` headings, three `(#rate-limits)` links
(the Enabling table, the broker rules and the security notes), the **Turning the broker off** paragraph and the
security note **Token redemptions reach Entra from the Function app.**

- [ ] **Step 3: Commit**

```powershell
git add docs\api-endpoints.md docs\mcp.md
git commit -m "feat(mcp-oauth): document consent, signed state, rate limits, https-only, switch-off and the Entra egress note" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
git status --short
```


---

## Task 11: Full verification and the plan

**Files:**
- Create: `docs\superpowers\plans\2026-11-22-mcp-oauth-broker-hardening.md` (this file)

- [ ] **Step 1: Clean build**

Run: `dotnet build SafeExchange.slnx --nologo -v q --no-incremental`
Expected: `704 Warning(s)`, `0 Error(s)` (unchanged from Task 0).

- [ ] **Step 2: Phase filter**

Run the phase filter (ground rule 6).
Expected: `Passed:   670` with the emulator (without it: `Failed:     1, Passed:   669`, the one test of ground rule 6).

- [ ] **Step 3: Full suite against the Cosmos emulator**

The test project reads `ConnectionStrings:CosmosDb` from its user secrets; the emulator image serves plain http.
Point the secret at http for the run and **restore it afterwards**:

```powershell
docker run --detach --name saex-test-cosmos --publish 127.0.0.1:8081:8081 --publish 127.0.0.1:8080:8080 mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-EN20260907pre
for ($i = 0; $i -lt 60; $i++) { try { if ((Invoke-WebRequest -UseBasicParsing http://localhost:8080/ready -TimeoutSec 3).StatusCode -eq 200) { break } } catch { }; Start-Sleep 3 }
dotnet user-secrets set "ConnectionStrings:CosmosDb" "AccountEndpoint=http://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==" --project SafeExchange.Tests\SafeExchange.Tests.csproj
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo
dotnet user-secrets set "ConnectionStrings:CosmosDb" "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==" --project SafeExchange.Tests\SafeExchange.Tests.csproj
docker rm -f saex-test-cosmos
```

(The key is the emulator's public, well-known key.)
Expected: `Passed!  - Failed:     0, Passed:  1403, Skipped:     0, Total:  1403, …`; afterwards
`dotnet user-secrets list --project SafeExchange.Tests\SafeExchange.Tests.csproj` shows the `https://localhost:8081/`
value again and `docker ps -a --filter name=saex-test-cosmos` lists nothing.

- [ ] **Step 4: Spec coverage check**

| Spec item | Where |
|---|---|
| A consent page, cookie, POST allow/deny, 400 page, no `prompt`, signed state, callback cookie check + expiry | Tasks 2, 3, 4 (D1–D4, D6) |
| A key `Mcp:OAuth:StateKey`, broker enabled only with it, ARM securestring default + secret condition | Tasks 1, 9 (D5) |
| B per-IP token buckets, 429 + `Retry-After`, bounded store, Entra concurrency cap + 503 | Tasks 5, 6, 8 (D7–D9) |
| C `InvalidOperationException` (register body, state payload), invalid UTF-8 | Tasks 2, 6, 8 (D4, D12) |
| D refresh/code relay egress note | Task 10 |
| E `no-store` metadata, https-or-loopback with one Warning | Task 7 (D11) |
| F Warning-level refusals, richer Entra-unreachable log, no secrets in logs | Tasks 4, 5, 6 (D13) |
| G relay only 200/400/401 JSON else 502; switch-off runbook | Tasks 6, 10 (D10) |
| Route guard and contract tests green; `FunctionRouteTests` authorize methods only | Task 4 |

- [ ] **Step 5 (optional): smoke-test a real Functions host**

Needs Azure Functions Core Tools and Azurite. From the worktree root, in one terminal:
`azurite --silent --location .\azurite-data --skipApiVersionCheck`; in a second one:

```powershell
cd SafeExchange.Functions
$env:Features__McpEndpoint = 'True'
$env:Mcp__OAuth__ClientId  = '00000000-0000-0000-0000-0000000000c9'
$env:Mcp__OAuth__StateKey  = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
func start --port 7071
```

When the host prints `Host started`, run in a third terminal (from the worktree root):

```powershell
$q = "client_id=00000000-0000-0000-0000-0000000000c9&redirect_uri=http%3A%2F%2F127.0.0.1%3A53682%2Fcallback&response_type=code&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256&state=abc&prompt=none"
$raw = curl.exe -s -i "http://localhost:7071/api/oauth/authorize?$q" | Out-String
$raw.Split("`n") | Select-Object -First 11
$cookie = ([regex]::Match($raw, "Set-Cookie: (__Host-saex-mcp-consent=[^;]+)")).Groups[1].Value
$form = (([regex]::Matches($raw, '<input type="hidden" name="([^"]*)" value="([^"]*)">') | ForEach-Object { [Uri]::EscapeDataString([Net.WebUtility]::HtmlDecode($_.Groups[1].Value)) + "=" + [Uri]::EscapeDataString([Net.WebUtility]::HtmlDecode($_.Groups[2].Value)) }) -join "&") + "&decision=allow"
$location = (curl.exe -s -i -X POST -H "Cookie: $cookie" --data $form "http://localhost:7071/api/oauth/authorize" | Select-String "^Location:").Line
"prompt forwarded: $($location -match 'prompt=')"; "signed state: $($location -match '&state=[A-Za-z0-9_-]+\.[A-Za-z0-9_-]{43}')"
curl.exe -s -i -X POST --data $form "http://localhost:7071/api/oauth/authorize" | Select-Object -First 2
curl.exe -s -i "http://localhost:7071/api/oauth/callback?code=c&state=x" | Select-Object -First 2
curl.exe -s -i "http://localhost:7071/api/.well-known/oauth-protected-resource" | Select-String "HTTP/|Cache-Control"
$codes = 1..61 | ForEach-Object { curl.exe -s -o NUL -w "%{http_code}" -H "X-Forwarded-For: 198.51.100.9, 203.0.113.50:1234" "http://localhost:7071/api/oauth/callback?code=c&state=x" }
curl.exe -s -i -H "X-Forwarded-For: 203.0.113.50:9" "http://localhost:7071/api/oauth/callback?code=c&state=x" | Select-String "HTTP/|Retry-After"
```

Expected: `HTTP/1.1 200 OK` with `Content-Type: text/html; charset=utf-8`, `Cache-Control: no-store`,
`Set-Cookie: __Host-saex-mcp-consent=<43 characters>; Max-Age=600; Path=/; Secure; HttpOnly; SameSite=Lax`, the
`Content-Security-Policy`, `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff` and
`Referrer-Policy: no-referrer` headers; `prompt forwarded: False`; `signed state: True`; `HTTP/1.1 400 Bad Request`
twice (form without cookie, forged callback); the metadata with `HTTP/1.1 200 OK` and `Cache-Control: no-store`;
`HTTP/1.1 429 Too Many Requests` with `Retry-After: 1` for the last request (the 60-request burst plus about one refill
token were used up by then). The host console shows only lines such as `MCP OAuth authorize answered the consent
page.`, `MCP OAuth authorize redirected to Entra.`, `MCP OAuth authorize refused the request: missing_cookie.`,
`MCP OAuth callback refused the request: invalid_state.` and `MCP OAuth callback rate limit exceeded; retry after 1 s.`
— never the cookie value or the consent token. Stop `func` and Azurite (Ctrl+C in their terminals) and delete
`.\azurite-data`.

- [ ] **Step 6: Commit the plan**

```powershell
git add docs\superpowers\plans\2026-11-22-mcp-oauth-broker-hardening.md
git commit -m "feat(mcp-oauth): implementation plan for the broker hardening" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
git status --short
```

Expected: `git status` prints nothing; `git --no-pager log --oneline -11` lists the 11 commits of this plan.

---

## Risks and follow-ups

- **Key rotation per deployment.** Every ARM deployment writes a new `Mcp--OAuth--StateKey`. Sign-ins in progress
  at that moment fail with the `400` page; Key Vault configuration reloads every 5 minutes per instance, so for up to
  5 minutes after a deployment instances may hold different keys and a sign-in whose steps land on different instances
  can fail as well. Restarting the Function app after the deployment shortens the window. Pin the parameter in a
  parameter file only if this becomes a problem.
- **Client address behind another proxy.** With Front Door or API Management in front, the last `X-Forwarded-For`
  entry is that proxy and all users would share a bucket; switch the extraction (or add edge rate limiting, the planned
  follow-up) before putting a proxy in front of the API.
- **Per-instance, in-memory limits.** Scale-out multiplies the effective limit, and a restart resets it. Edge rate
  limiting remains the stronger control (documented as a follow-up).
- **Warning volume.** Each throttled request logs one Warning (without the address); a flood produces a matching log
  volume. Application Insights sampling applies; aggregate or sample the message if the volume becomes costly.
- **One more click for users.** Every sign-in shows the consent page (approvals are not remembered, by spec).
  **Cancel** surfaces as `access_denied` in the MCP client.
- **Cookies on local http.** `Secure` `__Host-` cookies on `http://localhost` work in Chromium and Firefox (localhost
  is a secure context); a browser that refuses them makes the local flow end on the `400` page.
- **Behaviour changes for callers.** The callback and the consent post now answer an HTML `400` page instead of JSON,
  and `prompt` is no longer honoured; Entra responses other than `200`/`400`/`401` JSON objects become `502`.
