# MCP OAuth broker for Entra ID — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Let MCP clients such as Copilot CLI (Rust SDK `rmcp`) sign in to `/api/mcp` with Entra ID by publishing
the MCP URL as the protected resource and adding a small, anonymous OAuth broker on SafeExchange that hands out a
fixed Entra public client, relays the authorization-code + PKCE flow through its own callback and forwards token
requests to Entra — without ever issuing, storing or inspecting tokens.

**Architecture:** Everything with logic is host-agnostic and pure in `SafeExchange.Core\Mcp\OAuth`
(`McpOAuthOptions`, `LoopbackRedirectUri`, `BrokerState`, `OAuthParameters`, `EntraEndpoints`, `McpOAuthPaths`,
`AuthorizationServerMetadata`, `McpOAuthResult`, `McpOAuthBroker`, `IEntraTokenClient` + `EntraTokenClient`).
`McpProtectedResource` publishes `resource` = `https://<host>/api/mcp` and, when the broker is configured,
`authorization_servers` = `["https://<host>/api"]`. `SafeExchange.Core.Functions\Mcp\OAuth\McpOAuthHandler` is the
thin `HttpRequestData` adapter, `SafeExchange.Functions\Functions\SafeMcpOAuth.cs` holds six anonymous triggers, and
`SafeExchangeFunctionsStartup.IsAuthenticatedHttpTrigger` skips them by exact function name.

**Tech stack:** .NET 10, Azure Functions isolated worker (`HttpRequestData`), `System.Text.Json` (10, for
`JsonDocumentOptions.AllowDuplicateProperties`), `IHttpClientFactory` (`Microsoft.Extensions.Http`, already referenced
by Core), `ModelContextProtocol.Core` 2.4.0 (`ProtectedResourceMetadata`), NUnit 4 + Moq, ARM templates.

**Spec (authoritative):** `docs\superpowers\specs\2026-11-22-mcp-oauth-broker-design.md`.
**Background:** `docs\superpowers\specs\2026-11-21-mcp-endpoint-design.md`, master plan
`docs\superpowers\plans\2026-11-21-mcp-endpoint.md` (style rules) and phase plan
`docs\superpowers\plans\2026-11-21-mcp-p6-mcp-endpoint.md` (how `/api/mcp`, `OAuthProtectedResourceHandler`,
`McpProtectedResource`, the `WWW-Authenticate` challenge, the feature flag and `IsAuthenticatedHttpTrigger` were built).

**Verified baseline:** this plan was applied task by task, exactly as written, in a scratch worktree created from
`feat/mcp-oauth-broker` at **`e833cf6`** (spec commit). Results:
- every red step failed as quoted and every green step passed with the quoted counts (durations vary);
- `dotnet build SafeExchange.slnx --no-incremental` reports **0 errors and 704 warnings before and after** (no new
  warnings);
- the phase filter (ground rule 6) goes from **323 to 515** tests, all green; the **212 contract tests pass
  unchanged**; the existing MCP tests change only where the published `resource` and the skip list require it;
- the full suite passes **1058/1058 before and 1250/1250 after** against the Cosmos emulator
  `mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-EN20260907pre` (HTTP gateway, CI connection string);
- a real Functions host (`func start`, Core Tools) with `Features__McpEndpoint=True` and `Mcp__OAuth__ClientId` set
  mapped all six routes, served them anonymously, redirected `/api/oauth/authorize` to Entra, answered `400` without a
  `Location` for a non-loopback `redirect_uri`, and relayed a real Entra `invalid_grant` from `/api/oauth/token`;
  its console log showed only route/outcome lines (Task 6 Step 9);
- the tasks produce 12 commits (Tasks 1, 2, 3, 4.1–4.4, 5, 6, 7 and 8 were committed in the scratch run; Task 9 adds
  this plan).

---

## Client facts this plan is built against (rmcp, `crates/rmcp/src/transport/auth.rs`)

- Reads `resource_metadata` from `WWW-Authenticate`; the protected-resource document's `resource` must equal the MCP
  URL (or be a same-origin path prefix of it), otherwise the document is rejected (`validate_resource_metadata_resource`).
- For an authorization server with a path (`https://host/api`) it tries, in order:
  `/.well-known/oauth-authorization-server/api`, `/.well-known/openid-configuration/api`,
  `/api/.well-known/openid-configuration`, `/.well-known/oauth-authorization-server`. Only the third is servable under
  the Functions `api` route prefix; this plan also serves `/api/.well-known/oauth-authorization-server` (same JSON)
  for other clients. A non-200 candidate is skipped.
- The metadata `issuer` must equal the expected issuer (`https://host/api`, no trailing slash — the trailing-slash
  tolerance applies only to path-less issuers).
- Refuses metadata whose `code_challenge_methods_supported` lacks `S256`.
- Registers via `registration_endpoint` (RFC 7591 JSON `POST`: `client_name`, `redirect_uris` (one loopback URI),
  `grant_types` `["authorization_code","refresh_token"]`, `token_endpoint_auth_method` `"none"`, `response_types`
  `["code"]`, optional `scope`, `application_type` `"native"`); any 2xx is success and the response needs `client_id`
  and `redirect_uris`.
- Sends `resource=<MCP URL>` on authorize and token requests; redirect URIs are `http://127.0.0.1:<port>/…` or
  `http://localhost:<port>/…`.
- RFC 9207: if the callback carries `iss`, it must equal the metadata issuer; `iss` is *required* only when the
  metadata advertises `authorization_response_iss_parameter_supported`. The broker therefore never advertises that
  field and never forwards Entra's `iss` (or anything but `code`/`state`, `error`/`error_description`/`state`).

## Ground rules

1. **Worktree.** Run every command from the root of a worktree on `feat/mcp-oauth-broker` at `e833cf6` or later
   (for example `C:\Users\yurio\Documents\github\safeexchange-wt-oauth`). Use PowerShell 7. Never touch
   `C:\Users\yurio\Documents\github\safeexchange` (the main tree).
2. **Style** (master plan): file header `/// <summary>\n/// TypeName\n/// </summary>` before the namespace; block
   `namespace X { … }` with `using` directives inside it; `this.` prefix on members; constructor null guards
   `?? throw new ArgumentNullException(nameof(x))`; 4-space indentation; NUnit `Assert.That(..., Is....)`. Comments only
   where a line needs explaining. The test project has **no implicit usings**. New files: UTF-8 without BOM.
   `SafeExchange.Core\SafeExchangeStartup.cs` and `SafeExchange.Core\Configuration\AuthenticationConfiguration.cs`
   start with a BOM — edit them with an editor or the `edit` tool, never with `Set-Content`.
3. **Frozen tests.** `SafeExchange.Tests\Contract\*` is not edited. Existing non-contract tests change only where this
   plan says so: `McpProtectedResourceTests` and `McpMetadataTests` (published `resource`), `McpTestBase` (one options
   binding line), `McpChallengeTests` (skip-list cases), `SafeExchangeFunctionsStartupTests` (registration snapshot +1
   line), `ServiceRegistrationTests` (+1 resolution assertion).
4. **Commits** after every task (Task 4 commits after each of its four cycles), staged by exact paths (never
   `git add -A`), message `feat(mcp-oauth): …` followed by the trailer line
   `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`. The last commit (Task 9) adds this plan file.
5. **File locks.** If a build fails with `MSB3027`/`MSB3021`, a stale MSBuild node or `testhost` holds a `bin` DLL. Set
   `$env:MSBUILDDISABLENODEREUSE='1'` and pass `-nr:false`; never kill processes you did not start.
6. **Commands.**
   - Build: `dotnet build SafeExchange.slnx --nologo -v q`
   - Task filter: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.Mcp.OAuth"`
   - Phase filter (no emulator needed): `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.Mcp|FullyQualifiedName~SafeExchange.Tests.Contract|FullyQualifiedName~Startup|FullyQualifiedName~Middleware|FullyQualifiedName~ServiceRegistration"`
     (the same filter without `ServiceRegistration` gives 2 fewer: 321 → 513).
7. **Red steps** fail at compile time whenever the test references a type or member the task is about to add: the
   expected failure is the quoted compiler error (the first lines are quoted; paths relative to the repository root;
   line/column numbers may differ by a line if a file is formatted differently). Test summaries end with a duration
   that varies (`…`).

## Decisions

- **D1 — pure core, thin adapter.** `McpOAuthBroker` takes strings (query, body) and an API base `Uri` and returns
  `McpOAuthResult(StatusCode, Body, Location)`. All rules are unit-tested without HTTP types; `McpOAuthHandler` only
  maps `HttpRequestData` ↔ result (`Cache-Control: no-store`, `Content-Type: application/json`, `Location`).
- **D2 — "configured" means all three.** Broker routes answer a bare `404` unless `Features.McpEndpoint` is on,
  `Mcp:OAuth:ClientId` is non-blank **and** `Authentication:Authority` yields Entra endpoints (an https URL). The spec
  names the first two; the third is added so the protected-resource metadata can never advertise a broker that cannot
  build Entra URLs. `McpProtectedResource.IsBrokerConfigured` (client ID + authority) is the single predicate; the
  broker adds the flag.
- **D3 — issuer and metadata routes.** Issuer = API base without trailing slash (`https://<host>/api`); endpoints
  `…/api/oauth/{authorize,token,register}`; metadata served at `/api/.well-known/openid-configuration` and
  `/api/.well-known/oauth-authorization-server` (identical JSON, spec field order). `<host>` comes from the request
  URL via the existing `McpHandler.ApiBaseUri`, like the challenge and `file_transfer_info`.
- **D4 — published resource.** `McpProtectedResource.ToMetadata()` becomes `ToMetadata(Uri apiBaseUri)`:
  `resource` = `https://<host>/api/mcp`. `McpProtectedResource.Resource` keeps meaning "the API's `api://` identifier"
  because scopes (`Scopes`, `ApplicationScope`, `file_transfer_info`) are derived from it.
- **D5 — strict parsing.** Query strings and forms: a repeated parameter fails the request; a parameter without a
  value counts as omitted (RFC 6749 §3.1). Registration JSON: duplicate properties are rejected
  (`JsonDocumentOptions.AllowDuplicateProperties = false`). Body limit 16 KB (the adapter reads at most 16 KB + 1
  characters, so a huge body is never buffered); at most 10 redirect URIs; redirect URI ≤ 2048 characters; wrapped
  state ≤ 2048 characters.
- **D6 — loopback check.** A redirect URI must start (case-insensitively) with `http://127.0.0.1`,
  `http://localhost` or `http://[::1]`, followed by end, `:`, `/` or `?`; must contain no whitespace, control
  character, `\` or `#`; must parse as an absolute `http` URI with empty user info and host `127.0.0.1`, `localhost`
  or `[::1]`. The prefix check is what rejects `localhost.evil.com`, `127.0.0.1.nip.io` and `127.0.0.1@evil.com`.
- **D7 — where errors go.** Authorize: unparsable query, unknown/missing `client_id` or a missing/non-loopback
  `redirect_uri` → `400` JSON, never a redirect. After that, `response_type` ≠ `code` → redirect
  `error=unsupported_response_type`; missing/ill-formed `code_challenge` or method ≠ `S256` (exact) or state too long →
  redirect `error=invalid_request` (always with the client's `state`). Callback: no valid wrapped state → `400` JSON;
  Entra `error` → redirect with only `error`, `error_description`, `state`; neither code nor error → redirect
  `error=server_error`. Register: RFC 7591 `invalid_redirect_uri` / `invalid_client_metadata` (`400`). Token: RFC 6749
  `invalid_request` / `invalid_client` / `unsupported_grant_type` (`400`); Entra unreachable or timed out → `502`
  `server_error`.
- **D8 — what is forwarded.** Entra authorize gets exactly `client_id` (configured), `response_type=code`,
  `redirect_uri` (`<base>/api/oauth/callback`), `response_mode=query`, `scope` (API scope(s) + `offline_access`),
  `code_challenge`, `code_challenge_method=S256`, `state` (wrapped), and `prompt` / `login_hint` only when present.
  Entra token gets exactly `grant_type`, `client_id` (configured), then `code` + `code_verifier` + `redirect_uri`
  (callback) or `refresh_token`, then `scope`. A client `client_id` must match if present; `code_verifier` must have
  the RFC 7636 shape. Entra's status and body are relayed unchanged.
- **D9 — registration response.** `201` `{client_id, redirect_uris (echo), grant_types, response_types,
  token_endpoint_auth_method: "none"}`; no secret, no `client_id_issued_at`, `client_name` not echoed.
- **D10 — Entra token client.** Named `HttpClient` `SafeExchange.McpOAuth.EntraToken` registered in
  `AddSafeExchangeMcp()`: 10 s timeout, primary handler `SocketsHttpHandler { AllowAutoRedirect = false }`, URL always
  `EntraEndpoints.Token`. No logging in the client; the default `IHttpClientFactory` logging writes method/URL only
  (verified by a log-capture test).
- **D11 — logging.** Message templates only: route, outcome, grant type, Entra status, and the Entra error code when it
  is `[a-z_]{1,64}` (else `unrecognized`). Never codes, verifiers, challenges, tokens, client state or wrapped state.
- **D12 — skip list.** `McpRoutes.AnonymousFunctionNames` (ordinal `HashSet`) = the protected-resource metadata function
  plus the six broker functions; `IsAuthenticatedHttpTrigger` uses it (affects `DefaultAuthenticationMiddleware` and
  `TokenFilterMiddleware`).
- **D13 — ARM.** Parameter `settings-mcp-oauthClientId` (string, default `""`) in the template and all three
  parameter files (`""` everywhere; the operator sets it after creating the Entra app). The Key Vault secret
  `Mcp--OAuth--ClientId` follows the `Features--McpEndpoint` resource exactly, plus
  `"condition": "[not(empty(parameters('settings-mcp-oauthClientId')))]"` — the template's existing pattern for
  optional values (`AADClient--ClientSecret`) — so an empty parameter neither writes an empty secret nor overwrites a
  value set by hand.

## File structure

| File | Responsibility |
|---|---|
| Create `SafeExchange.Core\Mcp\OAuth\McpOAuthOptions.cs` | `Mcp:OAuth` section (`ClientId`) |
| Create `SafeExchange.Core\Mcp\OAuth\LoopbackRedirectUri.cs` | The loopback rule (D6) |
| Create `SafeExchange.Core\Mcp\OAuth\BrokerState.cs` | Wrapped state codec |
| Create `SafeExchange.Core\Mcp\OAuth\OAuthParameters.cs` | Query/form parsing (D5) |
| Create `SafeExchange.Core\Mcp\OAuth\EntraEndpoints.cs` | Entra v2 authorize/token URLs from `Authority` |
| Create `SafeExchange.Core\Mcp\OAuth\McpOAuthPaths.cs` | Broker paths, issuer and endpoint URLs |
| Create `SafeExchange.Core\Mcp\OAuth\AuthorizationServerMetadata.cs` | RFC 8414 document |
| Create `SafeExchange.Core\Mcp\OAuth\McpOAuthResult.cs` | Host-agnostic result |
| Create `SafeExchange.Core\Mcp\OAuth\IEntraTokenClient.cs` | Token client contract + `EntraTokenResponse` |
| Create `SafeExchange.Core\Mcp\OAuth\McpOAuthBroker.cs` | Metadata, register, authorize, callback, token |
| Create `SafeExchange.Core\Mcp\OAuth\EntraTokenClient.cs` | `IHttpClientFactory` POST to Entra |
| Modify `SafeExchange.Core\Mcp\McpProtectedResource.cs` | `IsBrokerConfigured`, `ToMetadata(Uri)` |
| Modify `SafeExchange.Core\Mcp\McpServiceRegistration.cs` | Register HttpClient, token client, broker |
| Modify `SafeExchange.Core\SafeExchangeStartup.cs` | Bind `Mcp:OAuth` |
| Create `SafeExchange.Core.Functions\Mcp\OAuth\McpOAuthHandler.cs` | Functions adapter |
| Modify `SafeExchange.Core.Functions\Mcp\McpRoutes.cs` | Routes, function names, anonymous set |
| Modify `SafeExchange.Core.Functions\Mcp\OAuthProtectedResourceHandler.cs` | Pass the API base |
| Modify `SafeExchange.Core.Functions\SafeExchangeFunctionsStartup.cs` | Skip list, DI |
| Create `SafeExchange.Functions\Functions\SafeMcpOAuth.cs` | Six anonymous triggers |
| Modify `deployment\current\arm\services-template.arm.json` + 3 parameter files | `settings-mcp-oauthClientId`, `Mcp--OAuth--ClientId` |
| Modify `docs\mcp.md`, `docs\api-endpoints.md`, `docs\consumer-migration.md` | Broker docs, Entra setup |
| Tests: create 17 files under `SafeExchange.Tests\Mcp\OAuth\`; modify the six files of ground rule 3 | |

---

## Task 0: Baseline

**Files:** none.

- [ ] **Step 1: Check the worktree**

Run: `git status --short; git --no-pager log --oneline -1`
Expected: no output from `git status`, and `e833cf6 docs(spec): MCP OAuth broker for Entra ID` (or a later commit
on `feat/mcp-oauth-broker`).

- [ ] **Step 2: Build and record the warning count**

Run: `dotnet build SafeExchange.slnx --nologo -v q --no-incremental`
Expected: ends with `704 Warning(s)` and `0 Error(s)`.

- [ ] **Step 3: Run the phase filter**

Run the phase filter (ground rule 6).
Expected: `Passed!  - Failed:     0, Passed:   323, Skipped:     0, Total:   323, …`
(the contract tests alone: `--filter "FullyQualifiedName~SafeExchange.Tests.Contract"` → `Passed:   212`).

- [ ] **Step 4 (optional): full suite** — see Task 9 Step 2 for the emulator setup. Expected `Passed:  1058`.

---

## Task 1: Options and the loopback redirect rule

**Files:**
- Create: `SafeExchange.Core\Mcp\OAuth\McpOAuthOptions.cs`, `SafeExchange.Core\Mcp\OAuth\LoopbackRedirectUri.cs`
- Modify: `SafeExchange.Core\SafeExchangeStartup.cs` (usings; `services.Configure<…>` block near the end of `ConfigureServices`)
- Test: `SafeExchange.Tests\Mcp\OAuth\LoopbackRedirectUriTests.cs`, `SafeExchange.Tests\Mcp\OAuth\McpOAuthOptionsTests.cs`

- [ ] **Step 1: Write the failing tests**

`SafeExchange.Tests\Mcp\OAuth\LoopbackRedirectUriTests.cs`:

```csharp
/// <summary>
/// LoopbackRedirectUriTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;

    [TestFixture]
    public class LoopbackRedirectUriTests
    {
        [TestCase("http://127.0.0.1:53682/callback")]
        [TestCase("http://127.0.0.1/")]
        [TestCase("http://127.0.0.1")]
        [TestCase("http://localhost:8080/")]
        [TestCase("http://localhost/oauth/callback?client=copilot")]
        [TestCase("http://LOCALHOST:5000/cb")]
        [TestCase("http://[::1]:1234/oauth/callback")]
        [TestCase("http://[::1]/")]
        public void LoopbackHttpUris_OnAnyPortAndPath_AreAllowed(string value)
        {
            Assert.That(LoopbackRedirectUri.IsValid(value), Is.True);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("https://127.0.0.1:8080/callback")]
        [TestCase("https://localhost/callback")]
        [TestCase("http://example.com/callback")]
        [TestCase("http://localhost.evil.com/callback")]
        [TestCase("http://127.0.0.1.nip.io/callback")]
        [TestCase("http://localhost./callback")]
        [TestCase("http://evil.com/http://127.0.0.1/")]
        [TestCase("http://user@localhost:8080/callback")]
        [TestCase("http://user:pass@127.0.0.1/callback")]
        [TestCase("http://localhost:8080@evil.com/")]
        [TestCase("http://127.0.0.1@evil.com/")]
        [TestCase("http://127.0.0.1:8080/callback#fragment")]
        [TestCase("http://127.0.0.1:8080/callback#")]
        [TestCase("/callback")]
        [TestCase("localhost:8080/callback")]
        [TestCase("//localhost:8080/callback")]
        [TestCase("http://127.0.0.2/callback")]
        [TestCase("http://0.0.0.0/callback")]
        [TestCase("http://[::ffff:127.0.0.1]/callback")]
        [TestCase("http://127.0.0.1\\@evil.com/")]
        [TestCase("http://127.0.0.1/call back")]
        [TestCase("http://127.0.0.1/callback\r\nLocation: https://evil.com")]
        [TestCase("javascript://localhost/%0aalert(1)")]
        [TestCase("ftp://127.0.0.1/callback")]
        public void EverythingElse_IsRejected(string? value)
        {
            Assert.That(LoopbackRedirectUri.IsValid(value), Is.False);
        }

        [Test]
        public void TheLengthLimit_Is2048Characters()
        {
            var prefix = "http://127.0.0.1:8080/";
            var longest = prefix + new string('a', LoopbackRedirectUri.MaxLength - prefix.Length);

            Assert.That(LoopbackRedirectUri.IsValid(longest), Is.True);
            Assert.That(LoopbackRedirectUri.IsValid(longest + "a"), Is.False);
        }
    }
}
```

`SafeExchange.Tests\Mcp\OAuth\McpOAuthOptionsTests.cs`:

```csharp
/// <summary>
/// McpOAuthOptionsTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Options;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Mcp.OAuth;
    using System.Collections.Generic;

    [TestFixture]
    public class McpOAuthOptionsTests
    {
        [Test]
        public void ConfigureServices_BindsTheMcpOAuthSection()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "CosmosDb:CosmosDbEndpoint", "https://localhost:8081/" },
                    { "CosmosDb:DatabaseName", "startup-test" },
                    { "Mcp:OAuth:ClientId", "00000000-0000-0000-0000-0000000000c9" },
                })
                .Build();
            var services = new ServiceCollection();

            SafeExchangeStartup.ConfigureServices(configuration, services);

            using var provider = services.BuildServiceProvider();
            Assert.That(provider.GetRequiredService<IOptionsMonitor<McpOAuthOptions>>().CurrentValue.ClientId, Is.EqualTo("00000000-0000-0000-0000-0000000000c9"));
        }
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run the task filter (ground rule 6).
Expected: build fails with
`SafeExchange.Tests\Mcp\OAuth\LoopbackRedirectUriTests.cs(8,33): error CS0234: The type or namespace name 'OAuth' does not exist in the namespace 'SafeExchange.Core.Mcp' (are you missing an assembly reference?)`
(and the same for `McpOAuthOptionsTests.cs(12,33)`).

