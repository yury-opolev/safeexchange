# P2 — Fix This Quarter

> See [`SUMMARY.md`](SUMMARY.md) for methodology and rubric.

## [P2] [MEDIUM] Authentication middleware echoes JWT validation errors to unauthenticated caller

- **Category:** A02:2025 (also A07, A09)
- **CWE:** CWE-209
- **File:** `SafeExchange.Core/Middleware/DefaultAuthenticationMiddleware.cs:76-87`
- **Severity:** Medium · **Exploitability:** Trivial · **Exposure:** Internet (pre-auth) · **Confidence:** Confirmed · **Priority:** P2

### Description

Catches `ArgumentException` + `SecurityTokenException` from `JwtSecurityTokenHandler.ValidateToken` and writes `exception.Message` to the 401 body. `Microsoft.IdentityModel` error messages expose internal validation state: `IDX10205: Issuer validation failed. Issuer: '<attacker>' Did not match validationParameters.ValidIssuer: '<internal>'`, `IDX10214: Audience validation failed ...`, `IDX10223: Lifetime validation failed ... ValidFrom: '…', ValidTo: '…'`. Unauthenticated attackers enumerate the configured audiences, issuers, tenant IDs, clock skew, and signing-key rotation state by submitting crafted tokens in a loop.

### Recommendation

Return fixed `"Invalid or expired token."`; log detail server-side.

```csharp
catch (Exception ex) when (ex is ArgumentException or SecurityTokenException)
{
    this.log.LogWarning(ex, "Token validation exception");
    await UnauthorizedAsync(context, httpRequestData, "Invalid or expired token.");
    return;
}
```

---

## [P2] [MEDIUM] `TokenValidationParametersProvider` does not set `ValidAlgorithms` allowlist

- **Category:** A07:2025 — Authentication Failures
- **CWE:** CWE-347, CWE-327
- **File:** `SafeExchange.Core/Utilities/TokenValidationParametersProvider.cs:32-39`
- **Severity:** Medium · **Exploitability:** Hard (depends on IDX hardening) · **Exposure:** Internet · **Confidence:** Medium · **Priority:** P2

### Description

`TokenValidationParameters` does not set `ValidAlgorithms`. Current `Microsoft.IdentityModel.Tokens 8.16.0` rejects `none` and enforces reasonable defaults, but an explicit allowlist `ValidAlgorithms = ["RS256"]` is the documented defensive-in-depth posture and prevents algorithm-confusion attack classes the moment the underlying library weakens a default or a new class (e.g., `HS256` with a public key as the HMAC secret) is introduced. Also note `ValidateIssuerSigningKey` and `ValidateLifetime` are not explicitly set — they default to `true` in the SDK, so currently safe, but this is fragile.

### Recommendation

```csharp
this.tokenValidationParameters = new TokenValidationParameters
{
    RequireExpirationTime      = true,
    RequireSignedTokens        = true,
    ValidateIssuerSigningKey   = true,   // explicit, don't rely on default
    ValidateLifetime           = true,   // explicit
    ValidAlgorithms            = new[] { SecurityAlgorithms.RsaSha256 }, // allowlist
    ValidateAudience           = authConfig.ValidateAudience,
    ValidateIssuer             = authConfig.ValidateIssuer,
};
```

---

## [P2] [MEDIUM] Microsoft Graph `$search` injection via unsanitized user input

- **Category:** A05:2025 — Injection
- **CWE:** CWE-943 (Improper Neutralization of Special Elements in Data Query Logic), CWE-89 (adjacent), CWE-90
- **File:** `SafeExchange.Core/Graph/GraphDataProvider.cs:158-162` (`TryFindUsersAsync`), `:220-223` (`TryFindGroupsAsync`); reached from `SafeExchangeUserSearch.cs:95` and `SafeExchangeGroupSearch.cs` without sanitization or length limit
- **Severity:** Medium · **Exploitability:** Easy · **Exposure:** Authenticated-user · **Confidence:** High · **Priority:** P2

### Description

`searchString` from the user's request body is interpolated directly into a Microsoft Graph `$search` parameter:

```csharp
requestConfiguration.QueryParameters.Search =
    string.Join(" OR ",
        $"\"displayName:{searchString}\"",
        $"\"userPrincipalName:{searchString}\"",
        $"\"mail:{searchString}\"");
```

An attacker supplying `searchString = foo" OR "accountEnabled:true` breaks out of the enclosing quotes and injects additional Graph search terms:

```
"displayName:foo" OR "accountEnabled:true" OR "userPrincipalName:foo" OR "accountEnabled:true" OR "mail:foo" OR "accountEnabled:true"
```

Because the Graph request runs on the user's own OBO token, the attacker cannot escalate beyond their existing `User.ReadBasic.All` scope, but they can:

1. Bypass any logical search-result restriction the application imposes (e.g., enumerate **every** user in the tenant rather than the intended prefix match).
2. Craft queries that cause Graph to return fields not normally surfaced via this endpoint.
3. Cause the Graph request to fail with an error that is then echoed back in the 500 body (compounding the P1 error-echo finding).

### Attack scenario

Attacker POSTs `{"searchString":"x\" OR \"accountEnabled:true"}` to `/api/user-search`. Backend constructs the injected `$search`. Graph returns the full set of enabled users in the tenant (bounded only by `top=100`), leaking the directory to any authenticated caller.

### Recommendation

Reject any `searchString` containing `"`, newline, or non-alphanumeric-plus-whitespace characters; or URL-encode and escape embedded quotes; or prefer `$filter` with `startsWith(displayName, @q)` and a parameter binding.

```csharp
if (searchString.Any(c => c == '"' || c == '\n' || c == '\r' || char.IsControl(c)))
{
    throw new ArgumentException("Invalid characters in searchString.");
}
if (searchString.Length > 64)
{
    throw new ArgumentException("searchString too long.");
}
```

---

## [P2] [MEDIUM] SSRF via admin-registered webhook URL (blind, reachable from internal network)

- **Category:** A01:2025 — Broken Access Control (SSRF class, new in 2025)
- **CWE:** CWE-918
- **Files:**
  - `SafeExchange.Core/Functions/SafeExchangeProcessExternalNotification.cs:107-113` (makes the call)
  - `SafeExchange.Core/Functions/Admin/SafeExchangeWebhookSubscriptions.cs:139-145` (stores the URL with no validation)
- **Severity:** Medium · **Exploitability:** Moderate · **Exposure:** Privileged-user (admin) · **Confidence:** Confirmed · **Priority:** P2

### Description

Admins register webhook subscriptions with a `Url` field; the only validation is "not empty". On access-request creation, `SafeExchangeProcessExternalNotification` loads the webhook row and executes `httpClient.PostAsync(webhookSubscription.Url, content)` with a default `IHttpClientFactory` client (no timeout, no redirect limit, no scheme allowlist). A malicious admin (or an admin whose credentials are compromised) can register URLs pointing at:

- `http://169.254.169.254/metadata/...` — Azure IMDS (the current code does not set `Metadata: true`, so the IMDS endpoint rejects the call, but other `169.254.*` metadata variants or legacy endpoints may respond).
- `http://10.x.y.z/admin/…` — private VNet addresses once the Function App is in a VNet.
- `http://127.0.0.1:<port>/…` — local Kudu / sidecar.
- `gopher://` or redirect chains via `HttpClient` with default `AllowAutoRedirect=true`.

The response body is not surfaced to the admin directly, so this is a **blind** SSRF. Still dangerous because (a) it can trigger state-changing internal APIs; (b) it leaks DNS timing / reachability; (c) the `log.LogInformation` at line 113 reveals status codes that enable oracle-style enumeration of internal hosts.

### Recommendation

1. **At registration time** (`SafeExchangeWebhookSubscriptions.cs:139`), validate the URL:
   ```csharp
   if (!Uri.TryCreate(creationInput.Url, UriKind.Absolute, out var parsed) ||
       parsed.Scheme != Uri.UriSchemeHttps ||
       parsed.IsLoopback)
   {
       return BadRequest("URL must be absolute HTTPS and must not be a loopback address.");
   }
   var addresses = await Dns.GetHostAddressesAsync(parsed.Host);
   if (addresses.Any(IsPrivateOrLinkLocal))
   {
       return BadRequest("URL resolves to a private or link-local address.");
   }
   ```
2. **At delivery time** (`SafeExchangeProcessExternalNotification.cs:107`), harden the `HttpClient`:
   ```csharp
   var handler = new SocketsHttpHandler
   {
       AllowAutoRedirect = false,
       ConnectTimeout = TimeSpan.FromSeconds(5),
   };
   using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
   ```
3. Wrap all outbound calls in an **egress proxy** that enforces the same allowlist at the network layer (VNet service endpoint + NSG).

---

## [P2] [MEDIUM] Admin-group filter missing explicit user-token check