- [ ] **Step 3: Implement**

`SafeExchange.Core\Mcp\OAuth\McpOAuthOptions.cs`:

```csharp
/// <summary>
/// McpOAuthOptions
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    /// <summary>
    /// Binds to the <c>Mcp:OAuth</c> configuration section (Key Vault <c>Mcp--OAuth--ClientId</c>). The OAuth broker
    /// for MCP clients is configured only when <see cref="ClientId"/> is set.
    /// </summary>
    public sealed class McpOAuthOptions
    {
        public const string SectionName = "Mcp:OAuth";

        /// <summary>
        /// Application (client) ID of the Entra public client app the broker signs users in with.
        /// </summary>
        public string ClientId { get; set; } = string.Empty;
    }
}
```

`SafeExchange.Core\Mcp\OAuth\LoopbackRedirectUri.cs`:

```csharp
/// <summary>
/// LoopbackRedirectUri
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;

    /// <summary>
    /// The only client redirect URIs the OAuth broker accepts: <c>http</c> to <c>127.0.0.1</c>, <c>localhost</c> or
    /// <c>[::1]</c>, any port and path, no user-info, no fragment, at most <see cref="MaxLength"/> characters. A code
    /// can therefore only reach the signing-in user's own machine, so the broker cannot act as an open redirect.
    /// </summary>
    public static class LoopbackRedirectUri
    {
        public const int MaxLength = 2048;

        private static readonly string[] AllowedPrefixes = { "http://127.0.0.1", "http://localhost", "http://[::1]" };

        private static readonly string[] AllowedHosts = { "127.0.0.1", "localhost", "[::1]" };

        public static bool IsValid([NotNullWhen(true)] string? value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
            {
                return false;
            }

            if (value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\' || c == '#'))
            {
                return false;
            }

            // The host must end right after the prefix, so "localhost.evil.com" and "127.0.0.1.nip.io" fail here.
            var prefix = AllowedPrefixes.FirstOrDefault(p => value.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (prefix is null || (value.Length > prefix.Length && value[prefix.Length] is not (':' or '/' or '?')))
            {
                return false;
            }

            return Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttp
                && uri.UserInfo.Length == 0
                && AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
        }
    }
}
```

`SafeExchange.Core\SafeExchangeStartup.cs` (BOM file — use an editor or the `edit` tool). Before:

```csharp
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Blob;
```

After:

```csharp
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Mcp.OAuth;
    using SafeExchange.Core.Blob;
```

and before:

```csharp
            services.Configure<WebClientTelemetryConfiguration>(configuration.GetSection("WebClientTelemetry"));
```

after:

```csharp
            services.Configure<WebClientTelemetryConfiguration>(configuration.GetSection("WebClientTelemetry"));
            services.Configure<McpOAuthOptions>(configuration.GetSection(McpOAuthOptions.SectionName));
```

- [ ] **Step 4: Run the tests to see them pass**

Run the task filter.
Expected: `Passed!  - Failed:     0, Passed:    36, Skipped:     0, Total:    36, …`

- [ ] **Step 5: Commit**

```powershell
git add SafeExchange.Core\Mcp\OAuth\McpOAuthOptions.cs SafeExchange.Core\Mcp\OAuth\LoopbackRedirectUri.cs SafeExchange.Core\SafeExchangeStartup.cs SafeExchange.Tests\Mcp\OAuth\LoopbackRedirectUriTests.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthOptionsTests.cs
git commit -m "feat(mcp-oauth): broker options and loopback redirect URI validation" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 2: Wrapped state codec and OAuth parameter parsing

**Files:**
- Create: `SafeExchange.Core\Mcp\OAuth\BrokerState.cs`, `SafeExchange.Core\Mcp\OAuth\OAuthParameters.cs`
- Test: `SafeExchange.Tests\Mcp\OAuth\BrokerStateTests.cs`, `SafeExchange.Tests\Mcp\OAuth\OAuthParametersTests.cs`

- [ ] **Step 1: Write the failing tests**

`SafeExchange.Tests\Mcp\OAuth\BrokerStateTests.cs` (the 1400/1600-character client states land below/above the
2048-character limit once wrapped; the padded value and `{"r":…,"r":…}` are rejected on purpose):

```csharp
/// <summary>
/// BrokerStateTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Text;

    [TestFixture]
    public class BrokerStateTests
    {
        [TestCase("http://127.0.0.1:53682/callback", "client-state-123")]
        [TestCase("http://localhost:8080/cb?x=1", "with spaces, \"quotes\", ünïcödé & =+/")]
        [TestCase("http://[::1]:1234/", null)]
        public void Encode_ThenDecode_RoundTrips(string redirectUri, string? clientState)
        {
            Assert.That(new BrokerState(redirectUri, clientState).TryEncode(out var encoded), Is.True);

            Assert.That(encoded, Does.Match("^[A-Za-z0-9_-]+$"), "base64url without padding");
            Assert.That(BrokerState.TryDecode(encoded, out var decoded), Is.True);
            Assert.That(decoded, Is.EqualTo(new BrokerState(redirectUri, clientState)));
        }

        [Test]
        public void Encode_RefusesAStateLongerThan2KB()
        {
            var fits = new BrokerState("http://127.0.0.1:1/", new string('s', 1400));
            var tooLong = new BrokerState("http://127.0.0.1:1/", new string('s', 1600));

            Assert.That(fits.TryEncode(out var encoded), Is.True);
            Assert.That(encoded!.Length, Is.LessThanOrEqualTo(BrokerState.MaxEncodedLength));
            Assert.That(tooLong.TryEncode(out var none), Is.False);
            Assert.That(none, Is.Null);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("not base64url!")]
        [TestCase("eyJyIjoiaHR0cDovLzEyNy4wLjAuMToxLyJ9==")]
        public void Decode_RejectsMissingOrNonBase64UrlValues(string? encoded)
        {
            Assert.That(BrokerState.TryDecode(encoded, out var state), Is.False);
            Assert.That(state, Is.Null);
        }

        [TestCase("not json")]
        [TestCase("[]")]
        [TestCase("\"http://127.0.0.1:1/\"")]
        [TestCase("{}")]
        [TestCase("{\"r\":1}")]
        [TestCase("{\"r\":\"http://127.0.0.1:1/\",\"s\":2}")]
        [TestCase("{\"r\":\"http://127.0.0.1:1/\",\"x\":\"extra\"}")]
        [TestCase("{\"r\":\"http://127.0.0.1:1/\",\"r\":\"http://127.0.0.1:2/\"}")]
        [TestCase("{\"r\":\"http://127.0.0.1:1/\"")]
        public void Decode_RejectsMalformedPayloads(string json)
        {
            Assert.That(BrokerState.TryDecode(Encode(json), out _), Is.False);
        }

        [TestCase("https://evil.example/callback")]
        [TestCase("http://localhost.evil.com/callback")]
        [TestCase("http://127.0.0.1.nip.io/callback")]
        [TestCase("http://user@127.0.0.1/callback")]
        public void Decode_RejectsANonLoopbackRedirectInside(string redirectUri)
        {
            var crafted = Encode($"{{\"r\":\"{redirectUri}\",\"s\":\"x\"}}");

            Assert.That(BrokerState.TryDecode(crafted, out _), Is.False);
        }

        [Test]
        public void Decode_RejectsAnOversizeValue_BeforeDecoding()
        {
            var oversize = Encode($"{{\"r\":\"http://127.0.0.1:1/\",\"s\":\"{new string('s', 1600)}\"}}");

            Assert.That(oversize.Length, Is.GreaterThan(BrokerState.MaxEncodedLength));
            Assert.That(BrokerState.TryDecode(oversize, out _), Is.False);
        }

        private static string Encode(string json)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
```

`SafeExchange.Tests\Mcp\OAuth\OAuthParametersTests.cs`:

```csharp
/// <summary>
/// OAuthParametersTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System.Collections.Generic;

    [TestFixture]
    public class OAuthParametersTests
    {
        [TestCase("a=1&b=two+words&c=%2Fpath%3Fx%3D1")]
        [TestCase("?a=1&b=two%20words&c=/path?x=1")]
        public void Parse_DecodesAQueryOrForm(string encoded)
        {
            Assert.That(OAuthParameters.TryParse(encoded, out var parameters), Is.True);

            Assert.That(parameters, Is.EqualTo(new Dictionary<string, string>
            {
                ["a"] = "1",
                ["b"] = "two words",
                ["c"] = "/path?x=1",
            }));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("?")]
        [TestCase("a=&b&&")]
        public void ParametersWithoutAValue_AreTreatedAsOmitted(string? encoded)
        {
            Assert.That(OAuthParameters.TryParse(encoded, out var parameters), Is.True);

            Assert.That(parameters, Is.Empty);
        }

        [TestCase("a=1&a=2")]
        [TestCase("a=1&b=2&a=1")]
        [TestCase("a=1&%61=2")]
        public void ARepeatedParameter_IsRejected(string encoded)
        {
            Assert.That(OAuthParameters.TryParse(encoded, out var parameters), Is.False);

            Assert.That(parameters, Is.Null);
        }
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run the task filter.
Expected: build fails with
`SafeExchange.Tests\Mcp\OAuth\OAuthParametersTests.cs(18,25): error CS0103: The name 'OAuthParameters' does not exist in the current context`
and
`SafeExchange.Tests\Mcp\OAuth\BrokerStateTests.cs(20,29): error CS0246: The type or namespace name 'BrokerState' could not be found (are you missing a using directive or an assembly reference?)`.

- [ ] **Step 3: Implement**

`SafeExchange.Core\Mcp\OAuth\BrokerState.cs`:

```csharp
/// <summary>
/// BrokerState
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;
    using System.Buffers.Text;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Nodes;

    /// <summary>
    /// The <c>state</c> the broker sends to Entra: <c>base64url(JSON{ r: clientRedirect, s: clientState })</c>, at
    /// most <see cref="MaxEncodedLength"/> characters. It carries no authority (the redirect is loopback-only and
    /// re-validated on decode), so it is neither signed nor encrypted.
    /// </summary>
    public sealed record BrokerState(string RedirectUri, string? ClientState)
    {
        public const int MaxEncodedLength = 2048;

        private const string RedirectName = "r";

        private const string ClientStateName = "s";

        public bool TryEncode([NotNullWhen(true)] out string? encoded)
        {
            var payload = new JsonObject { [RedirectName] = this.RedirectUri };
            if (this.ClientState is not null)
            {
                payload[ClientStateName] = this.ClientState;
            }

            var value = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload));
            encoded = value.Length <= MaxEncodedLength ? value : null;
            return encoded is not null;
        }

        public static bool TryDecode(string? encoded, [NotNullWhen(true)] out BrokerState? state)
        {
            state = null;
            if (string.IsNullOrEmpty(encoded) || encoded.Length > MaxEncodedLength || !encoded.All(IsBase64UrlChar))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(encoded));
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                string? redirectUri = null;
                string? clientState = null;
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }

                    if (property.Name == RedirectName && redirectUri is null)
                    {
                        redirectUri = property.Value.GetString();
                    }
                    else if (property.Name == ClientStateName && clientState is null)
                    {
                        clientState = property.Value.GetString();
                    }
                    else
                    {
                        return false;
                    }
                }

                if (!LoopbackRedirectUri.IsValid(redirectUri))
                {
                    return false;
                }

                state = new BrokerState(redirectUri, clientState);
                return true;
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                return false;
            }
        }

        private static bool IsBase64UrlChar(char c)
            => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_';
    }
}
```

`SafeExchange.Core\Mcp\OAuth\OAuthParameters.cs`:

```csharp
/// <summary>
/// OAuthParameters
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;

    /// <summary>
    /// Parses an OAuth query string or <c>application/x-www-form-urlencoded</c> body. Parameters without a value are
    /// treated as omitted and a repeated parameter fails the whole request (RFC 6749 §3.1, §3.2).
    /// </summary>
    public static class OAuthParameters
    {
        public static bool TryParse(string? encoded, [NotNullWhen(true)] out IReadOnlyDictionary<string, string>? parameters)
        {
            var text = encoded ?? string.Empty;
            if (text.StartsWith('?'))
            {
                text = text[1..];
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                var name = Decode(separator < 0 ? pair : pair[..separator]);
                var value = separator < 0 ? string.Empty : Decode(pair[(separator + 1)..]);
                if (value.Length == 0)
                {
                    continue;
                }

                if (!values.TryAdd(name, value))
                {
                    parameters = null;
                    return false;
                }
            }

            parameters = values;
            return true;
        }

        private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
    }
}
```

- [ ] **Step 4: Run the tests to see them pass**

Run the task filter.
Expected: `Passed!  - Failed:     0, Passed:    67, Skipped:     0, Total:    67, …`

- [ ] **Step 5: Commit**

```powershell
git add SafeExchange.Core\Mcp\OAuth\BrokerState.cs SafeExchange.Core\Mcp\OAuth\OAuthParameters.cs SafeExchange.Tests\Mcp\OAuth\BrokerStateTests.cs SafeExchange.Tests\Mcp\OAuth\OAuthParametersTests.cs
git commit -m "feat(mcp-oauth): broker state codec and OAuth parameter parsing" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 3: Authorization-server metadata, Entra endpoints and the published resource

**Files:**
- Create: `SafeExchange.Core\Mcp\OAuth\EntraEndpoints.cs`, `SafeExchange.Core\Mcp\OAuth\McpOAuthPaths.cs`,
  `SafeExchange.Core\Mcp\OAuth\AuthorizationServerMetadata.cs`
- Modify: `SafeExchange.Core\Mcp\McpProtectedResource.cs` (usings, class summary, constructor, `ToMetadata`),
  `SafeExchange.Core.Functions\Mcp\OAuthProtectedResourceHandler.cs` (one line)
- Test (create): `SafeExchange.Tests\Mcp\OAuth\EntraEndpointsTests.cs`, `SafeExchange.Tests\Mcp\OAuth\AuthorizationServerMetadataTests.cs`
- Test (modify): `SafeExchange.Tests\Mcp\McpProtectedResourceTests.cs`, `SafeExchange.Tests\Mcp\McpMetadataTests.cs`,
  `SafeExchange.Tests\Mcp\McpTestBase.cs`

- [ ] **Step 1: Write the new tests**

`SafeExchange.Tests\Mcp\OAuth\EntraEndpointsTests.cs`:

```csharp
/// <summary>
/// EntraEndpointsTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;

    [TestFixture]
    public class EntraEndpointsTests
    {
        private const string Tenant = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001";

        [TestCase(Tenant)]
        [TestCase(Tenant + "/")]
        [TestCase(Tenant + "/v2.0")]
        [TestCase(Tenant + "/V2.0/")]
        [TestCase("  " + Tenant + "/v2.0  ")]
        public void TheV2Endpoints_AreDerivedFromTheAuthority(string authority)
        {
            var endpoints = EntraEndpoints.FromAuthority(authority);

            Assert.That(endpoints, Is.EqualTo(new EntraEndpoints(Tenant + "/oauth2/v2.0/authorize", Tenant + "/oauth2/v2.0/token")));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("login.microsoftonline.com/tenant")]
        [TestCase("http://login.microsoftonline.com/tenant")]
        [TestCase("https://login.microsoftonline.com/tenant?x=1")]
        [TestCase("https://user@login.microsoftonline.com/tenant")]
        public void AnAuthorityThatIsNotAPlainHttpsUrl_GivesNoEndpoints(string? authority)
        {
            Assert.That(EntraEndpoints.FromAuthority(authority), Is.Null);
        }
    }
}
```

`SafeExchange.Tests\Mcp\OAuth\AuthorizationServerMetadataTests.cs`:

```csharp
/// <summary>
/// AuthorizationServerMetadataTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System;

    [TestFixture]
    public class AuthorizationServerMetadataTests
    {
        [Test]
        public void TheDocument_DescribesThisHostAsAPublicClientPkceAuthorizationServer()
        {
            var json = AuthorizationServerMetadata.ToJson(new Uri("https://safeexchange.test/api/"), new[] { "api://safeexchange.test/user_impersonation", "offline_access" });

            Assert.That(json, Is.EqualTo(
                "{\"issuer\":\"https://safeexchange.test/api\","
                + "\"authorization_endpoint\":\"https://safeexchange.test/api/oauth/authorize\","
                + "\"token_endpoint\":\"https://safeexchange.test/api/oauth/token\","
                + "\"registration_endpoint\":\"https://safeexchange.test/api/oauth/register\","
                + "\"response_types_supported\":[\"code\"],"
                + "\"grant_types_supported\":[\"authorization_code\",\"refresh_token\"],"
                + "\"code_challenge_methods_supported\":[\"S256\"],"
                + "\"token_endpoint_auth_methods_supported\":[\"none\"],"
                + "\"scopes_supported\":[\"api://safeexchange.test/user_impersonation\",\"offline_access\"]}"));
        }

        [Test]
        public void TheIssuer_HasNoTrailingSlash_AndEndpointsKeepTheHostAndPort()
        {
            var apiBase = new Uri("https://saex.example:8443/api/");

            Assert.That(McpOAuthPaths.Issuer(apiBase), Is.EqualTo("https://saex.example:8443/api"));
            Assert.That(McpOAuthPaths.Endpoint(apiBase, McpOAuthPaths.Callback), Is.EqualTo("https://saex.example:8443/api/oauth/callback"));
            Assert.That(McpOAuthPaths.Endpoint(apiBase, McpOAuthPaths.Mcp), Is.EqualTo("https://saex.example:8443/api/mcp"));
        }
    }
}
```

- [ ] **Step 2: Change the protected-resource tests to the MCP URL as `resource`**

`SafeExchange.Tests\Mcp\McpProtectedResourceTests.cs` — before:

```csharp
    using SafeExchange.Core.Mcp;

    [TestFixture]
    public class McpProtectedResourceTests
    {
        private const string Authority = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001";
```

after:

```csharp
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Mcp.OAuth;
    using System;

    [TestFixture]
    public class McpProtectedResourceTests
    {
        private const string Authority = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001";

        private const string BrokerClientId = "00000000-0000-0000-0000-0000000000c9";

        private static readonly Uri ApiBase = new("https://safeexchange.test/api/");
```

and replace this block (the last test and the helper):

```csharp
        [Test]
        public void ToMetadata_IsTheRfc9728Document()
        {
            var metadata = Create(new AuthenticationConfiguration { Authority = Authority, ValidAudiences = "api://safeexchange.test" }).ToMetadata();

            Assert.That(metadata.Resource, Is.EqualTo("api://safeexchange.test"));
            Assert.That(metadata.AuthorizationServers, Is.EqualTo(new[] { Authority + "/v2.0" }));
            Assert.That(metadata.ScopesSupported, Is.EqualTo(new[] { "api://safeexchange.test/user_impersonation" }));
            Assert.That(metadata.BearerMethodsSupported, Is.EqualTo(new[] { "header" }));
        }

        private static McpProtectedResource Create(AuthenticationConfiguration configuration)
            => new(Mock.Of<IOptionsMonitor<AuthenticationConfiguration>>(x => x.CurrentValue == configuration));
```

with:

```csharp
        [Test]
        public void ToMetadata_IsTheRfc9728Document_WithTheMcpUrlAsResource()
        {
            var resource = Create(new AuthenticationConfiguration { Authority = Authority, ValidAudiences = "api://safeexchange.test" });

            var metadata = resource.ToMetadata(ApiBase);

            Assert.That(resource.IsBrokerConfigured, Is.False);
            Assert.That(metadata.Resource, Is.EqualTo("https://safeexchange.test/api/mcp"));
            Assert.That(metadata.AuthorizationServers, Is.EqualTo(new[] { Authority + "/v2.0" }));
            Assert.That(metadata.ScopesSupported, Is.EqualTo(new[] { "api://safeexchange.test/user_impersonation" }));
            Assert.That(metadata.BearerMethodsSupported, Is.EqualTo(new[] { "header" }));
        }

        [Test]
        public void ToMetadata_WithTheBrokerConfigured_NamesThisHostAsTheAuthorizationServer()
        {
            var resource = Create(new AuthenticationConfiguration { Authority = Authority, ValidAudiences = "api://safeexchange.test" }, BrokerClientId);

            var metadata = resource.ToMetadata(ApiBase);

            Assert.That(resource.IsBrokerConfigured, Is.True);
            Assert.That(metadata.Resource, Is.EqualTo("https://safeexchange.test/api/mcp"));
            Assert.That(metadata.AuthorizationServers, Is.EqualTo(new[] { "https://safeexchange.test/api" }));
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
```

The resulting file:

```csharp
/// <summary>
/// McpProtectedResourceTests
/// </summary>

namespace SafeExchange.Tests.Mcp
{
    using Microsoft.Extensions.Options;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Mcp.OAuth;
    using System;

    [TestFixture]
    public class McpProtectedResourceTests
    {
        private const string Authority = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001";

        private const string BrokerClientId = "00000000-0000-0000-0000-0000000000c9";

        private static readonly Uri ApiBase = new("https://safeexchange.test/api/");

        [Test]
        public void Defaults_AreDerivedFromTheAuthenticationSection()
        {
            var resource = Create(new AuthenticationConfiguration
            {
                Authority = Authority,
                ValidAudiences = "00000000-0000-0000-0000-0000000000e1, api://safeexchange.test",
            });

            Assert.That(resource.Resource, Is.EqualTo("api://safeexchange.test"));
            Assert.That(resource.AuthorizationServers, Is.EqualTo(new[] { Authority + "/v2.0" }));
            Assert.That(resource.Scopes, Is.EqualTo(new[] { "api://safeexchange.test/user_impersonation" }));
            Assert.That(resource.ApplicationScope, Is.EqualTo("api://safeexchange.test/.default"));
        }

        [Test]
        public void McpScopes_AndAV2Authority_AreTakenAsConfigured()
        {
            var resource = Create(new AuthenticationConfiguration
            {
                Authority = Authority + "/v2.0/",
                ValidAudiences = "api://safeexchange.test/",
                McpScopes = "api://custom/a, ,api://custom/b",
            });

            Assert.That(resource.AuthorizationServers, Is.EqualTo(new[] { Authority + "/v2.0" }));
            Assert.That(resource.Scopes, Is.EqualTo(new[] { "api://custom/a", "api://custom/b" }));
            Assert.That(resource.ApplicationScope, Is.EqualTo("api://safeexchange.test/.default"));
        }

        [Test]
        public void WithoutAnAppIdUri_TheFirstAudienceIsTheResource_AndEmptyConfigurationGivesEmptyLists()
        {
            var guidOnly = Create(new AuthenticationConfiguration { ValidAudiences = "00000000-0000-0000-0000-0000000000e1" });
            var empty = Create(new AuthenticationConfiguration());

            Assert.That(guidOnly.Resource, Is.EqualTo("00000000-0000-0000-0000-0000000000e1"));
            Assert.That(empty.Resource, Is.Empty);
            Assert.That(empty.Scopes, Is.Empty);
            Assert.That(empty.AuthorizationServers, Is.Empty);
            Assert.That(empty.ApplicationScope, Is.Empty);
        }

        [Test]
        public void ToMetadata_IsTheRfc9728Document_WithTheMcpUrlAsResource()
        {
            var resource = Create(new AuthenticationConfiguration { Authority = Authority, ValidAudiences = "api://safeexchange.test" });

            var metadata = resource.ToMetadata(ApiBase);

            Assert.That(resource.IsBrokerConfigured, Is.False);
            Assert.That(metadata.Resource, Is.EqualTo("https://safeexchange.test/api/mcp"));
            Assert.That(metadata.AuthorizationServers, Is.EqualTo(new[] { Authority + "/v2.0" }));
            Assert.That(metadata.ScopesSupported, Is.EqualTo(new[] { "api://safeexchange.test/user_impersonation" }));
            Assert.That(metadata.BearerMethodsSupported, Is.EqualTo(new[] { "header" }));
        }

        [Test]
        public void ToMetadata_WithTheBrokerConfigured_NamesThisHostAsTheAuthorizationServer()
        {
            var resource = Create(new AuthenticationConfiguration { Authority = Authority, ValidAudiences = "api://safeexchange.test" }, BrokerClientId);

            var metadata = resource.ToMetadata(ApiBase);

            Assert.That(resource.IsBrokerConfigured, Is.True);
            Assert.That(metadata.Resource, Is.EqualTo("https://safeexchange.test/api/mcp"));
            Assert.That(metadata.AuthorizationServers, Is.EqualTo(new[] { "https://safeexchange.test/api" }));
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

`SafeExchange.Tests\Mcp\McpMetadataTests.cs` — before:

```csharp
            Assert.That(BodyOf(response), Is.EqualTo(
                "{\"resource\":\"api://safeexchange.test\","
                + $"\"authorization_servers\":[\"https://login.microsoftonline.com/{TenantId}/v2.0\"],"
                + "\"bearer_methods_supported\":[\"header\"],"
                + "\"scopes_supported\":[\"api://safeexchange.test/user_impersonation\"]}"));
        }
```

after:

```csharp
            Assert.That(BodyOf(response), Is.EqualTo(
                "{\"resource\":\"https://safeexchange.test/api/mcp\","
                + $"\"authorization_servers\":[\"https://login.microsoftonline.com/{TenantId}/v2.0\"],"
                + "\"bearer_methods_supported\":[\"header\"],"
                + "\"scopes_supported\":[\"api://safeexchange.test/user_impersonation\"]}"));
        }

        [Test]
        public async Task Metadata_WithTheBrokerConfigured_NamesThisHostAsTheAuthorizationServer()
        {
            this.BuildProvider(("Mcp:OAuth:ClientId", "00000000-0000-0000-0000-0000000000c9"));

            var response = await this.MetadataHandler.Run(MetadataRequest());

            Assert.That(BodyOf(response), Is.EqualTo(
                "{\"resource\":\"https://safeexchange.test/api/mcp\","
                + "\"authorization_servers\":[\"https://safeexchange.test/api\"],"
                + "\"bearer_methods_supported\":[\"header\"],"
                + "\"scopes_supported\":[\"api://safeexchange.test/user_impersonation\"]}"));
        }
```

`SafeExchange.Tests\Mcp\McpTestBase.cs` (mirrors `SafeExchangeStartup`) — before:

```csharp
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Permissions;
```

after:

```csharp
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Mcp.OAuth;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Permissions;
```

and before:

```csharp
            services.Configure<GloballyAllowedGroupsConfiguration>(configuration.GetSection("GlobalAllowLists"));

            services.AddSingleton(this.DbContext);
```

after:

```csharp
            services.Configure<GloballyAllowedGroupsConfiguration>(configuration.GetSection("GlobalAllowLists"));
            services.Configure<McpOAuthOptions>(configuration.GetSection(McpOAuthOptions.SectionName));

            services.AddSingleton(this.DbContext);
```

- [ ] **Step 3: Run the MCP tests to see them fail**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.Mcp"`
Expected: build fails with, among others,
`SafeExchange.Tests\Mcp\OAuth\EntraEndpointsTests.cs(22,29): error CS0103: The name 'EntraEndpoints' does not exist in the current context`,
`SafeExchange.Tests\Mcp\OAuth\AuthorizationServerMetadataTests.cs(17,24): error CS0103: The name 'AuthorizationServerMetadata' does not exist in the current context`,
`SafeExchange.Tests\Mcp\McpProtectedResourceTests.cs(72,37): error CS1501: No overload for method 'ToMetadata' takes 1 arguments` and
`SafeExchange.Tests\Mcp\McpProtectedResourceTests.cs(74,34): error CS1061: 'McpProtectedResource' does not contain a definition for 'IsBrokerConfigured' …`.

- [ ] **Step 4: Implement the new types**

`SafeExchange.Core\Mcp\OAuth\EntraEndpoints.cs`:

```csharp
/// <summary>
/// EntraEndpoints
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;

    /// <summary>
    /// Entra ID v2 authorize and token endpoints derived from <c>Authentication:Authority</c> (trailing <c>/</c> and
    /// <c>/v2.0</c> removed, then <c>/oauth2/v2.0/authorize|token</c> appended). Only a plain https authority
    /// qualifies; nothing a caller sends influences these URLs.
    /// </summary>
    public sealed record EntraEndpoints(string Authorize, string Token)
    {
        private const string V2Suffix = "/v2.0";

        public static EntraEndpoints? FromAuthority(string? authority)
        {
            var value = (authority ?? string.Empty).Trim().TrimEnd('/');
            if (value.EndsWith(V2Suffix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^V2Suffix.Length].TrimEnd('/');
            }

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || uri.UserInfo.Length > 0
                || uri.Query.Length > 0
                || uri.Fragment.Length > 0)
            {
                return null;
            }

            return new EntraEndpoints(value + "/oauth2/v2.0/authorize", value + "/oauth2/v2.0/token");
        }
    }
}
```

`SafeExchange.Core\Mcp\OAuth\McpOAuthPaths.cs`:

```csharp
/// <summary>
/// McpOAuthPaths
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;

    /// <summary>
    /// Paths of the MCP endpoint and the OAuth broker relative to the API base (<c>https://host/api/</c>, which must
    /// end with <c>/</c>). The broker's issuer is the API base without the trailing slash.
    /// </summary>
    public static class McpOAuthPaths
    {
        public const string Mcp = "mcp";

        public const string AuthorizationServerMetadata = ".well-known/oauth-authorization-server";

        public const string OpenIdConfiguration = ".well-known/openid-configuration";

        public const string Register = "oauth/register";

        public const string Authorize = "oauth/authorize";

        public const string Callback = "oauth/callback";

        public const string Token = "oauth/token";

        public static string Issuer(Uri apiBaseUri)
        {
            ArgumentNullException.ThrowIfNull(apiBaseUri);
            return apiBaseUri.AbsoluteUri.TrimEnd('/');
        }

        public static string Endpoint(Uri apiBaseUri, string path)
        {
            ArgumentNullException.ThrowIfNull(apiBaseUri);
            return new Uri(apiBaseUri, path).AbsoluteUri;
        }
    }
}
```

`SafeExchange.Core\Mcp\OAuth\AuthorizationServerMetadata.cs`:

```csharp
/// <summary>
/// AuthorizationServerMetadata
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json.Nodes;

    /// <summary>
    /// RFC 8414 authorization-server metadata of the broker: issuer <c>https://host/api</c>, its four endpoints,
    /// authorization code with S256 PKCE for public clients only.
    /// </summary>
    public static class AuthorizationServerMetadata
    {
        public static string ToJson(Uri apiBaseUri, IEnumerable<string> scopes)
        {
            ArgumentNullException.ThrowIfNull(apiBaseUri);
            ArgumentNullException.ThrowIfNull(scopes);

            return new JsonObject
            {
                ["issuer"] = McpOAuthPaths.Issuer(apiBaseUri),
                ["authorization_endpoint"] = McpOAuthPaths.Endpoint(apiBaseUri, McpOAuthPaths.Authorize),
                ["token_endpoint"] = McpOAuthPaths.Endpoint(apiBaseUri, McpOAuthPaths.Token),
                ["registration_endpoint"] = McpOAuthPaths.Endpoint(apiBaseUri, McpOAuthPaths.Register),
                ["response_types_supported"] = new JsonArray("code"),
                ["grant_types_supported"] = new JsonArray("authorization_code", "refresh_token"),
                ["code_challenge_methods_supported"] = new JsonArray("S256"),
                ["token_endpoint_auth_methods_supported"] = new JsonArray("none"),
                ["scopes_supported"] = new JsonArray(scopes.Select(s => (JsonNode?)s).ToArray()),
            }.ToJsonString();
        }
    }
}
```

- [ ] **Step 5: Publish the MCP URL and the broker in `McpProtectedResource`**

`SafeExchange.Core\Mcp\McpProtectedResource.cs` — before:

```csharp
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
```

after:

```csharp
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
```

and before:

```csharp
        public ProtectedResourceMetadata ToMetadata() => new()
        {
            Resource = this.Resource,
            AuthorizationServers = this.AuthorizationServers.ToList(),
            ScopesSupported = this.Scopes.ToList(),
            BearerMethodsSupported = new List<string> { "header" },
        };
```

after:

```csharp
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
```

`SafeExchange.Core.Functions\Mcp\OAuthProtectedResourceHandler.cs` — before:

```csharp
            await response.WriteStringAsync(JsonSerializer.Serialize(this.protectedResource.ToMetadata(), McpJsonUtilities.DefaultOptions));
```

after:

```csharp
            await response.WriteStringAsync(JsonSerializer.Serialize(this.protectedResource.ToMetadata(McpHandler.ApiBaseUri(request.Url)), McpJsonUtilities.DefaultOptions));
```

(`McpProtectedResource` is a DI singleton; `IOptionsMonitor<McpOAuthOptions>` resolves from the options
infrastructure, bound by Task 1's `services.Configure<McpOAuthOptions>`.)

- [ ] **Step 6: Run the phase filter to see it pass**

Run the phase filter (ground rule 6).
Expected: `Passed!  - Failed:     0, Passed:   409, Skipped:     0, Total:   409, …`
(the task filter alone: `Passed:    80`).

- [ ] **Step 7: Commit**

```powershell
git add SafeExchange.Core\Mcp\OAuth\EntraEndpoints.cs SafeExchange.Core\Mcp\OAuth\McpOAuthPaths.cs SafeExchange.Core\Mcp\OAuth\AuthorizationServerMetadata.cs SafeExchange.Core\Mcp\McpProtectedResource.cs SafeExchange.Core.Functions\Mcp\OAuthProtectedResourceHandler.cs SafeExchange.Tests\Mcp\OAuth\EntraEndpointsTests.cs SafeExchange.Tests\Mcp\OAuth\AuthorizationServerMetadataTests.cs SafeExchange.Tests\Mcp\McpProtectedResourceTests.cs SafeExchange.Tests\Mcp\McpMetadataTests.cs SafeExchange.Tests\Mcp\McpTestBase.cs
git commit -m "feat(mcp-oauth): publish the MCP URL as resource and the broker as authorization server" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 4: The broker (`McpOAuthBroker`)

Four red/green cycles, one commit each. The broker's constructor and test fixture are final from cycle 4.1 on
(`IEntraTokenClient` is defined in 4.1 so the fixture never changes).

### Cycle 4.1: metadata, registration and gating

**Files:**
- Create: `SafeExchange.Core\Mcp\OAuth\McpOAuthResult.cs`, `SafeExchange.Core\Mcp\OAuth\IEntraTokenClient.cs`,
  `SafeExchange.Core\Mcp\OAuth\McpOAuthBroker.cs`
- Test: `SafeExchange.Tests\Mcp\OAuth\FakeEntraTokenClient.cs`, `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTestBase.cs`,
  `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerRegisterTests.cs`, `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerGatingTests.cs`

- [ ] **Step 1: Write the test fixture and the failing tests**

`SafeExchange.Tests\Mcp\OAuth\FakeEntraTokenClient.cs`:

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
    /// Records every form the broker forwards and answers with <see cref="Response"/> (or throws <see cref="Failure"/>).
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

        public Task<EntraTokenResponse> RedeemAsync(IReadOnlyList<KeyValuePair<string, string>> form, CancellationToken cancellationToken = default)
        {
            this.Requests.Add(form.ToDictionary(p => p.Key, p => p.Value));
            if (this.Failure is not null)
            {
                throw this.Failure;
            }

            return Task.FromResult(this.Response);
        }
    }
}
```

`SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTestBase.cs`:

```csharp
/// <summary>
/// McpOAuthBrokerTestBase
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Mcp.OAuth;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json.Nodes;

    /// <summary>
    /// A broker over plain options (MCP on, client ID and Entra authority set), a <see cref="FakeEntraTokenClient"/>
    /// and every log entry captured in <see cref="Logs"/>.
    /// </summary>
    public abstract class McpOAuthBrokerTestBase
    {
        protected const string BrokerClientId = "00000000-0000-0000-0000-0000000000c9";

        protected const string Tenant = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001";

        protected const string ApiScope = "api://safeexchange.test/user_impersonation";

        protected const string ClientRedirect = "http://127.0.0.1:53682/callback";

        protected const string Callback = "https://safeexchange.test/api/oauth/callback";

        // RFC 7636 appendix B.
        protected const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

        protected const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        protected static readonly Uri ApiBase = new("https://safeexchange.test/api/");

        protected McpOAuthBroker Broker { get; private set; } = null!;

        protected FakeEntraTokenClient Entra { get; private set; } = null!;

        protected RecordingLoggerProvider Logs { get; private set; } = null!;

        [SetUp]
        public void BrokerSetUp()
        {
            this.Entra = new FakeEntraTokenClient();
            this.Logs = new RecordingLoggerProvider();
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
        }

        protected static JsonObject Json(string? body) => JsonNode.Parse(body!)!.AsObject();

        protected static string Query(params (string Name, string Value)[] parameters)
            => string.Join('&', parameters.Select(p => $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(p.Value)}"));

        /// <summary>
        /// Splits a redirect URL into the part before <c>?</c> and its decoded parameters.
        /// </summary>
        protected static (string Target, IReadOnlyDictionary<string, string> Parameters) SplitLocation(string? location)
        {
            Assert.That(location, Is.Not.Null);
            var separator = location!.IndexOf('?');
            Assert.That(OAuthParameters.TryParse(location[(separator + 1)..], out var parameters), Is.True);
            return (location[..separator], parameters!);
        }
    }
}
```

`SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerRegisterTests.cs`:

```csharp
/// <summary>
/// McpOAuthBrokerRegisterTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System.Linq;
    using System.Net;

    [TestFixture]
    public class McpOAuthBrokerRegisterTests : McpOAuthBrokerTestBase
    {
        // What rmcp (Copilot CLI) posts.
        private const string RmcpRegistration =
            "{\"client_name\":\"GitHub Copilot CLI\",\"redirect_uris\":[\"http://127.0.0.1:53682/callback\"],"
            + "\"grant_types\":[\"authorization_code\",\"refresh_token\"],\"token_endpoint_auth_method\":\"none\","
            + "\"response_types\":[\"code\"],\"scope\":\"api://safeexchange.test/user_impersonation\",\"application_type\":\"native\"}";

        [Test]
        public void Metadata_AdvertisesTheApiScopePlusOfflineAccess()
        {
            var result = this.Broker.Metadata(ApiBase);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(result.Body, Is.EqualTo(AuthorizationServerMetadata.ToJson(ApiBase, new[] { ApiScope, "offline_access" })));
        }

        [Test]
        public void Register_AnRmcpClient_ReturnsTheConfiguredPublicClient()
        {
            var result = this.Broker.Register(RmcpRegistration);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(result.Location, Is.Null);
            Assert.That(result.Body, Is.EqualTo(
                $"{{\"client_id\":\"{BrokerClientId}\","
                + "\"redirect_uris\":[\"http://127.0.0.1:53682/callback\"],"
                + "\"grant_types\":[\"authorization_code\",\"refresh_token\"],"
                + "\"response_types\":[\"code\"],"
                + "\"token_endpoint_auth_method\":\"none\"}"));
        }

        [Test]
        public void Register_WithOnlyRedirectUris_EchoesThemAll()
        {
            var result = this.Broker.Register("{\"redirect_uris\":[\"http://localhost:8080/cb\",\"http://[::1]:8080/cb\"]}");

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(Json(result.Body)["redirect_uris"]!.ToJsonString(), Is.EqualTo("[\"http://localhost:8080/cb\",\"http://[::1]:8080/cb\"]"));
        }

        [TestCase("{\"redirect_uris\":[\"https://evil.example/callback\"]}")]
        [TestCase("{\"redirect_uris\":[\"http://localhost.evil.com/callback\"]}")]
        [TestCase("{\"redirect_uris\":[\"http://127.0.0.1.nip.io/callback\"]}")]
        [TestCase("{\"redirect_uris\":[\"http://127.0.0.1:1/cb\",\"https://evil.example/cb\"]}")]
        [TestCase("{\"redirect_uris\":[\"http://user@127.0.0.1/cb\"]}")]
        [TestCase("{\"redirect_uris\":[\"http://127.0.0.1/cb#x\"]}")]
        [TestCase("{\"redirect_uris\":[]}")]
        [TestCase("{\"redirect_uris\":\"http://127.0.0.1/cb\"}")]
        [TestCase("{\"redirect_uris\":[1]}")]
        [TestCase("{\"client_name\":\"no redirect\"}")]
        public void Register_WithoutOnlyLoopbackRedirectUris_IsInvalidRedirectUri(string body)
        {
            var result = this.Broker.Register(body);

            AssertError(result, "invalid_redirect_uri");
        }

        [Test]
        public void Register_WithMoreThanTenRedirectUris_IsInvalidRedirectUri()
        {
            var uris = string.Join(',', Enumerable.Range(1, 11).Select(i => $"\"http://127.0.0.1:{i}/cb\""));

            AssertError(this.Broker.Register($"{{\"redirect_uris\":[{uris}]}}"), "invalid_redirect_uri");
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("not json")]
        [TestCase("[\"http://127.0.0.1/cb\"]")]
        [TestCase("{\"redirect_uris\":[\"http://127.0.0.1/cb\"],\"redirect_uris\":[\"https://evil.example/cb\"]}")]
        [TestCase("{\"redirect_uris\":[\"http://127.0.0.1/cb\"],\"token_endpoint_auth_method\":\"client_secret_basic\"}")]
        [TestCase("{\"redirect_uris\":[\"http://127.0.0.1/cb\"],\"grant_types\":[\"authorization_code\",\"client_credentials\"]}")]
        [TestCase("{\"redirect_uris\":[\"http://127.0.0.1/cb\"],\"grant_types\":\"authorization_code\"}")]
        [TestCase("{\"redirect_uris\":[\"http://127.0.0.1/cb\"],\"response_types\":[\"token\"]}")]
        public void Register_WithOtherInvalidMetadata_IsInvalidClientMetadata(string? body)
        {
            AssertError(this.Broker.Register(body), "invalid_client_metadata");
        }

        [Test]
        public void Register_ABodyOver16KB_IsInvalidClientMetadata()
        {
            var body = "{\"redirect_uris\":[\"http://127.0.0.1/cb\"],\"client_name\":\"" + new string('n', McpOAuthBroker.MaxBodyLength) + "\"}";

            AssertError(this.Broker.Register(body), "invalid_client_metadata");
        }

        private static void AssertError(McpOAuthResult result, string error)
        {
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(result.Location, Is.Null);
            Assert.That(Json(result.Body)["error"]!.GetValue<string>(), Is.EqualTo(error));
            Assert.That(Json(result.Body)["error_description"]!.GetValue<string>(), Is.Not.Empty);
        }
    }
}
```

`SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerGatingTests.cs` (cycles 4.2–4.4 add one endpoint each to
`AllEndpointsAsync`):

```csharp
/// <summary>
/// McpOAuthBrokerGatingTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System.Collections.Generic;
    using System.Net;
    using System.Threading.Tasks;

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
            {
                Assert.That(result, Is.EqualTo(McpOAuthResult.NotFound));
            }
        }

        [Test]
        public void TheBroker_IsEnabled_WithMcpOnAClientIdAndAnAuthority()
        {
            Assert.That(this.Broker.IsEnabled, Is.True);
        }

        private static Task<List<McpOAuthResult>> AllEndpointsAsync(McpOAuthBroker broker)
            => Task.FromResult(new List<McpOAuthResult>
            {
                broker.Metadata(ApiBase),
                broker.Register("{\"redirect_uris\":[\"http://127.0.0.1:1/cb\"]}"),
            });
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run the task filter.
Expected: build fails with
`SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTestBase.cs(43,19): error CS0246: The type or namespace name 'McpOAuthBroker' could not be found …`,
`SafeExchange.Tests\Mcp\OAuth\FakeEntraTokenClient.cs(18,48): error CS0246: The type or namespace name 'IEntraTokenClient' could not be found …` and
`SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerRegisterTests.cs(101,41): error CS0246: The type or namespace name 'McpOAuthResult' could not be found …`.

- [ ] **Step 3: Implement**

`SafeExchange.Core\Mcp\OAuth\McpOAuthResult.cs`:

```csharp
/// <summary>
/// McpOAuthResult
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System.Net;
    using System.Text.Json.Nodes;

    /// <summary>
    /// A host-agnostic broker response: a status, an optional JSON body and an optional redirect <see cref="Location"/>.
    /// </summary>
    public sealed record McpOAuthResult(HttpStatusCode StatusCode, string? Body = null, string? Location = null)
    {
        public static McpOAuthResult NotFound { get; } = new(HttpStatusCode.NotFound);

        public static McpOAuthResult Json(HttpStatusCode statusCode, string body) => new(statusCode, body);

        public static McpOAuthResult Redirect(string location) => new(HttpStatusCode.Found, null, location);

        public static McpOAuthResult Error(HttpStatusCode statusCode, string error, string description)
            => Json(statusCode, new JsonObject { ["error"] = error, ["error_description"] = description }.ToJsonString());
    }
}
```

`SafeExchange.Core\Mcp\OAuth\IEntraTokenClient.cs`:

```csharp
/// <summary>
/// IEntraTokenClient
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System.Collections.Generic;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Posts a token request form to Entra's fixed token endpoint and returns Entra's status and body unchanged.
    /// </summary>
    public interface IEntraTokenClient
    {
        Task<EntraTokenResponse> RedeemAsync(IReadOnlyList<KeyValuePair<string, string>> form, CancellationToken cancellationToken = default);
    }

    public sealed record EntraTokenResponse(HttpStatusCode StatusCode, string Body);
}
```

`SafeExchange.Core\Mcp\OAuth\McpOAuthBroker.cs` (`tokenClient` and `authentication` are used from cycles 4.2 and
4.4 on):

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
    using System.Linq;
    using System.Net;
    using System.Text.Json;
    using System.Text.Json.Nodes;

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
    }
}
```

- [ ] **Step 4: Run the tests to see them pass**

Run the task filter.
Expected: `Passed!  - Failed:     0, Passed:   109, Skipped:     0, Total:   109, …`

- [ ] **Step 5: Commit**

```powershell
git add SafeExchange.Core\Mcp\OAuth\McpOAuthResult.cs SafeExchange.Core\Mcp\OAuth\IEntraTokenClient.cs SafeExchange.Core\Mcp\OAuth\McpOAuthBroker.cs SafeExchange.Tests\Mcp\OAuth\FakeEntraTokenClient.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTestBase.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerRegisterTests.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerGatingTests.cs
git commit -m "feat(mcp-oauth): broker metadata and loopback-only client registration" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Cycle 4.2: authorize

**Files:**
- Modify: `SafeExchange.Core\Mcp\OAuth\McpOAuthBroker.cs`
- Test: create `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerAuthorizeTests.cs`; modify `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerGatingTests.cs`

- [ ] **Step 1: Write the failing tests**

`SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerAuthorizeTests.cs` (open-redirect attempts, client mismatch without
redirect, missing/plain/lower-case PKCE, dropped `resource`, replaced `scope`):

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
        private const string ClientState = "rmcp-csrf-token-123";

        [Test]
        public void Authorize_RedirectsToEntra_WithTheBrokersClientCallbackScopeAndWrappedState()
        {
            var result = this.Broker.Authorize(ApiBase, ValidQuery());

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(result.Body, Is.Null);
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
            Assert.That(BrokerState.TryDecode(parameters["state"], out var state), Is.True);
            Assert.That(state, Is.EqualTo(new BrokerState(ClientRedirect, ClientState)));
        }

        [Test]
        public void Authorize_DropsResourceAndUnknownParameters_AndReplacesTheRequestedScope()
        {
            var result = this.Broker.Authorize(ApiBase, ValidQuery(
                ("resource", "https://safeexchange.test/api/mcp"),
                ("scope", "openid profile https://graph.microsoft.com/Mail.Read api://other/.default"),
                ("nonce", "n-0S6_WzA2Mj"),
                ("redirect_to", "https://evil.example/")));

            var (_, parameters) = SplitLocation(result.Location);
            Assert.That(parameters.Keys, Does.Not.Contain("resource").And.Not.Contain("nonce").And.Not.Contain("redirect_to"));
            Assert.That(parameters["scope"], Is.EqualTo(ApiScope + " offline_access"));
        }

        [Test]
        public void Authorize_PassesPromptAndLoginHint_OnlyWhenPresent()
        {
            var withHints = SplitLocation(this.Broker.Authorize(ApiBase, ValidQuery(("prompt", "select_account"), ("login_hint", "user@contoso.test"))).Location).Parameters;
            var without = SplitLocation(this.Broker.Authorize(ApiBase, ValidQuery()).Location).Parameters;

            Assert.That(withHints["prompt"], Is.EqualTo("select_account"));
            Assert.That(withHints["login_hint"], Is.EqualTo("user@contoso.test"));
            Assert.That(without.Keys, Does.Not.Contain("prompt").And.Not.Contain("login_hint"));
        }

        [Test]
        public void Authorize_WithoutClientState_WrapsOnlyTheRedirect()
        {
            var query = Query(("client_id", BrokerClientId), ("redirect_uri", ClientRedirect), ("response_type", "code"), ("code_challenge", Challenge), ("code_challenge_method", "S256"));

            var (_, parameters) = SplitLocation(this.Broker.Authorize(ApiBase, query).Location);

            Assert.That(BrokerState.TryDecode(parameters["state"], out var state), Is.True);
            Assert.That(state, Is.EqualTo(new BrokerState(ClientRedirect, null)));
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

        private static string ValidQuery(params (string Name, string Value)[] extra)
            => Query(new[]
            {
                ("client_id", BrokerClientId),
                ("redirect_uri", ClientRedirect),
                ("response_type", "code"),
                ("code_challenge", Challenge),
                ("code_challenge_method", "S256"),
                ("state", ClientState),
            }.Concat(extra).ToArray());

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

`SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerGatingTests.cs` — before:

```csharp
                broker.Register("{\"redirect_uris\":[\"http://127.0.0.1:1/cb\"]}"),
            });
```

after:

```csharp
                broker.Register("{\"redirect_uris\":[\"http://127.0.0.1:1/cb\"]}"),
                broker.Authorize(ApiBase, $"client_id={BrokerClientId}&redirect_uri=http%3A%2F%2F127.0.0.1%3A1%2Fcb&response_type=code&code_challenge={Challenge}&code_challenge_method=S256"),
            });
```

- [ ] **Step 2: Run them to see them fail**

Run the task filter.
Expected: build fails with
`SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerAuthorizeTests.cs(21,38): error CS1061: 'McpOAuthBroker' does not contain a definition for 'Authorize' …`
(and `McpOAuthBrokerGatingTests.cs(46,24)`).

- [ ] **Step 3: Implement**

In `SafeExchange.Core\Mcp\OAuth\McpOAuthBroker.cs`, insert immediately **before** the line
`        private string ClientId => this.oauth.CurrentValue.ClientId.Trim();`:

```csharp
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

```

and append these members at the end of the class, right **after** the `IsAbsentOrSubset` method (before the two
closing braces of the file):

```csharp

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
```

(`AppendQuery` drops parameters whose value is `null`, which is how `prompt`, `login_hint` and a missing client
`state` disappear.)

- [ ] **Step 4: Run the tests to see them pass**

Run the task filter.
Expected: `Passed!  - Failed:     0, Passed:   136, Skipped:     0, Total:   136, …`

- [ ] **Step 5: Commit**

```powershell
git add SafeExchange.Core\Mcp\OAuth\McpOAuthBroker.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerAuthorizeTests.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerGatingTests.cs
git commit -m "feat(mcp-oauth): authorize redirects to Entra with S256 PKCE and wrapped state" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Cycle 4.3: callback

**Files:**
- Modify: `SafeExchange.Core\Mcp\OAuth\McpOAuthBroker.cs`
- Test: create `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerCallbackTests.cs`; modify `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerGatingTests.cs`

- [ ] **Step 1: Write the failing tests**

`SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerCallbackTests.cs` (`iss`, `session_state`, `error_uri` and
`correlation_id` from Entra are not forwarded; a crafted non-loopback state never redirects; the two literal states
are `{"r":"https://evil.example/cb"}` and, in the gating test, `{"r":"http://127.0.0.1:1/cb"}` in base64url):

```csharp
/// <summary>
/// McpOAuthBrokerCallbackTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Net;
    using System.Text;

    [TestFixture]
    public class McpOAuthBrokerCallbackTests : McpOAuthBrokerTestBase
    {
        private const string Code = "1.AQEA-fake-entra-authorization-code";

        [Test]
        public void Callback_ForwardsTheCodeAndTheClientsState_ToTheLoopbackRedirect()
        {
            var query = Query(("code", Code), ("state", Wrap(ClientRedirect, "csrf-1")), ("session_state", "00000000-0000-0000-0000-0000000000aa"), ("iss", "https://login.microsoftonline.com/x/v2.0"));

            var result = this.Broker.Callback(query);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(result.Body, Is.Null);
            Assert.That(result.Location, Is.EqualTo($"{ClientRedirect}?code={Uri.EscapeDataString(Code)}&state=csrf-1"));
        }

        [Test]
        public void Callback_AppendsToARedirectThatHasAQuery_AndOmitsAMissingClientState()
        {
            var result = this.Broker.Callback(Query(("code", Code), ("state", Wrap("http://localhost:8080/cb?session=7", null))));

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
                ("state", Wrap(ClientRedirect, "csrf-2")));

            var result = this.Broker.Callback(query);

            var (target, parameters) = SplitLocation(result.Location);
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(result.Body, Is.Null);
            Assert.That(target, Is.EqualTo(ClientRedirect));
            Assert.That(parameters.Keys, Is.EquivalentTo(new[] { "error", "error_description", "state" }));
            Assert.That(parameters["error"], Is.EqualTo("access_denied"));
            Assert.That(parameters["error_description"], Is.EqualTo("AADSTS65004: User declined to consent."));
            Assert.That(parameters["state"], Is.EqualTo("csrf-2"));
        }

        [Test]
        public void Callback_WithNeitherCodeNorError_RedirectsServerErrorToTheClient()
        {
            var (target, parameters) = SplitLocation(this.Broker.Callback(Query(("state", Wrap(ClientRedirect, "csrf-3")))).Location);

            Assert.That(target, Is.EqualTo(ClientRedirect));
            Assert.That(parameters["error"], Is.EqualTo("server_error"));
            Assert.That(parameters["state"], Is.EqualTo("csrf-3"));
        }

        [TestCase(null)]
        [TestCase("not-a-state")]
        [TestCase("eyJyIjoiaHR0cHM6Ly9ldmlsLmV4YW1wbGUvY2IifQ")]
        public void Callback_WithoutAValidState_AnswersBadRequest_WithoutRedirecting(string? state)
        {
            var query = state is null ? Query(("code", Code)) : Query(("code", Code), ("state", state));

            var result = this.Broker.Callback(query);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(result.Location, Is.Null);
            Assert.That(Json(result.Body)["error"]!.GetValue<string>(), Is.EqualTo("invalid_request"));
        }

        [TestCase("https://evil.example/callback")]
        [TestCase("http://localhost.evil.com/callback")]
        [TestCase("http://127.0.0.1.nip.io/callback")]
        public void Callback_WithACraftedNonLoopbackState_NeverRedirects(string redirectUri)
        {
            var crafted = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"r\":\"{redirectUri}\",\"s\":\"x\"}}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            var result = this.Broker.Callback(Query(("code", Code), ("state", crafted)));

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(result.Location, Is.Null);
        }

        [Test]
        public void Callback_WithARepeatedState_AnswersBadRequest_WithoutRedirecting()
        {
            var query = Query(("code", Code), ("state", Wrap(ClientRedirect, "a")), ("state", Wrap("http://localhost:1/other", "b")));

            var result = this.Broker.Callback(query);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(result.Location, Is.Null);
        }

        private static string Wrap(string redirectUri, string? clientState)
        {
            Assert.That(new BrokerState(redirectUri, clientState).TryEncode(out var encoded), Is.True);
            return encoded!;
        }
    }
}
```

`SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerGatingTests.cs` — before:

```csharp
                broker.Authorize(ApiBase, $"client_id={BrokerClientId}&redirect_uri=http%3A%2F%2F127.0.0.1%3A1%2Fcb&response_type=code&code_challenge={Challenge}&code_challenge_method=S256"),
            });
```

after:

```csharp
                broker.Authorize(ApiBase, $"client_id={BrokerClientId}&redirect_uri=http%3A%2F%2F127.0.0.1%3A1%2Fcb&response_type=code&code_challenge={Challenge}&code_challenge_method=S256"),
                broker.Callback("code=c&state=eyJyIjoiaHR0cDovLzEyNy4wLjAuMToxL2NiIn0"),
            });
```

- [ ] **Step 2: Run them to see them fail**

Run the task filter.
Expected: build fails with
`SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerCallbackTests.cs(23,38): error CS1061: 'McpOAuthBroker' does not contain a definition for 'Callback' …`
(and `McpOAuthBrokerGatingTests.cs(47,24)`).

- [ ] **Step 3: Implement**

In `SafeExchange.Core\Mcp\OAuth\McpOAuthBroker.cs`, insert immediately **before** the line
`        private string ClientId => this.oauth.CurrentValue.ClientId.Trim();`:

```csharp
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

```

and append at the end of the class, right **after** the `AppendQuery` method:

```csharp

        // Entra error codes are short snake_case words; anything else is not echoed into logs.
        private static string LogSafeErrorCode(string? error)
            => error is { Length: > 0 and <= 64 } && error.All(c => char.IsAsciiLetterLower(c) || c == '_') ? error : "unrecognized";
```

- [ ] **Step 4: Run the tests to see them pass**

Run the task filter.
Expected: `Passed!  - Failed:     0, Passed:   147, Skipped:     0, Total:   147, …`

- [ ] **Step 5: Commit**

```powershell
git add SafeExchange.Core\Mcp\OAuth\McpOAuthBroker.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerCallbackTests.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerGatingTests.cs
git commit -m "feat(mcp-oauth): callback forwards the code or error to the loopback redirect only" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Cycle 4.4: token

**Files:**
- Modify: `SafeExchange.Core\Mcp\OAuth\McpOAuthBroker.cs`
- Test: create `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTokenTests.cs`; modify `SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerGatingTests.cs`

- [ ] **Step 1: Write the failing tests**

`SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTokenTests.cs` (the forwarded form is compared as a whole, so dropped
`resource` / `client_secret`, the replaced `scope` and the broker's `redirect_uri` are all pinned):

```csharp
/// <summary>
/// McpOAuthBrokerTokenTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Threading.Tasks;

    [TestFixture]
    public class McpOAuthBrokerTokenTests : McpOAuthBrokerTestBase
    {
        private const string Code = "1.AQEA-fake-entra-authorization-code";

        [Test]
        public async Task AuthorizationCode_IsForwardedWithTheBrokersClientCallbackAndScope_AndNothingElse()
        {
            var body = Query(
                ("grant_type", "authorization_code"),
                ("code", Code),
                ("code_verifier", Verifier),
                ("redirect_uri", ClientRedirect),
                ("client_id", BrokerClientId),
                ("resource", "https://safeexchange.test/api/mcp"),
                ("client_secret", "should-never-reach-entra"),
                ("scope", "openid https://graph.microsoft.com/.default"));

            var result = await this.Broker.TokenAsync(ApiBase, body);

            Assert.That(this.Entra.Requests, Is.EqualTo(new[]
            {
                new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = BrokerClientId,
                    ["code"] = Code,
                    ["code_verifier"] = Verifier,
                    ["redirect_uri"] = Callback,
                    ["scope"] = ApiScope + " offline_access",
                },
            }));
            Assert.That(result, Is.EqualTo(McpOAuthResult.Json(HttpStatusCode.OK, FakeEntraTokenClient.SuccessBody)));
        }

        [Test]
        public async Task RefreshToken_IsForwardedWithTheBrokersClientAndScope_AndNothingElse()
        {
            var body = Query(("grant_type", "refresh_token"), ("refresh_token", FakeEntraTokenClient.RefreshToken), ("resource", "https://safeexchange.test/api/mcp"), ("client_secret", "x"));

            var result = await this.Broker.TokenAsync(ApiBase, body);

            Assert.That(this.Entra.Requests, Is.EqualTo(new[]
            {
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = BrokerClientId,
                    ["refresh_token"] = FakeEntraTokenClient.RefreshToken,
                    ["scope"] = ApiScope + " offline_access",
                },
            }));
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public async Task AnEntraError_IsRelayedUnchanged()
        {
            const string EntraError = "{\"error\":\"invalid_grant\",\"error_description\":\"AADSTS70008: The provided authorization code or refresh token has expired.\",\"error_codes\":[70008]}";
            this.Entra.Response = new EntraTokenResponse(HttpStatusCode.BadRequest, EntraError);

            var result = await this.Broker.TokenAsync(ApiBase, Query(("grant_type", "refresh_token"), ("refresh_token", FakeEntraTokenClient.RefreshToken)));

            Assert.That(result, Is.EqualTo(McpOAuthResult.Json(HttpStatusCode.BadRequest, EntraError)));
        }

        [TestCase("grant_type=client_credentials&scope=x", "unsupported_grant_type")]
        [TestCase("grant_type=password&username=u&password=p", "unsupported_grant_type")]
        [TestCase("code=c&code_verifier=" + Verifier, "unsupported_grant_type")]
        [TestCase("grant_type=authorization_code&code_verifier=" + Verifier, "invalid_request")]
        [TestCase("grant_type=authorization_code&code=c", "invalid_request")]
        [TestCase("grant_type=authorization_code&code=c&code_verifier=too-short", "invalid_request")]
        [TestCase("grant_type=refresh_token", "invalid_request")]
        [TestCase("grant_type=refresh_token&refresh_token=a&refresh_token=b", "invalid_request")]
        [TestCase("grant_type=refresh_token&refresh_token=a&client_id=00000000-0000-0000-0000-0000000000ff", "invalid_client")]
        [TestCase("", "invalid_request")]
        public async Task AnInvalidRequest_IsRefused_WithoutCallingEntra(string body, string error)
        {
            var result = await this.Broker.TokenAsync(ApiBase, body);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(Json(result.Body)["error"]!.GetValue<string>(), Is.EqualTo(error));
            Assert.That(this.Entra.Requests, Is.Empty);
        }

        [Test]
        public async Task ABodyOver16KB_IsRefused_WithoutCallingEntra()
        {
            var body = Query(("grant_type", "refresh_token"), ("refresh_token", new string('r', McpOAuthBroker.MaxBodyLength)));

            var result = await this.Broker.TokenAsync(ApiBase, body);

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(this.Entra.Requests, Is.Empty);
        }

        [Test]
        public async Task AnUnreachableEntra_IsABadGateway()
        {
            this.Entra.Failure = new HttpRequestException("No such host is known. (login.microsoftonline.com:443)");

            var result = await this.Broker.TokenAsync(ApiBase, Query(("grant_type", "refresh_token"), ("refresh_token", FakeEntraTokenClient.RefreshToken)));

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(Json(result.Body)["error"]!.GetValue<string>(), Is.EqualTo("server_error"));
        }

        [Test]
        public async Task AnEntraTimeout_IsABadGateway()
        {
            this.Entra.Failure = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.");

            var result = await this.Broker.TokenAsync(ApiBase, Query(("grant_type", "refresh_token"), ("refresh_token", FakeEntraTokenClient.RefreshToken)));

            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
        }
    }
}
```

`SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerGatingTests.cs` — before:

```csharp
                Assert.That(result, Is.EqualTo(McpOAuthResult.NotFound));
            }
        }