- **Category:** A01:2025 (latent), A06 (design)
- **CWE:** CWE-284, CWE-285
- **File:** `SafeExchange.Core/Requests/Filters/AdminGroupFilter.cs:54-102`
- **Severity:** Medium · **Exploitability:** Hard · **Exposure:** Privileged · **Confidence:** Medium · **Priority:** P2

### Description

`AdminGroupFilter.GetFilterResultAsync` does not call `tokenHelper.IsUserToken(principal)`. It calls `GetUpn` + `GetObjectId`. If an administrator adds an *application's* ObjectId to `AdminConfiguration.AdminUsers` (not typical but supported by the free-form config), that application token passes the admin filter. Combined with the missing-return bugs in `SafeExchangeAdminOperations.cs:49-54` and peers, an application token could reach admin operations including `add_kek_version` and `run_dbmigration` — i.e., **crypto key rotation and schema migration triggered by an application principal**.

### Recommendation

```csharp
public async ValueTask<(bool shouldReturn, HttpResponseData? response)> GetFilterResultAsync(...)
{
    if (!this.tokenHelper.IsUserToken(principal))
    {
        return (shouldReturn: true, response: await ActionResults.CreateResponseAsync(
            req, HttpStatusCode.Forbidden,
            new BaseResponseObject<object> { Status = "forbidden", Error = "Admin APIs are user-only." }));
    }
    // ... existing logic
}
```

Fix the P4 missing-return bugs independently.

---

## [P2] [MEDIUM] Legacy ARM template exposes Cosmos / Key Vault / Storage to the public internet

- **Category:** A02:2025
- **CWE:** CWE-284, CWE-732
- **File:** `deployment/legacy/arm/services-template.arm.json`
  - Cosmos line 343 — `publicNetworkAccess: "Enabled"`, `ipRules: []`, `isVirtualNetworkFilterEnabled: false`
  - Key Vault line 431 — `publicNetworkAccess: "Enabled"` (unconditional)
  - Storage lines 601/631/658/667 — `networkAcls.defaultAction: "Allow"`, `publicNetworkAccess: "Enabled"`
  - App Insights / Log Analytics lines 406-407 — `publicNetworkAccessForIngestion: Enabled`, `publicNetworkAccessForQuery: Enabled`
- **Severity:** Medium (Info if legacy template truly not deployed) · **Exploitability:** Easy (network) / Hard (still needs AAD) · **Exposure:** Internet · **Confidence:** Confirmed · **Priority:** P2

### Description

The legacy ARM template exposes every data-plane resource to the internet with no IP allowlist or VNet filter. Mitigations present: Cosmos has `disableLocalAuth: true`, the data and webjobs storage accounts set `allowSharedKeyAccess: false`, and `allowBlobPublicAccess: false`. This blocks anonymous data access, but any CVE in the Azure SDK auth path, any leaked PIM-backed AAD identity, or any SSRF pivot lands directly on the data plane with no network tier to traverse. App Insights exposure additionally allows any internet caller with an instrumentation key to send telemetry (billing abuse + log poisoning).

The **`current/` ARM template** mitigates this: `publicNetworkAccess` is gated by `has_debug_ip` and Storage `networkAcls.defaultAction: "Deny"`. The legacy template is still committed to main and appears to be the deployment path for older environments.

### Recommendation

Delete `deployment/legacy/` if not deployed; otherwise port the `has_debug_ip` / `defaultAction: Deny` pattern from `deployment/current/` to `legacy/`, or add private endpoints + `publicNetworkAccess: Disabled`.

---

## [P2] [MEDIUM] No automated dependency vulnerability scanning or update process

- **Category:** A03:2025 — Software Supply Chain Failures
- **CWE:** CWE-1104 (Use of Unmaintained Third Party Components), CWE-1357
- **File:** absent — `.github/dependabot.yml`, `.github/workflows/*.yml` (the `.github/workflows/` directory is empty), no `renovate.json`
- **Severity:** High (process) · **Exploitability:** Hard (passive) · **Exposure:** Internet · **Confidence:** Confirmed · **Priority:** P2

### Description

No Dependabot, no Renovate, no CI workflow, no SCA, no `dotnet list package --vulnerable` step. For a production credential-sharing backend, new CVEs in any of `Microsoft.IdentityModel.*`, `Azure.Identity`, `Microsoft.Graph`, `Microsoft.EntityFrameworkCore.Cosmos` are discoverable only by manual audit.

Current package versions are **clean as of this review**:

- `Microsoft.IdentityModel.* 8.16.0` — well beyond CVE-2024-21319 (fixed 7.1.2/6.34.0) and CVE-2024-30105
- `Azure.Identity 1.19.0` — beyond CVE-2024-29992 (1.11.0) and CVE-2024-35255 (1.11.4)
- `Microsoft.Graph 5.103.0` — current
- `Microsoft.EntityFrameworkCore.* 10.0.5` — current
- No `Newtonsoft.Json`, `System.Drawing.Common`, `YamlDotNet`, `SharpZipLib`, `MSAL.NET` in scope

The process gap is the real finding.

### Recommendation

1. Add `.github/dependabot.yml` with a weekly `nuget` ecosystem schedule.
2. Add a CI workflow that runs `dotnet list package --vulnerable --include-transitive` and fails the build on any High/Critical hit:
   ```yaml
   - name: Check vulnerable packages
     run: |
       dotnet restore
       OUTPUT=$(dotnet list package --vulnerable --include-transitive)
       echo "$OUTPUT"
       echo "$OUTPUT" | grep -E "(Critical|High)" && exit 1 || exit 0
   ```
3. Enable GitHub native code scanning / Dependabot alerts at minimum.

---

## [P2] [MEDIUM] `DefaultJsonSerializer` / Cosmos configuration has no explicit hardening of polymorphic deserialization

- **Category:** A08:2025 (adjacent)
- **CWE:** CWE-502
- **File:** `SafeExchange.Core/Utilities/DefaultJsonSerializer.cs`, `SafeExchangeStartup.cs:94-98`
- **Severity:** Medium · **Exploitability:** Hard · **Exposure:** Internet (via JSON bodies) and queue-trigger inputs · **Confidence:** Medium · **Priority:** P2

### Description

The project uses `System.Text.Json` (good — no `Newtonsoft.Json` with `TypeNameHandling`). However, polymorphic deserialization via `[JsonPolymorphic]`/`[JsonDerivedType]` is allowed by default, and the `JsonSerializerOptions` configuration only sets `PropertyNamingPolicy` + `DefaultIgnoreCondition`. The defense-in-depth posture is to explicitly set `TypeInfoResolver` to a source-generated resolver and reject unknown properties so that any future DTO added with `$type` discriminators cannot become a deserialization gadget. Queue-trigger payloads (`WebhookNotificationTaskPayload` at `SafeExchangeProcessExternalNotification.cs:42`) are deserialized from Azure Queue Storage without schema validation; an attacker with queue-write access (storage-key leak) could inject crafted payloads.

### Recommendation

Switch to source-generated `JsonSerializerContext`; explicitly set `options.UnknownTypeHandling = JsonUnknownTypeHandling.JsonElement`; validate queue-message fields against a schema before use.

```csharp
[JsonSerializable(typeof(WebhookNotificationTaskPayload))]
[JsonSerializable(typeof(ApplicationRegistrationInput))]
// ... one line per DTO
internal partial class SafeExchangeJsonContext : JsonSerializerContext { }

services.Configure<JsonSerializerOptions>(options =>
{
    options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.TypeInfoResolver = SafeExchangeJsonContext.Default;
    options.UnknownTypeHandling = JsonUnknownTypeHandling.JsonElement;
});
```

---

## [P2] [MEDIUM] Case-sensitive DisplayName duplicate prevention can desync permissions key

- **Category:** A06:2025
- **CWE:** CWE-178 (Improper Handling of Case Sensitivity)
- **File:** `SafeExchange.Core/Functions/Admin/SafeExchangeApplications.cs:82` (registration dup check), `SubjectHelper.cs:43`
- **Severity:** Medium · **Exploitability:** Hard · **Exposure:** Privileged-user · **Confidence:** Medium · **Priority:** P2

### Description

Duplicate detection on `Applications.DisplayName` uses EF Core LINQ `.Equals(applicationId)`, which translates to case-sensitive comparison in Cosmos. An admin can register `"NotificationBot"` and `"notificationbot"` as distinct rows. The permissions table stores whatever case the admin typed. A subsequent grant to `"NotificationBot"` applies only to one row; the other keeps shadow permissions. Not directly exploitable without admin cooperation, but brittle — and removed entirely if the P1 identity-as-`oid` / `(tenant,client)` fix lands.

### Recommendation

Normalize `DisplayName` to lowercase (or `Normalize`-casefold) in both the registration path and the permissions join; add a unique index. Or remove the dependency on `DisplayName` entirely by adopting the P1 identity fix.