```

after:

```csharp
                Assert.That(result, Is.EqualTo(McpOAuthResult.NotFound));
            }

            Assert.That(this.Entra.Requests, Is.Empty);
        }
```

and before:

```csharp
        private static Task<List<McpOAuthResult>> AllEndpointsAsync(McpOAuthBroker broker)
            => Task.FromResult(new List<McpOAuthResult>
            {
                broker.Metadata(ApiBase),
                broker.Register("{\"redirect_uris\":[\"http://127.0.0.1:1/cb\"]}"),
                broker.Authorize(ApiBase, $"client_id={BrokerClientId}&redirect_uri=http%3A%2F%2F127.0.0.1%3A1%2Fcb&response_type=code&code_challenge={Challenge}&code_challenge_method=S256"),
                broker.Callback("code=c&state=eyJyIjoiaHR0cDovLzEyNy4wLjAuMToxL2NiIn0"),
            });
```

after:

```csharp
        private static async Task<List<McpOAuthResult>> AllEndpointsAsync(McpOAuthBroker broker)
            => new List<McpOAuthResult>
            {
                broker.Metadata(ApiBase),
                broker.Register("{\"redirect_uris\":[\"http://127.0.0.1:1/cb\"]}"),
                broker.Authorize(ApiBase, $"client_id={BrokerClientId}&redirect_uri=http%3A%2F%2F127.0.0.1%3A1%2Fcb&response_type=code&code_challenge={Challenge}&code_challenge_method=S256"),
                broker.Callback("code=c&state=eyJyIjoiaHR0cDovLzEyNy4wLjAuMToxL2NiIn0"),
                await broker.TokenAsync(ApiBase, "grant_type=refresh_token&refresh_token=r"),
            };
```

The resulting gating test file:

```csharp
/// <summary>
/// McpOAuthBrokerGatingTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System.Collections.Generic;
    using System.Net;
    using System.Threading.Tasks;

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
            {
                Assert.That(result, Is.EqualTo(McpOAuthResult.NotFound));
            }

            Assert.That(this.Entra.Requests, Is.Empty);
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
                broker.Callback("code=c&state=eyJyIjoiaHR0cDovLzEyNy4wLjAuMToxL2NiIn0"),
                await broker.TokenAsync(ApiBase, "grant_type=refresh_token&refresh_token=r"),
            };
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run the task filter.
Expected: build fails with
`SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTokenTests.cs(32,44): error CS1061: 'McpOAuthBroker' does not contain a definition for 'TokenAsync' …`.

- [ ] **Step 3: Implement**

In `SafeExchange.Core\Mcp\OAuth\McpOAuthBroker.cs`, replace the usings — before:

```csharp
    using System.Net;
    using System.Text.Json;
    using System.Text.Json.Nodes;
```

after:

```csharp
    using System.Net;
    using System.Net.Http;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;
```

insert immediately **before** the line `        private string ClientId => this.oauth.CurrentValue.ClientId.Trim();`:

```csharp
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

```

and append at the end of the class, right **after** `LogSafeErrorCode`:

```csharp

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

The complete broker after this cycle (for checking):

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
```

- [ ] **Step 4: Run the tests to see them pass**

Run the task filter.
Expected: `Passed!  - Failed:     0, Passed:   163, Skipped:     0, Total:   163, …`

- [ ] **Step 5: Commit**

```powershell
git add SafeExchange.Core\Mcp\OAuth\McpOAuthBroker.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerTokenTests.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthBrokerGatingTests.cs
git commit -m "feat(mcp-oauth): token requests are rewritten and forwarded to Entra" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 5: Entra token client and DI registration

**Files:**
- Create: `SafeExchange.Core\Mcp\OAuth\EntraTokenClient.cs`
- Modify: `SafeExchange.Core\Mcp\McpServiceRegistration.cs`
- Test: `SafeExchange.Tests\Mcp\OAuth\EntraTokenClientTests.cs`

- [ ] **Step 1: Write the failing tests**

`SafeExchange.Tests\Mcp\OAuth\EntraTokenClientTests.cs` (fixed endpoint, form encoding, Entra status/body returned;
registration: singleton broker, 10 s timeout, innermost handler does not follow redirects; a log-capture test over
the real `IHttpClientFactory` pipeline at `Trace` shows no form value or response body):

```csharp
/// <summary>
/// EntraTokenClientTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Mcp.OAuth;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;

    [TestFixture]
    public class EntraTokenClientTests
    {
        private const string Tenant = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001";

        private const string Code = "1.AQEA-secret-code-for-the-client-log-test";

        private const string Verifier = "verifier-for-the-client-log-test-0123456789abcdef";

        [Test]
        public async Task Redeem_PostsTheFormToTheFixedTokenEndpoint_AndReturnsEntrasAnswer()
        {
            var handler = new StubHandler(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}");
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(EntraTokenClient.HttpClientName)).Returns(() => new HttpClient(handler));
            var authentication = Mock.Of<IOptionsMonitor<AuthenticationConfiguration>>(x => x.CurrentValue == new AuthenticationConfiguration { Authority = Tenant + "/v2.0" });
            var client = new EntraTokenClient(factory.Object, authentication);

            var response = await client.RedeemAsync(new KeyValuePair<string, string>[] { new("grant_type", "refresh_token"), new("refresh_token", "r t+/=") });

            Assert.That(response, Is.EqualTo(new EntraTokenResponse(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}")));
            Assert.That(handler.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(handler.RequestUri, Is.EqualTo(new Uri(Tenant + "/oauth2/v2.0/token")));
            Assert.That(handler.ContentType, Is.EqualTo("application/x-www-form-urlencoded"));
            Assert.That(handler.Body, Is.EqualTo("grant_type=refresh_token&refresh_token=r+t%2B%2F%3D"));
        }

        [Test]
        public void AddSafeExchangeMcp_RegistersTheBroker_AndATenSecondEntraClientThatDoesNotFollowRedirects()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSafeExchangeMcp();
            using var provider = services.BuildServiceProvider();

            Assert.That(provider.GetRequiredService<IEntraTokenClient>(), Is.InstanceOf<EntraTokenClient>());
            Assert.That(provider.GetRequiredService<McpOAuthBroker>(), Is.SameAs(provider.GetRequiredService<McpOAuthBroker>()));
            Assert.That(provider.GetRequiredService<IHttpClientFactory>().CreateClient(EntraTokenClient.HttpClientName).Timeout, Is.EqualTo(TimeSpan.FromSeconds(10)));
            var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(EntraTokenClient.HttpClientName);
            while (handler is DelegatingHandler delegating)
            {
                handler = delegating.InnerHandler!;
            }

            Assert.That(handler, Is.InstanceOf<SocketsHttpHandler>());
            Assert.That(((SocketsHttpHandler)handler).AllowAutoRedirect, Is.False);
        }

        [Test]
        public async Task TheEntraCall_LogsNoFormValueOrResponseBody()
        {
            var logs = new RecordingLoggerProvider();
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
            services.Configure<AuthenticationConfiguration>(c => c.Authority = Tenant);
            services.AddSafeExchangeMcp();
            services.AddHttpClient(EntraTokenClient.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(HttpStatusCode.OK, $"{{\"access_token\":\"{FakeEntraTokenClient.AccessToken}\"}}"));
            using var provider = services.BuildServiceProvider();

            await provider.GetRequiredService<IEntraTokenClient>().RedeemAsync(new KeyValuePair<string, string>[]
            {
                new("grant_type", "authorization_code"),
                new("code", Code),
                new("code_verifier", Verifier),
            });

            var text = string.Join('\n', logs.Entries);
            Assert.That(text, Does.Contain("/oauth2/v2.0/token"), "Arrange: the HttpClient logged the call.");
            foreach (var forbidden in new[] { Code, Verifier, FakeEntraTokenClient.AccessToken })
            {
                Assert.That(text, Does.Not.Contain(forbidden), $"'{forbidden}' was logged.");
            }
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode statusCode;

            private readonly string responseBody;

            public StubHandler(HttpStatusCode statusCode, string responseBody)
            {
                this.statusCode = statusCode;
                this.responseBody = responseBody;
            }

            public HttpMethod? Method { get; private set; }

            public Uri? RequestUri { get; private set; }

            public string? ContentType { get; private set; }

            public string? Body { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                this.Method = request.Method;
                this.RequestUri = request.RequestUri;
                this.ContentType = request.Content?.Headers.ContentType?.MediaType;
                this.Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(this.statusCode) { Content = new StringContent(this.responseBody) };
            }
        }
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run the task filter.
Expected: build fails with
`SafeExchange.Tests\Mcp\OAuth\EntraTokenClientTests.cs(37,47): error CS0103: The name 'EntraTokenClient' does not exist in the current context`.

- [ ] **Step 3: Implement**

`SafeExchange.Core\Mcp\OAuth\EntraTokenClient.cs`:

```csharp
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
```

`SafeExchange.Core\Mcp\McpServiceRegistration.cs` — before:

```csharp
    using Microsoft.Extensions.DependencyInjection;
    using SafeExchange.Core.Mcp.Tools;
    using System;

    public static class McpServiceRegistration
    {
        /// <summary>
        /// Registers the MCP tools (scoped, like the services they call), their registry, the dispatcher and the
        /// protected-resource facts. Needs the SafeExchange services and the <c>Authentication</c> options.
        /// </summary>
```

after:

```csharp
    using Microsoft.Extensions.DependencyInjection;
    using SafeExchange.Core.Mcp.OAuth;
    using SafeExchange.Core.Mcp.Tools;
    using System;
    using System.Net.Http;

    public static class McpServiceRegistration
    {
        /// <summary>
        /// Registers the MCP tools (scoped, like the services they call), their registry, the dispatcher, the
        /// protected-resource facts and the OAuth broker with its Entra token client. Needs the SafeExchange services
        /// and the <c>Authentication</c>, <c>Features</c> and <c>Mcp:OAuth</c> options.
        /// </summary>
```

and before:

```csharp
            services.AddScoped<McpToolRegistry>();
            services.AddScoped<McpRequestDispatcher>();
            services.AddSingleton<McpProtectedResource>();
            return services;
```

after:

```csharp
            services.AddScoped<McpToolRegistry>();
            services.AddScoped<McpRequestDispatcher>();
            services.AddSingleton<McpProtectedResource>();

            services.AddHttpClient(EntraTokenClient.HttpClientName, client => client.Timeout = EntraTokenClient.Timeout)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
            services.AddSingleton<IEntraTokenClient, EntraTokenClient>();
            services.AddSingleton<McpOAuthBroker>();
            return services;
```

- [ ] **Step 4: Run the tests to see them pass**

Run the phase filter.
Expected: `Passed!  - Failed:     0, Passed:   495, Skipped:     0, Total:   495, …`
(the task filter alone: `Passed:   166`).

- [ ] **Step 5: Commit**

```powershell
git add SafeExchange.Core\Mcp\OAuth\EntraTokenClient.cs SafeExchange.Core\Mcp\McpServiceRegistration.cs SafeExchange.Tests\Mcp\OAuth\EntraTokenClientTests.cs
git commit -m "feat(mcp-oauth): Entra token client and broker registration" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 6: Functions adapter, routes, triggers and the skip list

**Files:**
- Create: `SafeExchange.Core.Functions\Mcp\OAuth\McpOAuthHandler.cs`, `SafeExchange.Functions\Functions\SafeMcpOAuth.cs`
- Modify: `SafeExchange.Core.Functions\Mcp\McpRoutes.cs` (whole file), `SafeExchange.Core.Functions\SafeExchangeFunctionsStartup.cs`
- Test (create): `SafeExchange.Tests\Mcp\OAuth\McpOAuthHttpTestBase.cs`, `SafeExchange.Tests\Mcp\OAuth\McpOAuthHandlerTests.cs`,
  `SafeExchange.Tests\Mcp\OAuth\McpOAuthLoggingTests.cs`
- Test (modify): `SafeExchange.Tests\Mcp\McpChallengeTests.cs`, `SafeExchange.Tests\Tests\SafeExchangeFunctionsStartupTests.cs`,
  `SafeExchange.Tests\Services\ServiceRegistrationTests.cs`

- [ ] **Step 1: Write the adapter tests**

`SafeExchange.Tests\Mcp\OAuth\McpOAuthHttpTestBase.cs`:

```csharp
/// <summary>
/// McpOAuthHttpTestBase
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using Microsoft.Extensions.DependencyInjection;
    using NUnit.Framework;
    using SafeExchange.Core.Functions.Mcp.OAuth;
    using SafeExchange.Core.Mcp.OAuth;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.IO;
    using System.Text;

    /// <summary>
    /// <see cref="McpHttpTestBase"/> with the broker configured (<c>Mcp:OAuth:ClientId</c>), the
    /// <see cref="McpOAuthHandler"/> adapter and a <see cref="FakeEntraTokenClient"/> in place of Entra.
    /// </summary>
    public abstract class McpOAuthHttpTestBase : McpHttpTestBase
    {
        protected const string BrokerClientId = "00000000-0000-0000-0000-0000000000c9";

        protected FakeEntraTokenClient Entra { get; private set; } = null!;

        protected McpOAuthHandler OAuthHandler => this.Services.GetRequiredService<McpOAuthHandler>();

        [SetUp]
        public void OAuthSetUp()
        {
            this.BuildProvider(("Mcp:OAuth:ClientId", BrokerClientId));
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            this.Entra = new FakeEntraTokenClient();
            services.AddSingleton<IEntraTokenClient>(this.Entra);
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

`SafeExchange.Tests\Mcp\OAuth\McpOAuthHandlerTests.cs`:

```csharp
/// <summary>
/// McpOAuthHandlerTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using Microsoft.Azure.Functions.Worker.Http;
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// The Functions adapter: request URL and body in, status, <c>Location</c>, JSON body and
    /// <c>Cache-Control: no-store</c> out; a bare 404 while the broker is off.
    /// </summary>
    [TestFixture]
    public class McpOAuthHandlerTests : McpOAuthHttpTestBase
    {
        private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

        private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        [TestCase(".well-known/oauth-authorization-server")]
        [TestCase(".well-known/openid-configuration")]
        public async Task Metadata_IsTheBrokersDocument_ForThisHost(string path)
        {
            var response = await this.OAuthHandler.Metadata(OAuthRequest("get", path));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            AssertJsonNoStore(response);
            Assert.That(BodyOf(response), Is.EqualTo(AuthorizationServerMetadata.ToJson(ApiBase, new[] { "api://safeexchange.test/user_impersonation", "offline_access" })));
            Assert.That(BodyOf(response), Does.StartWith("{\"issuer\":\"https://safeexchange.test/api\","));
        }

        [Test]
        public async Task Register_AnswersCreated_WithTheConfiguredClient()
        {
            var response = await this.OAuthHandler.Register(OAuthRequest("post", "oauth/register", "{\"redirect_uris\":[\"http://127.0.0.1:53682/callback\"]}"));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            AssertJsonNoStore(response);
            Assert.That(BodyOf(response), Does.StartWith($"{{\"client_id\":\"{BrokerClientId}\","));
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
            var query = $"oauth/authorize?client_id={BrokerClientId}&redirect_uri=https%3A%2F%2Fevil.example%2Fcb&response_type=code&code_challenge={Challenge}&code_challenge_method=S256";

            var response = await this.OAuthHandler.Authorize(OAuthRequest("get", query));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(response.Headers.Contains("Location"), Is.False);
            AssertJsonNoStore(response);
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
            this.Entra.Response = new EntraTokenResponse(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}");
            var body = $"grant_type=authorization_code&code=the-code&code_verifier={Verifier}&redirect_uri=http%3A%2F%2F127.0.0.1%3A53682%2Fcallback&client_id={BrokerClientId}";

            var response = await this.OAuthHandler.Token(OAuthRequest("post", "oauth/token", body));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            AssertJsonNoStore(response);
            Assert.That(BodyOf(response), Is.EqualTo("{\"error\":\"invalid_grant\"}"));
            Assert.That(this.Entra.Requests.Single()["redirect_uri"], Is.EqualTo("https://safeexchange.test/api/oauth/callback"));
        }

        [Test]
        public async Task Token_WithAnOversizeBody_IsRefused_WithoutCallingEntra()
        {
            var body = "grant_type=refresh_token&refresh_token=" + new string('r', 1024 * 1024);

            var response = await this.OAuthHandler.Token(OAuthRequest("post", "oauth/token", body));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(this.Entra.Requests, Is.Empty);
        }

        [TestCase("False", BrokerClientId)]
        [TestCase("True", "")]
        public async Task EveryRoute_IsABareNotFound_WhileTheBrokerIsOff(string mcpEndpoint, string clientId)
        {
            this.BuildProvider(("Features:McpEndpoint", mcpEndpoint), ("Mcp:OAuth:ClientId", clientId));
            var handler = this.OAuthHandler;

            var responses = new[]
            {
                await handler.Metadata(OAuthRequest("get", ".well-known/oauth-authorization-server")),
                await handler.Metadata(OAuthRequest("get", ".well-known/openid-configuration")),
                await handler.Register(OAuthRequest("post", "oauth/register", "{\"redirect_uris\":[\"http://127.0.0.1:1/cb\"]}")),
                await handler.Authorize(OAuthRequest("get", $"oauth/authorize?client_id={BrokerClientId}")),
                await handler.Callback(OAuthRequest("get", "oauth/callback?code=c")),
                await handler.Token(OAuthRequest("post", "oauth/token", "grant_type=refresh_token&refresh_token=r")),
            };

            foreach (var response in responses)
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                Assert.That(response.Headers, Is.Empty);
                Assert.That(BodyOf(response), Is.Empty);
            }

            Assert.That(this.Entra.Requests, Is.Empty);
        }

        private static void AssertJsonNoStore(HttpResponseData response)
        {
            Assert.That(response.Headers.GetValues("Content-Type"), Is.EqualTo(new[] { "application/json" }));
            Assert.That(response.Headers.GetValues("Cache-Control"), Is.EqualTo(new[] { "no-store" }));
        }
    }
}
```

`SafeExchange.Tests\Mcp\OAuth\McpOAuthLoggingTests.cs` (like `McpLoggingTests`: every category captured at `Trace`):

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
    /// Runs register, authorize, callback (success and Entra error) and token (code, refresh, Entra error) through the
    /// adapter with every log category captured, and checks that no code, verifier, challenge, token, client state,
    /// wrapped state or Entra error description reached a log, while routes and outcomes did.
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
        public async Task AFullSignIn_LogsOutcomesButNoCodeVerifierTokenOrState()
        {
            var handler = this.OAuthHandler;
            await handler.Register(OAuthRequest("post", "oauth/register", "{\"redirect_uris\":[\"http://127.0.0.1:53682/callback\"]}"));
            var authorize = await handler.Authorize(OAuthRequest("get",
                $"oauth/authorize?client_id={BrokerClientId}&redirect_uri=http%3A%2F%2F127.0.0.1%3A53682%2Fcallback&response_type=code"
                + $"&code_challenge={Challenge}&code_challenge_method=S256&state={ClientState}"));
            var entraLocation = authorize.Headers.GetValues("Location").Single();
            Assert.That(OAuthParameters.TryParse(new Uri(entraLocation).Query, out var entraParameters), Is.True);
            var wrappedState = entraParameters!["state"];
            var callback = await handler.Callback(OAuthRequest("get", $"oauth/callback?code={Code}&state={wrappedState}"));
            await handler.Callback(OAuthRequest("get", $"oauth/callback?error=access_denied&error_description={Uri.EscapeDataString(EntraDescription)}&state={wrappedState}"));
            await handler.Token(OAuthRequest("post", "oauth/token", $"grant_type=authorization_code&code={Code}&code_verifier={Verifier}&client_id={BrokerClientId}"));
            await handler.Token(OAuthRequest("post", "oauth/token", $"grant_type=refresh_token&refresh_token={FakeEntraTokenClient.RefreshToken}"));
            this.Entra.Response = new EntraTokenResponse(HttpStatusCode.BadRequest, $"{{\"error\":\"invalid_grant\",\"error_description\":\"{EntraDescription}\"}}");
            await handler.Token(OAuthRequest("post", "oauth/token", $"grant_type=refresh_token&refresh_token={FakeEntraTokenClient.RefreshToken}"));
            Assert.That(callback.Headers.GetValues("Location").Single(), Does.Contain(Code), "Arrange: the flow moved the code.");

            var logs = string.Join('\n', this.Logs.Entries);
            Assert.That(logs, Does.Contain("MCP OAuth register answered 201"));
            Assert.That(logs, Does.Contain("MCP OAuth authorize redirected to Entra."));
            Assert.That(logs, Does.Contain("MCP OAuth callback forwarded the authorization code to the client."));
            Assert.That(logs, Does.Contain("MCP OAuth callback forwarded Entra error access_denied to the client."));
            Assert.That(logs, Does.Contain("MCP OAuth token (authorization_code) relayed Entra status 200"));
            Assert.That(logs, Does.Contain("MCP OAuth token (refresh_token) relayed Entra status 400 (error: invalid_grant)."));
            foreach (var forbidden in new[] { Code, Verifier, Challenge, ClientState, wrappedState, FakeEntraTokenClient.AccessToken, FakeEntraTokenClient.RefreshToken, EntraDescription })
            {
                Assert.That(logs, Does.Not.Contain(forbidden), $"'{forbidden}' was logged.");
            }
        }
    }
}
```

- [ ] **Step 2: Extend the existing registration and skip-list tests**

`SafeExchange.Tests\Mcp\McpChallengeTests.cs` — before:

```csharp
    /// The 401 written by the authentication middlewares (RFC 9728 challenge only while MCP is enabled, body
    /// unchanged) and the predicate that leaves only the metadata trigger anonymous.
```

after:

```csharp
    /// The 401 written by the authentication middlewares (RFC 9728 challenge only while MCP is enabled, body
    /// unchanged) and the predicate that leaves only the metadata and OAuth broker triggers anonymous.
```

and before:

```csharp
        [TestCase("SafeExchange-OAuthProtectedResource", "httpTrigger", false)]
        [TestCase("SafeExchange-Mcp", "httpTrigger", true)]
        [TestCase("SafeExchange-SecretMeta", "httpTrigger", true)]
        [TestCase("SafeExchange-Purge", "timerTrigger", false)]
        public void OnlyTheMetadataTrigger_SkipsAuthentication(string functionName, string triggerType, bool authenticated)
```

after:

```csharp
        [TestCase("SafeExchange-OAuthProtectedResource", "httpTrigger", false)]
        [TestCase("SafeExchange-OAuthAuthorizationServerMetadata", "httpTrigger", false)]
        [TestCase("SafeExchange-OAuthOpenIdConfiguration", "httpTrigger", false)]
        [TestCase("SafeExchange-OAuthRegister", "httpTrigger", false)]
        [TestCase("SafeExchange-OAuthAuthorize", "httpTrigger", false)]
        [TestCase("SafeExchange-OAuthCallback", "httpTrigger", false)]
        [TestCase("SafeExchange-OAuthToken", "httpTrigger", false)]
        [TestCase("safeexchange-oauthtoken", "httpTrigger", true)]
        [TestCase("SafeExchange-OAuthToken2", "httpTrigger", true)]
        [TestCase("SafeExchange-OAuth", "httpTrigger", true)]
        [TestCase("SafeExchange-Mcp", "httpTrigger", true)]
        [TestCase("SafeExchange-SecretMeta", "httpTrigger", true)]
        [TestCase("SafeExchange-Purge", "timerTrigger", false)]
        public void OnlyTheMetadataAndBrokerTriggers_SkipAuthentication_ByExactName(string functionName, string triggerType, bool authenticated)
```

`SafeExchange.Tests\Tests\SafeExchangeFunctionsStartupTests.cs` — before:

```csharp
                "Scoped OAuthProtectedResourceHandler <- OAuthProtectedResourceHandler",
```

after:

```csharp
                "Scoped OAuthProtectedResourceHandler <- OAuthProtectedResourceHandler",
                "Scoped McpOAuthHandler <- McpOAuthHandler",
```

`SafeExchange.Tests\Services\ServiceRegistrationTests.cs` — before:

```csharp
    using SafeExchange.Core.Functions.Mcp;
    using SafeExchange.Core.Mcp;
```

after:

```csharp
    using SafeExchange.Core.Functions.Mcp;
    using SafeExchange.Core.Functions.Mcp.OAuth;
    using SafeExchange.Core.Mcp;
```

and before:

```csharp
            Assert.That(handlers.GetRequiredService<OAuthProtectedResourceHandler>(), Is.Not.Null);
```

after:

```csharp
            Assert.That(handlers.GetRequiredService<OAuthProtectedResourceHandler>(), Is.Not.Null);
            Assert.That(handlers.GetRequiredService<McpOAuthHandler>(), Is.Not.Null);
```

- [ ] **Step 3: Run the tests to see them fail**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.Mcp"`
Expected: build fails with
`SafeExchange.Tests\Services\ServiceRegistrationTests.cs(14,43): error CS0234: The type or namespace name 'OAuth' does not exist in the namespace 'SafeExchange.Core.Functions.Mcp' (are you missing an assembly reference?)`
and `SafeExchange.Tests\Mcp\OAuth\McpOAuthHttpTestBase.cs(26,19): error CS0246: The type or namespace name 'McpOAuthHandler' could not be found …`.

- [ ] **Step 4: Implement the routes**

Replace the whole of `SafeExchange.Core.Functions\Mcp\McpRoutes.cs` with:

```csharp
/// <summary>
/// McpRoutes
/// </summary>

namespace SafeExchange.Core.Functions.Mcp
{
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Routes and function names of the MCP triggers. Azure Functions serves HTTP triggers under the
    /// <c>api</c> route prefix (host.json sets no other), so the metadata document lives at
    /// <c>/api/.well-known/oauth-protected-resource</c>; the <c>WWW-Authenticate</c> challenge points there.
    /// The OAuth broker's issuer is <c>https://host/api</c>, so its metadata is served at
    /// <c>/api/.well-known/openid-configuration</c> (the issuer-relative form MCP clients try) and
    /// <c>/api/.well-known/oauth-authorization-server</c>.
    /// </summary>
    public static class McpRoutes
    {
        public const string RoutePrefix = "api";

        public const string Mcp = "mcp";

        public const string ProtectedResourceMetadata = ".well-known/oauth-protected-resource";

        public const string OAuthAuthorizationServerMetadata = McpOAuthPaths.AuthorizationServerMetadata;

        public const string OAuthOpenIdConfiguration = McpOAuthPaths.OpenIdConfiguration;

        public const string OAuthRegister = McpOAuthPaths.Register;

        public const string OAuthAuthorize = McpOAuthPaths.Authorize;

        public const string OAuthCallback = McpOAuthPaths.Callback;

        public const string OAuthToken = McpOAuthPaths.Token;

        public const string McpFunctionName = "SafeExchange-Mcp";

        public const string ProtectedResourceMetadataFunctionName = "SafeExchange-OAuthProtectedResource";

        public const string OAuthAuthorizationServerMetadataFunctionName = "SafeExchange-OAuthAuthorizationServerMetadata";

        public const string OAuthOpenIdConfigurationFunctionName = "SafeExchange-OAuthOpenIdConfiguration";

        public const string OAuthRegisterFunctionName = "SafeExchange-OAuthRegister";

        public const string OAuthAuthorizeFunctionName = "SafeExchange-OAuthAuthorize";

        public const string OAuthCallbackFunctionName = "SafeExchange-OAuthCallback";

        public const string OAuthTokenFunctionName = "SafeExchange-OAuthToken";

        public const string ProtectedResourceMetadataPath = "/" + RoutePrefix + "/" + ProtectedResourceMetadata;

        /// <summary>
        /// The functions the authentication middleware skips, matched by exact (ordinal) name.
        /// </summary>
        public static IReadOnlySet<string> AnonymousFunctionNames { get; } = new HashSet<string>(StringComparer.Ordinal)
        {
            ProtectedResourceMetadataFunctionName,
            OAuthAuthorizationServerMetadataFunctionName,
            OAuthOpenIdConfigurationFunctionName,
            OAuthRegisterFunctionName,
            OAuthAuthorizeFunctionName,
            OAuthCallbackFunctionName,
            OAuthTokenFunctionName,
        };
    }
}
```

- [ ] **Step 5: Implement the adapter**

`SafeExchange.Core.Functions\Mcp\OAuth\McpOAuthHandler.cs`:

```csharp
/// <summary>
/// McpOAuthHandler
/// </summary>

namespace SafeExchange.Core.Functions.Mcp.OAuth
{
    using Microsoft.Azure.Functions.Worker.Http;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.IO;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// Functions adapters for the anonymous OAuth broker routes: request URL and body into
    /// <see cref="McpOAuthBroker"/>, its result out as status, <c>Location</c>, JSON body and
    /// <c>Cache-Control: no-store</c>. A disabled broker answers a bare 404.
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

        public Task<HttpResponseData> Authorize(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return WriteAsync(request, this.broker.Authorize(McpHandler.ApiBaseUri(request.Url), request.Url.Query));
        }

        public Task<HttpResponseData> Callback(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return WriteAsync(request, this.broker.Callback(request.Url.Query));
        }

        public async Task<HttpResponseData> Token(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var result = await this.broker.TokenAsync(McpHandler.ApiBaseUri(request.Url), await ReadBodyAsync(request), request.FunctionContext.CancellationToken);
            return await WriteAsync(request, result);
        }

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

            if (result.Body is not null)
            {
                response.Headers.Add("Content-Type", "application/json");
                await response.WriteStringAsync(result.Body);
            }

            return response;
        }
    }
}
```

- [ ] **Step 6: Skip list and DI in `SafeExchangeFunctionsStartup`**

`SafeExchange.Core.Functions\SafeExchangeFunctionsStartup.cs` — before:

```csharp
    using SafeExchange.Core.Functions.Mcp;
    using SafeExchange.Core.Middleware;
```

after:

```csharp
    using SafeExchange.Core.Functions.Mcp;
    using SafeExchange.Core.Functions.Mcp.OAuth;
    using SafeExchange.Core.Middleware;
```

before:

```csharp
        /// <summary>
        /// HTTP triggers that need a bearer token: all except the anonymous protected-resource metadata.
        /// </summary>
        public static bool IsAuthenticatedHttpTrigger(FunctionContext context)
            => IsHttpTrigger(context)
                && !string.Equals(context.FunctionDefinition.Name, McpRoutes.ProtectedResourceMetadataFunctionName, StringComparison.Ordinal);
```

after:

```csharp
        /// <summary>
        /// HTTP triggers that need a bearer token: all except the anonymous protected-resource metadata and the
        /// OAuth broker (<see cref="McpRoutes.AnonymousFunctionNames"/>, matched by exact name).
        /// </summary>
        public static bool IsAuthenticatedHttpTrigger(FunctionContext context)
            => IsHttpTrigger(context)
                && !McpRoutes.AnonymousFunctionNames.Contains(context.FunctionDefinition.Name);
```

before:

```csharp
            services.AddScoped<OAuthProtectedResourceHandler>();
```

after:

```csharp
            services.AddScoped<OAuthProtectedResourceHandler>();
            services.AddScoped<McpOAuthHandler>();
```

- [ ] **Step 7: Add the triggers**

`SafeExchange.Functions\Functions\SafeMcpOAuth.cs`:

```csharp
/// <summary>
/// SafeMcpOAuth
/// </summary>

namespace SafeExchange.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using SafeExchange.Core.Functions.Mcp;
    using SafeExchange.Core.Functions.Mcp.OAuth;
    using System;
    using System.Threading.Tasks;

    // Anonymous: SafeExchangeFunctionsStartup.IsAuthenticatedHttpTrigger excludes these functions from the auth middleware.
    public class SafeMcpOAuth
    {
        private readonly McpOAuthHandler handler;

        public SafeMcpOAuth(McpOAuthHandler handler)
        {
            this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        [Function(McpRoutes.OAuthAuthorizationServerMetadataFunctionName)]
        public Task<HttpResponseData> AuthorizationServerMetadata(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = McpRoutes.OAuthAuthorizationServerMetadata)]
            HttpRequestData request)
            => this.handler.Metadata(request);

        [Function(McpRoutes.OAuthOpenIdConfigurationFunctionName)]
        public Task<HttpResponseData> OpenIdConfiguration(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = McpRoutes.OAuthOpenIdConfiguration)]
            HttpRequestData request)
            => this.handler.Metadata(request);

        [Function(McpRoutes.OAuthRegisterFunctionName)]
        public Task<HttpResponseData> Register(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = McpRoutes.OAuthRegister)]
            HttpRequestData request)
            => this.handler.Register(request);

        [Function(McpRoutes.OAuthAuthorizeFunctionName)]
        public Task<HttpResponseData> Authorize(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = McpRoutes.OAuthAuthorize)]
            HttpRequestData request)
            => this.handler.Authorize(request);

        [Function(McpRoutes.OAuthCallbackFunctionName)]
        public Task<HttpResponseData> Callback(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = McpRoutes.OAuthCallback)]
            HttpRequestData request)
            => this.handler.Callback(request);

        [Function(McpRoutes.OAuthTokenFunctionName)]
        public Task<HttpResponseData> Token(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = McpRoutes.OAuthToken)]
            HttpRequestData request)
            => this.handler.Token(request);
    }
}
```

- [ ] **Step 8: Build and run the phase filter**

Run: `dotnet build SafeExchange.slnx --nologo -v q --no-incremental`
Expected: `704 Warning(s)`, `0 Error(s)`.

Run the phase filter.
Expected: `Passed!  - Failed:     0, Passed:   515, Skipped:     0, Total:   515, …`
(the task filter alone: `Passed:   177`).

- [ ] **Step 9 (optional, recommended): real Functions host smoke test**

Needs Azure Functions Core Tools and Azurite (for the timer/queue triggers' host storage):

```powershell
docker run --detach --name saex-oauth-azurite --publish 127.0.0.1:10000:10000 --publish 127.0.0.1:10001:10001 --publish 127.0.0.1:10002:10002 mcr.microsoft.com/azure-storage/azurite
Push-Location SafeExchange.Functions
$env:Features__McpEndpoint = 'True'; $env:Mcp__OAuth__ClientId = '00000000-0000-0000-0000-0000000000c9'
func start --port 7391
```

Expected in the host output: `Mapped function route 'api/.well-known/oauth-authorization-server' [get] to 'SafeExchange-OAuthAuthorizationServerMetadata'`,
the same for `api/.well-known/openid-configuration` [get], `api/oauth/register` [post], `api/oauth/authorize` [get],
`api/oauth/callback` [get] and `api/oauth/token` [post]. In a second PowerShell:

```powershell
$b = 'http://localhost:7391/api'
curl.exe -s "$b/.well-known/oauth-protected-resource"
curl.exe -s -i "$b/.well-known/openid-configuration"
curl.exe -s -i "$b/oauth/authorize?client_id=00000000-0000-0000-0000-0000000000c9&redirect_uri=http%3A%2F%2F127.0.0.1%3A53682%2Fcallback&response_type=code&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256&state=abc&resource=x"
curl.exe -s -i "$b/oauth/authorize?client_id=00000000-0000-0000-0000-0000000000c9&redirect_uri=https%3A%2F%2Fevil.example%2Fcb&response_type=code"
curl.exe -s -i -X POST "$b/oauth/token" -d "grant_type=refresh_token&refresh_token=bogus"
curl.exe -s -i -X POST "$b/mcp"
```

Expected (no bearer token anywhere): the protected-resource JSON with `"resource":"http://localhost:7391/api/mcp"` and
`"authorization_servers":["http://localhost:7391/api"]`; `200` with `Cache-Control: no-store` and
`"issuer":"http://localhost:7391/api"`; `302` with `Location: https://login.microsoftonline.com/<tenant>/oauth2/v2.0/authorize?client_id=00000000-0000-0000-0000-0000000000c9&response_type=code&redirect_uri=http%3A%2F%2Flocalhost%3A7391%2Fapi%2Foauth%2Fcallback&response_mode=query&scope=…%20offline_access&code_challenge=…&code_challenge_method=S256&state=…`
(no `resource`); `400` `invalid_request` without `Location`; `400` `{"error":"invalid_grant",…AADSTS9002313…}` relayed
from the real Entra; `401` with `WWW-Authenticate: Bearer resource_metadata=…`. The host log shows lines such as
`MCP OAuth authorize redirected to Entra.` and `MCP OAuth token (refresh_token) relayed Entra status 400 (error: invalid_grant).`
Stop the host (`Ctrl+C`), `Pop-Location`, `docker rm -f saex-oauth-azurite`.

- [ ] **Step 10: Commit**

```powershell
git add SafeExchange.Core.Functions\Mcp\McpRoutes.cs SafeExchange.Core.Functions\Mcp\OAuth\McpOAuthHandler.cs SafeExchange.Core.Functions\SafeExchangeFunctionsStartup.cs SafeExchange.Functions\Functions\SafeMcpOAuth.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthHttpTestBase.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthHandlerTests.cs SafeExchange.Tests\Mcp\OAuth\McpOAuthLoggingTests.cs SafeExchange.Tests\Mcp\McpChallengeTests.cs SafeExchange.Tests\Tests\SafeExchangeFunctionsStartupTests.cs SafeExchange.Tests\Services\ServiceRegistrationTests.cs
git commit -m "feat(mcp-oauth): anonymous broker triggers and Functions adapters" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 7: ARM parameter and Key Vault secret

**Files:**
- Modify: `deployment\current\arm\services-template.arm.json` (parameter after `settings-features-mcpEndpoint`;
  secret resource after the `Features--McpEndpoint` secret)
- Modify: `deployment\current\arm\services-parameters.arm.json`, `deployment\current\arm\services-parameters-test.arm.json`,
  `deployment\current\arm\services-parameters-prd.arm.json`

- [ ] **Step 1: Add the parameter to the template**

In `deployment\current\arm\services-template.arm.json` — before:

```json
        "settings-features-mcpEndpoint": {
            "defaultValue": "False",
            "type": "string",
            "allowedValues": [
                "True",
                "False"
            ],
            "metadata": {
                "description": "Enables the first-party MCP endpoint (POST /api/mcp and /api/.well-known/oauth-protected-resource). When False both routes return 404. Default off; see docs/mcp.md."
            }
        },
```

after:

```json
        "settings-features-mcpEndpoint": {
            "defaultValue": "False",
            "type": "string",
            "allowedValues": [
                "True",
                "False"
            ],
            "metadata": {
                "description": "Enables the first-party MCP endpoint (POST /api/mcp and /api/.well-known/oauth-protected-resource). When False both routes return 404. Default off; see docs/mcp.md."
            }
        },
        "settings-mcp-oauthClientId": {
            "defaultValue": "",
            "type": "string",
            "metadata": {
                "description": "Application (client) ID of the Entra public client app used by the MCP OAuth broker (/api/oauth/*, /api/.well-known/oauth-authorization-server). Empty: the Mcp--OAuth--ClientId secret is not written (an existing value is kept; without one the broker answers 404). See docs/mcp.md."
            }
        },
```

- [ ] **Step 2: Add the Key Vault secret to the template**

In `deployment\current\arm\services-template.arm.json` — before:

```json
                "contentType": "text/plain",
                "value": "[parameters('settings-features-mcpEndpoint')]"
            }
        },
```

after:

```json
                "contentType": "text/plain",
                "value": "[parameters('settings-features-mcpEndpoint')]"
            }
        },
        {
            "condition": "[not(empty(parameters('settings-mcp-oauthClientId')))]",
            "type": "Microsoft.KeyVault/vaults/secrets",
            "apiVersion": "2023-02-01",
            "name": "[concat(parameters('safeexchange_kv_name'), '/Mcp--OAuth--ClientId')]",
            "location": "[parameters('default_location')]",
            "dependsOn": [
                "[resourceId('Microsoft.KeyVault/vaults', parameters('safeexchange_kv_name'))]"
            ],
            "properties": {
                "attributes": {
                    "enabled": true
                },
                "contentType": "text/plain",
                "value": "[parameters('settings-mcp-oauthClientId')]"
            }
        },
```

- [ ] **Step 3: Add the parameter to the three parameter files (empty everywhere)**

`deployment\current\arm\services-parameters-test.arm.json` — before:

```json
        "settings-features-mcpEndpoint": {
            "value": "True"
        },
```

after:

```json
        "settings-features-mcpEndpoint": {
            "value": "True"
        },
        "settings-mcp-oauthClientId": {
            "value": ""
        },
```

`deployment\current\arm\services-parameters-prd.arm.json` — before:

```json
        "settings-features-mcpEndpoint": {
            "value": "False"
        },
```

after:

```json
        "settings-features-mcpEndpoint": {
            "value": "False"
        },
        "settings-mcp-oauthClientId": {
            "value": ""
        },
```

`deployment\current\arm\services-parameters.arm.json` — before:

```json
        "settings-authentication-validIssuers": {
            "value": null
        },
```

after:

```json
        "settings-authentication-validIssuers": {
            "value": null
        },
        "settings-mcp-oauthClientId": {
            "value": ""
        },
```

- [ ] **Step 4: Validate the JSON**

```powershell
$arm = 'deployment\current\arm'
$t = Get-Content "$arm\services-template.arm.json" -Raw | ConvertFrom-Json
$names = $t.parameters.PSObject.Properties.Name
foreach ($p in 'services-parameters.arm.json','services-parameters-test.arm.json','services-parameters-prd.arm.json') {
    $j = Get-Content "$arm\$p" -Raw | ConvertFrom-Json
    $unknown = $j.parameters.PSObject.Properties.Name | Where-Object { $_ -notin $names }
    "$p ok; oauthClientId='$($j.parameters.'settings-mcp-oauthClientId'.value)'; unknown=[$($unknown -join ',')]"
}
$s = $t.resources | Where-Object { $_.name -like '*Mcp--OAuth--ClientId*' }
"secret: condition=$($s.condition) value=$($s.properties.value)"
```

Expected:

```
services-parameters.arm.json ok; oauthClientId=''; unknown=[]
services-parameters-test.arm.json ok; oauthClientId=''; unknown=[]
services-parameters-prd.arm.json ok; oauthClientId=''; unknown=[]
secret: condition=[not(empty(parameters('settings-mcp-oauthClientId')))] value=[parameters('settings-mcp-oauthClientId')]
```

- [ ] **Step 5: Commit**

```powershell
git add deployment\current\arm\services-template.arm.json deployment\current\arm\services-parameters.arm.json deployment\current\arm\services-parameters-test.arm.json deployment\current\arm\services-parameters-prd.arm.json
git commit -m "feat(mcp-oauth): ARM parameter and Key Vault secret for the broker client ID" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 8: Documentation

**Files:**
- Modify: `docs\mcp.md`, `docs\api-endpoints.md`, `docs\consumer-migration.md`

No tests (documentation only). Each edit replaces the exact "before" text.

- [ ] **Step 1: `docs\mcp.md` — protocol paragraph**

Before:

````markdown
The dispatcher and tools live in `SafeExchange.Core/Mcp` and depend only on the services; the Functions adapter (`McpHandler`, `OAuthProtectedResourceHandler`) is in `SafeExchange.Core.Functions`, and the two triggers are in `SafeExchange.Functions`.
````

After:

````markdown
The dispatcher and tools live in `SafeExchange.Core/Mcp` and depend only on the services, and the OAuth broker lives in `SafeExchange.Core/Mcp/OAuth`; the Functions adapters (`McpHandler`, `OAuthProtectedResourceHandler`, `McpOAuthHandler`) are in `SafeExchange.Core.Functions`, and the triggers (`SafeMcp`, `SafeOAuthMetadata`, `SafeMcpOAuth`) are in `SafeExchange.Functions`.
````

- [ ] **Step 2: `docs\mcp.md` — Enabling table and bullets**

Before:

````markdown
| `Features:McpEndpoint` | `Features--McpEndpoint` (`True` / `False`) | `settings-features-mcpEndpoint` | `False` |
| `Authentication:McpScopes` (optional) | `Authentication--McpScopes` | — (set the secret by hand) | empty |

- With the flag off, `GET /api/.well-known/oauth-protected-resource` (anonymous) answers `404`. `POST /api/mcp` answers `404` to authenticated callers; an unauthenticated call gets the normal `401` from authentication, because authentication runs before the handler checks the flag (it is deliberately not skipped based on the flag). Either way `401` responses carry no `WWW-Authenticate` challenge while the flag is off.
````

After:

````markdown
| `Features:McpEndpoint` | `Features--McpEndpoint` (`True` / `False`) | `settings-features-mcpEndpoint` | `False` |
| `Authentication:McpScopes` (optional) | `Authentication--McpScopes` | — (set the secret by hand) | empty |
| `Mcp:OAuth:ClientId` (optional) | `Mcp--OAuth--ClientId` | `settings-mcp-oauthClientId` (written only when not empty) | empty (OAuth broker off) |

- With the flag off, `GET /api/.well-known/oauth-protected-resource` and the [OAuth broker](#oauth-broker-for-entra-id) routes (all anonymous) answer `404`. `POST /api/mcp` answers `404` to authenticated callers; an unauthenticated call gets the normal `401` from authentication, because authentication runs before the handler checks the flag (it is deliberately not skipped based on the flag). Either way `401` responses carry no `WWW-Authenticate` challenge while the flag is off.
````

Before:

````markdown
- The `deployment\current\arm` templates define `settings-features-mcpEndpoint` (default `False`) and the `Features--McpEndpoint` secret, like the other `Features--*` settings.

**Derived defaults.** The protected-resource metadata and `file_transfer_info` are derived from the existing `Authentication` section:

| Value | Derived from |
|-------|--------------|
| `resource` | First `api://…` entry of `Authentication:ValidAudiences`; if there is none, the first entry |
| `authorization_servers` | `Authentication:Authority`, with `/v2.0` appended when missing |
| `scopes_supported` (user scope) | `Authentication:McpScopes` (comma-separated) when set, otherwise `{resource}/user_impersonation` |
| S2S application scope | `{resource}/.default` |
````

After:

````markdown
- The `deployment\current\arm` templates define `settings-features-mcpEndpoint` (default `False`) and the `Features--McpEndpoint` secret, like the other `Features--*` settings, and `settings-mcp-oauthClientId` (default empty) for the `Mcp--OAuth--ClientId` secret, which is written only when the parameter is not empty (like `AADClient--ClientSecret`), so a value set by hand survives later deployments.

**Derived defaults.** The protected-resource metadata and `file_transfer_info` are derived from the request URL and the existing `Authentication` section:

| Value | Derived from |
|-------|--------------|
| `resource` | The MCP URL of the host that served the request: `https://<host>/api/mcp` |
| `authorization_servers` | `https://<host>/api` (the [OAuth broker](#oauth-broker-for-entra-id)) when `Mcp:OAuth:ClientId` is set; otherwise `Authentication:Authority`, with `/v2.0` appended when missing |
| API resource (for scopes) | First `api://…` entry of `Authentication:ValidAudiences`; if there is none, the first entry |
| `scopes_supported` (user scope) | `Authentication:McpScopes` (comma-separated) when set, otherwise `{API resource}/user_impersonation` |
| S2S application scope | `{API resource}/.default` |
````

- [ ] **Step 3: `docs\mcp.md` — discovery section, broker and Entra setup**

Before:

````markdown
The anonymous metadata document at that URL tells the client where to sign in:

```json
{
  "resource": "api://<api>",
  "authorization_servers": ["https://login.microsoftonline.com/<tenant>/v2.0"],
  "scopes_supported": ["api://<api>/user_impersonation"],
  "bearer_methods_supported": ["header"]
}
```

The metadata lives under `/api` because Azure Functions serves every HTTP trigger under the `api` route prefix; the challenge points to it explicitly, so clients that follow `resource_metadata` find it. Clients that ignore the hint and probe `https://<host>/.well-known/oauth-protected-resource` get `404`.

Limits to know about:

- **Entra ID has no dynamic client registration.** A client that needs it cannot finish OAuth against Entra on its own.
- `resource` is the API's `api://` identifier, not the MCP URL (Entra rejects a `resource` that does not match the requested scope's API). A client that insists on `resource` matching the MCP URL may reject the metadata.

In both cases use the fallback below. Record per client whether discovery works.
````

After:

````markdown
The anonymous metadata document at that URL tells the client where to sign in (here with the OAuth broker configured):

```json
{
  "resource": "https://<host>/api/mcp",
  "authorization_servers": ["https://<host>/api"],
  "scopes_supported": ["api://<api>/user_impersonation"],
  "bearer_methods_supported": ["header"]
}
```

The metadata lives under `/api` because Azure Functions serves every HTTP trigger under the `api` route prefix; the challenge points to it explicitly, so clients that follow `resource_metadata` find it. Clients that ignore the hint and probe `https://<host>/.well-known/oauth-protected-resource` get `404`.

`resource` is always the MCP URL (RFC 9728; MCP clients such as Copilot CLI reject anything else). Without `Mcp:OAuth:ClientId`, `authorization_servers` names Entra directly; Entra has no dynamic client registration and rejects the MCP URL as `resource`, so most MCP clients cannot finish OAuth that way and need the [fallback](#fallback-bearer-header-from-the-azure-cli).

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

#### Entra setup (once per environment)

Create a public client app for the broker and pre-authorise it on the API app, so users get no consent prompt. In PowerShell, signed in with `az login` as someone who can manage both apps:

```powershell
$apiAppId = '<API app (client) ID>'                      # the app whose api:// URI is in Authentication:ValidAudiences
$apiHost  = 'safeexchange-staging.azurewebsites.net'     # the Function app host
$envName  = 'staging'

# 1. The API's user_impersonation scope id and its sign-in audience.
$scopeId  = az ad app show --id $apiAppId --query "api.oauth2PermissionScopes[?value=='user_impersonation'].id | [0]" -o tsv
$audience = az ad app show --id $apiAppId --query signInAudience -o tsv

# 2. The public client: no secret; Entra redirects to the broker's callback.
ConvertTo-Json -Depth 5 -InputObject @(@{ resourceAppId = $apiAppId; resourceAccess = @(@{ id = $scopeId; type = 'Scope' }) }) |
    Set-Content mcp-client-access.json
$clientAppId = az ad app create --display-name "SafeExchange MCP Client ($envName)" `
    --sign-in-audience $audience `
    --is-fallback-public-client true `
    --public-client-redirect-uris "https://$apiHost/api/oauth/callback" `
    --required-resource-accesses "@mcp-client-access.json" `
    --query appId -o tsv
az ad sp create --id $clientAppId
Remove-Item mcp-client-access.json

# 3. Pre-authorise the client on the API app, keeping the existing entries (the whole api object is written back).
$apiObjectId = az ad app show --id $apiAppId --query id -o tsv
$api = az ad app show --id $apiAppId --query api -o json | ConvertFrom-Json
$others = @($api.preAuthorizedApplications | Where-Object { $_.appId -ne $clientAppId })
$api.preAuthorizedApplications = $others + @([pscustomobject]@{ appId = $clientAppId; delegatedPermissionIds = @($scopeId) })
ConvertTo-Json -Depth 10 -InputObject @{ api = $api } | Set-Content mcp-preauth.json
az rest --method PATCH --url "https://graph.microsoft.com/v1.0/applications/$apiObjectId" --headers "Content-Type=application/json" --body "@mcp-preauth.json"
Remove-Item mcp-preauth.json

$clientAppId
```

Then give SafeExchange the client ID: set `settings-mcp-oauthClientId` to `$clientAppId` in `deployment\current\arm\services-parameters-<env>.arm.json` and run `deployment\deploy.ps1` (it is not a secret), or write the secret directly with `az keyvault secret set --vault-name <key vault> --name Mcp--OAuth--ClientId --value $clientAppId` (an empty parameter never overwrites it). Key Vault configuration reloads every 5 minutes; restart the Function app to apply it at once. Check:

```powershell
curl.exe -s "https://$apiHost/api/.well-known/oauth-protected-resource"   # authorization_servers = ["https://<host>/api"]
curl.exe -s "https://$apiHost/api/.well-known/openid-configuration"       # issuer = "https://<host>/api"
```

To turn the broker off again, delete or disable the `Mcp--OAuth--ClientId` secret (or turn `Features.McpEndpoint` off).
````

- [ ] **Step 4: `docs\mcp.md` — client configuration and staging validation**

Before:

````markdown
If the server needs OAuth, Copilot opens a browser to sign in (or asks you to run `/mcp auth safeexchange`). When OAuth discovery does not work against Entra (see [Authentication](#authentication)), send a static token as a header:
````

After:

````markdown
If the server needs OAuth, Copilot opens a browser to sign in (or asks you to run `/mcp auth safeexchange`). With the [OAuth broker](#oauth-broker-for-entra-id) configured, Copilot registers through `/api/oauth/register`, the browser signs in with Entra and the token is stored and refreshed by Copilot. Without the broker, OAuth discovery does not work against Entra (see [Authentication](#authentication)); send a static token as a header:
````

Before:

````markdown
Any MCP client with streamable HTTP support needs the same three things: the URL `https://<host>/api/mcp`, a bearer token (OAuth where the client can complete it against Entra, otherwise a header) and, optionally, to send back the `Mcp-Session-Id` from `initialize` so audit shows its label. A minimal smoke test with `curl`:
````

After:

````markdown
Any MCP client with streamable HTTP support needs the same three things: the URL `https://<host>/api/mcp`, a bearer token (OAuth through the broker where the client supports dynamic client registration and loopback redirects, otherwise a header) and, optionally, to send back the `Mcp-Session-Id` from `initialize` so audit shows its label. A minimal smoke test with `curl`:
````

Before:

````markdown
3. **MCP as a user from Copilot CLI** via OAuth discovery. Open risk: Entra has no dynamic client registration. If a client cannot complete OAuth, use the `az account get-access-token` header fallback and record the outcome per client.
````

After:

````markdown
3. **MCP as a user from Copilot CLI** via OAuth discovery through the broker (after the [Entra setup](#entra-setup-once-per-environment)): `/mcp auth safeexchange` opens the browser, signs in without a consent prompt and the next tool call succeeds; repeat after the access token expires to see the refresh. If a client cannot complete OAuth, use the `az account get-access-token` header fallback and record the outcome per client.
````

Before:

````markdown
5. **Log scan** (Application Insights and Functions logs) for the whole run: no secret values, file content or tokens.
````

After:

````markdown
5. **Log scan** (Application Insights and Functions logs) for the whole run: no secret values, file content, tokens, authorization codes or code verifiers in traces (request URLs of `/api/oauth/callback` are platform telemetry, see the broker's logging rule).
````

- [ ] **Step 5: `docs\api-endpoints.md`**

Before:

````markdown
This document lists the SafeExchange REST API endpoints, as defined by the HTTP triggers in `SafeExchange.Functions`. All endpoints require a valid Microsoft Entra ID Bearer token, except `GET /api/.well-known/oauth-protected-resource` (see [MCP](#mcp)).
````

After:

````markdown
This document lists the SafeExchange REST API endpoints, as defined by the HTTP triggers in `SafeExchange.Functions`. All endpoints require a valid Microsoft Entra ID Bearer token, except the MCP discovery and OAuth broker routes (`/api/.well-known/*`, `/api/oauth/*`; see [MCP](#mcp)).
````

Before:

````markdown
SafeExchange exposes a first-party [Model Context Protocol](https://modelcontextprotocol.io) endpoint so AI agents can use secrets and files under the same permissions and audit as people. It is off unless `Features.McpEndpoint` is enabled; when off, the anonymous metadata route responds `404`, and `/api/mcp` responds `404` to authenticated callers (an unauthenticated call gets the normal `401`, without the `WWW-Authenticate` challenge, because authentication runs first). See [mcp.md](mcp.md) for setup, authentication, the tool list and the file transfer flows.

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/mcp` | MCP over stateless streamable HTTP: one JSON-RPC message in, one JSON response out. Requires a Bearer token. `GET` and `DELETE` respond `405` |
| GET | `/api/.well-known/oauth-protected-resource` | Anonymous RFC 9728 protected-resource metadata for MCP clients |
````

After:

````markdown
SafeExchange exposes a first-party [Model Context Protocol](https://modelcontextprotocol.io) endpoint so AI agents can use secrets and files under the same permissions and audit as people. It is off unless `Features.McpEndpoint` is enabled; when off, the anonymous metadata and OAuth broker routes respond `404`, and `/api/mcp` responds `404` to authenticated callers (an unauthenticated call gets the normal `401`, without the `WWW-Authenticate` challenge, because authentication runs first). The OAuth broker routes additionally need `Mcp:OAuth:ClientId`. See [mcp.md](mcp.md) for setup, authentication, the OAuth broker, the tool list and the file transfer flows.

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/mcp` | MCP over stateless streamable HTTP: one JSON-RPC message in, one JSON response out. Requires a Bearer token. `GET` and `DELETE` respond `405` |
| GET | `/api/.well-known/oauth-protected-resource` | Anonymous RFC 9728 protected-resource metadata for MCP clients (`resource` = `https://<host>/api/mcp`) |
| GET | `/api/.well-known/openid-configuration` | Anonymous authorization-server metadata of the OAuth broker (issuer `https://<host>/api`) |
| GET | `/api/.well-known/oauth-authorization-server` | Same document as `openid-configuration` |
| POST | `/api/oauth/register` | Anonymous RFC 7591 client registration (JSON). Loopback `redirect_uris` only; answers `201` with the configured Entra public `client_id` |
| GET | `/api/oauth/authorize` | Anonymous. Validates the authorization request (S256 PKCE, loopback `redirect_uri`, known `client_id`) and redirects (`302`) to Entra |
| GET | `/api/oauth/callback` | Anonymous. Entra's redirect target; redirects (`302`) the code or error to the client's loopback `redirect_uri` |
| POST | `/api/oauth/token` | Anonymous (form). Forwards `authorization_code` / `refresh_token` grants to Entra and relays Entra's status and JSON body (`Cache-Control: no-store`) |
````

- [ ] **Step 6: `docs\consumer-migration.md`**

Before:

````markdown
| `SafeExchange.Core.Functions` | Functions-typed: HTTP handlers (`Functions/`), middleware, request filters, `ActionResults` and other HTTP utilities, `SafeExchangeFunctionsStartup`, `McpHandler`, `OAuthProtectedResourceHandler` |
````

After:

````markdown
| `SafeExchange.Core.Functions` | Functions-typed: HTTP handlers (`Functions/`), middleware, request filters, `ActionResults` and other HTTP utilities, `SafeExchangeFunctionsStartup`, `McpHandler`, `OAuthProtectedResourceHandler`, `McpOAuthHandler` |
````

Before:

````markdown
- the six service-backed handlers (`SafeExchangeSecretMeta`, `SafeExchangeSecretContentMeta`, `SafeExchangeSecretStream`, `SafeExchangeContentCommit`, `SafeExchangeAccess`, `SafeExchangeSecretAudit`) and `McpHandler` / `OAuthProtectedResourceHandler`;
````

After:

````markdown
- the six service-backed handlers (`SafeExchangeSecretMeta`, `SafeExchangeSecretContentMeta`, `SafeExchangeSecretStream`, `SafeExchangeContentCommit`, `SafeExchangeAccess`, `SafeExchangeSecretAudit`) and `McpHandler` / `OAuthProtectedResourceHandler` / `McpOAuthHandler`;
````

Before:

````markdown
`IsAuthenticatedHttpTrigger` is every HTTP trigger except the anonymous MCP protected-resource metadata function.
````

After:

````markdown
`IsAuthenticatedHttpTrigger` is every HTTP trigger except the anonymous MCP functions listed in `McpRoutes.AnonymousFunctionNames` (the protected-resource metadata and the six OAuth broker functions), matched by exact name.
````

Before:

````markdown
`Features.McpEndpoint` (default `false`) and `Authentication:McpScopes` (optional) are new; see [mcp.md](mcp.md#enabling). `SafeExchangeStartup.ConfigureServices` already registers the MCP tools, dispatcher and protected-resource facts (`AddSafeExchangeMcp()`).
````

After:

````markdown
`Features.McpEndpoint` (default `false`) and `Authentication:McpScopes` (optional) are new; see [mcp.md](mcp.md#enabling). `SafeExchangeStartup.ConfigureServices` already registers the MCP tools, dispatcher and protected-resource facts (`AddSafeExchangeMcp()`).

`Mcp:OAuth:ClientId` (optional, `McpOAuthOptions`) turns on the OAuth broker for MCP clients; see [mcp.md](mcp.md#oauth-broker-for-entra-id). `SafeExchangeStartup.ConfigureServices` binds the `Mcp:OAuth` section, and `AddSafeExchangeMcp()` also registers `McpOAuthBroker` and `IEntraTokenClient` (`EntraTokenClient`, named `HttpClient` `SafeExchange.McpOAuth.EntraToken`: 10 s timeout, no redirects). A host that calls `AddSafeExchangeMcp()` without `ConfigureServices` must bind `McpOAuthOptions` to `Mcp:OAuth` itself. `McpProtectedResource` now also takes `IOptionsMonitor<McpOAuthOptions>`, and `ToMetadata()` became `ToMetadata(Uri apiBaseUri)`: the published `resource` is the MCP URL (`https://<host>/api/mcp`) instead of the `api://` identifier.
````

Before:

````markdown
Add the two triggers to your Functions app. `McpHandler` and `OAuthProtectedResourceHandler` come from DI (`ConfigureFunctionsServices`); the route constants are in `SafeExchange.Core.Functions.Mcp.McpRoutes`.
````

After:

````markdown
Add the two triggers to your Functions app, and a third for the OAuth broker. `McpHandler`, `OAuthProtectedResourceHandler` and `McpOAuthHandler` come from DI (`ConfigureFunctionsServices`); the route constants and function names are in `SafeExchange.Core.Functions.Mcp.McpRoutes`.
````

Before (end of the code sample and the first two requirements):

````markdown
        => this.handler.Run(request);
}
```

Requirements:

- Your authentication middleware must skip the metadata function (`McpRoutes.ProtectedResourceMetadataFunctionName`), which is what `IsAuthenticatedHttpTrigger` does.
- The metadata is derived from the `Authentication` section (`Authority`, `ValidAudiences`, `McpScopes`).
````

After:

````markdown
        => this.handler.Run(request);
}

public class SafeMcpOAuth
{
    private readonly McpOAuthHandler handler;

    public SafeMcpOAuth(McpOAuthHandler handler)
    {
        this.handler = handler;
    }

    // Anonymous, like the metadata: all six names are in McpRoutes.AnonymousFunctionNames.
    [Function(McpRoutes.OAuthAuthorizationServerMetadataFunctionName)]
    public Task<HttpResponseData> AuthorizationServerMetadata(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = McpRoutes.OAuthAuthorizationServerMetadata)] HttpRequestData request)
        => this.handler.Metadata(request);

    [Function(McpRoutes.OAuthOpenIdConfigurationFunctionName)]
    public Task<HttpResponseData> OpenIdConfiguration(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = McpRoutes.OAuthOpenIdConfiguration)] HttpRequestData request)
        => this.handler.Metadata(request);

    [Function(McpRoutes.OAuthRegisterFunctionName)]
    public Task<HttpResponseData> Register(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = McpRoutes.OAuthRegister)] HttpRequestData request)
        => this.handler.Register(request);

    [Function(McpRoutes.OAuthAuthorizeFunctionName)]
    public Task<HttpResponseData> Authorize(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = McpRoutes.OAuthAuthorize)] HttpRequestData request)
        => this.handler.Authorize(request);

    [Function(McpRoutes.OAuthCallbackFunctionName)]
    public Task<HttpResponseData> Callback(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = McpRoutes.OAuthCallback)] HttpRequestData request)
        => this.handler.Callback(request);

    [Function(McpRoutes.OAuthTokenFunctionName)]
    public Task<HttpResponseData> Token(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = McpRoutes.OAuthToken)] HttpRequestData request)
        => this.handler.Token(request);
}
```

Requirements:

- Your authentication middleware must skip the anonymous functions (`McpRoutes.AnonymousFunctionNames`: the metadata and the six broker functions), which is what `IsAuthenticatedHttpTrigger` does.
- The metadata is derived from the request URL and the `Authentication` section (`Authority`, `ValidAudiences`, `McpScopes`); the broker also needs `Mcp:OAuth:ClientId` and an Entra public client app (see [mcp.md](mcp.md#entra-setup-once-per-environment)).
````

Before:

````markdown
8. Optionally add the two MCP triggers and enable `Features.McpEndpoint`.
````

After:

````markdown
8. Optionally add the two MCP triggers (and `SafeMcpOAuth` for the OAuth broker) and enable `Features.McpEndpoint` (and set `Mcp:OAuth:ClientId`).
````

- [ ] **Step 7: Check the anchors**

Run: `Select-String docs\mcp.md, docs\consumer-migration.md, docs\api-endpoints.md -Pattern '\]\((mcp\.md)?#[a-z0-9-]+\)' -AllMatches | ForEach-Object { $_.Matches.Value } | Sort-Object -Unique`
Expected (every anchor is a heading in the target file):

```
](#authentication)
](#entra-setup-once-per-environment)
](#fallback-bearer-header-from-the-azure-cli)
](#mcp)
](#oauth-broker-for-entra-id)
](#security-notes)
](#staging-validation)
](mcp.md#enabling)
](mcp.md#entra-setup-once-per-environment)
](mcp.md#oauth-broker-for-entra-id)
```

- [ ] **Step 8: Commit**

```powershell
git add docs\mcp.md docs\api-endpoints.md docs\consumer-migration.md
git commit -m "feat(mcp-oauth): document the OAuth broker, the Entra setup and the consumer changes" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 9: Full verification and the final commit

**Files:** `docs\superpowers\plans\2026-11-22-mcp-oauth-broker.md` (this plan).

- [ ] **Step 1: Clean build and phase filter**

Run: `dotnet build SafeExchange.slnx --nologo -v q --no-incremental`
Expected: `704 Warning(s)`, `0 Error(s)` (the Task 0 count; no new warnings).

Run the phase filter.
Expected: `Passed!  - Failed:     0, Passed:   515, Skipped:     0, Total:   515, …`

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.Contract"`
Expected: `Passed!  - Failed:     0, Passed:   212, …` (the contract tests are untouched).

- [ ] **Step 2: Full suite against the Cosmos emulator**

The `SafeExchange.Tests` user secret `ConnectionStrings:CosmosDb` normally points at `https://localhost:8081`; the
emulator image below serves the HTTP gateway, so switch the secret to the CI value for the run and restore it after.

```powershell
docker run --detach --name saex-test-cosmos --publish 127.0.0.1:8081:8081 --publish 127.0.0.1:8080:8080 mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-EN20260907pre
while ((curl.exe -s -o NUL -w "%{http_code}" http://localhost:8080/ready) -ne '200') { Start-Sleep 5 }
dotnet user-secrets set "ConnectionStrings:CosmosDb" "AccountEndpoint=http://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==" --project SafeExchange.Tests
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo
dotnet user-secrets set "ConnectionStrings:CosmosDb" "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==" --project SafeExchange.Tests
docker rm -f saex-test-cosmos
```

Expected: `Passed!  - Failed:     0, Passed:  1250, Skipped:     0, Total:  1250, …` (1058 before this plan + 192).
If the emulator cannot run, skip this step and say so in the PR; everything in this plan's filters runs on EF InMemory.

- [ ] **Step 3: Review the branch**

Run: `git --no-pager diff --stat e833cf6 HEAD`
Expected: last line `49 files changed, 2767 insertions(+), 53 deletions(-)`; no file outside the File structure table.

- [ ] **Step 4: Commit this plan**

```powershell
git add docs\superpowers\plans\2026-11-22-mcp-oauth-broker.md
git commit -m "feat(mcp-oauth): implementation plan for the MCP OAuth broker" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

- [ ] **Step 5: After merge and deploy (operator, staging)** — run the [Entra setup](../../mcp.md) commands from
  `docs\mcp.md` for `safeexchange-staging.azurewebsites.net`, set `settings-mcp-oauthClientId` in
  `services-parameters-test.arm.json` (or the Key Vault secret), deploy, then run Copilot CLI
  `/mcp auth safeexchange-staging` end to end (spec §Testing) and the staging validation list in `docs\mcp.md`.

---

## Spec coverage

| Spec item | Where |
|---|---|
| PRM `resource` = MCP URL; `authorization_servers` = broker when configured, else Entra | Task 3 (`ToMetadata(Uri)`, `McpMetadataTests`, `McpProtectedResourceTests`) |
| AS metadata at `/api/.well-known/oauth-authorization-server` and `/openid-configuration`, exact JSON | Tasks 3, 4.1, 6 (`AuthorizationServerMetadataTests`, `Metadata_IsTheBrokersDocument_ForThisHost`) |
| Register: RFC 7591, configured `client_id`, `none`, loopback only | Task 4.1 (`McpOAuthBrokerRegisterTests`) |
| Loopback rule (incl. `localhost.evil.com`, `127.0.0.1.nip.io`, https, user-info, fragment, relative, > 2048) at register/authorize/callback | Tasks 1, 2, 4.1–4.3 |
| Client ID mismatch → `invalid_client`, no redirect | Task 4.2 (`Authorize_WithAnUnknownClient_…`), 4.4 (`invalid_client`) |
| `response_type=code`, S256 + challenge required; state passes; `resource`/unknown dropped; scope replaced | Task 4.2 |
| State wrapping ≤ 2 KB, validated on decode | Tasks 2, 4.2, 4.3 |
| Callback errors forwarded (only `error`, `error_description`, `state`), never HTML | Task 4.3 |
| Token: two grants only; broker sets `client_id`, `redirect_uri`, `scope`; drops `resource`, `client_secret`; fixed endpoint; relay + `no-store` | Tasks 4.4, 5, 6 |
| Logging: no codes, verifiers, tokens, refresh tokens or state | Tasks 5 (`TheEntraCall_LogsNoFormValueOrResponseBody`), 6 (`McpOAuthLoggingTests`) |
| Feature gating 404 (flag off or client ID empty) | Tasks 4.1–4.4 (`McpOAuthBrokerGatingTests`), 6 (`EveryRoute_IsABareNotFound_…`) |
| Middleware skip list by exact function name | Task 6 (`McpChallengeTests`) |
| Configuration `Mcp:OAuth:ClientId`, Key Vault `Mcp--OAuth--ClientId`, ARM `settings-mcp-oauthClientId` | Tasks 1, 7 |
| Entra setup (public client, callback redirect URI, required access, pre-authorisation) | Task 8 (`docs\mcp.md`), Task 9 Step 5 |
| REST contract tests stay green | Tasks 0, 3, 5, 6, 9 (212 unchanged) |

## Risks and follow-ups

- **Platform request telemetry.** The broker never logs codes or state, but the Functions host / Application Insights
  request telemetry records request URLs, so `/api/oauth/callback?code=…&state=…` (single-use, PKCE-bound, short-lived
  code) and `/api/oauth/authorize?…&state=…` can appear there. Follow-up option: `response_mode=form_post` for the
  Entra leg (code in a POST body), which needs the callback to accept POST.
- **Host-derived URLs.** Issuer, callback and `resource` come from the request host. Every public host name (e.g. a
  custom domain in addition to `*.azurewebsites.net`) needs its own redirect URI on the Entra client app, and a proxy
  that rewrites the host or scheme would publish wrong URLs (the existing challenge has the same dependency).
- **Entra behaviour not yet exercised end to end.** Unit tests fake Entra; the real host test only reached Entra's
  error path. Code redemption by a public client whose redirect URI is an https `publicClient` URI, and refresh, must
  be confirmed on staging (Task 9 Step 5). If Entra demands a secret (`AADSTS7000218`), check `isFallbackPublicClient`.
- **Client variety.** Clients that only try root-level `/.well-known/…` or need an issuer without a path cannot be
  served under the Functions `api` route prefix; they keep the header fallback.
- **Pre-authorisation write.** The documented Graph `PATCH` writes the API app's whole `api` object back; a concurrent
  edit of the API app could be lost — run it once, by one operator.
- **ARM.** The conditional secret means an empty parameter never clears a hand-set value; turning the broker off
  requires disabling the secret or the flag (documented).
