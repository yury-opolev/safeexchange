# P1 — Fix This Sprint

> See [`SUMMARY.md`](SUMMARY.md) for methodology and rubric.

## [P1] [HIGH] Token validation accepts any audience / issuer when config key is missing

- **Category:** A02:2025 — Security Misconfiguration (also A07)
- **CWE:** CWE-1188 (Insecure Default Initialization), CWE-453, CWE-347
- **File:** `SafeExchange.Core/Configuration/AuthenticationConfiguration.cs:13,17`; consumed at `SafeExchange.Core/Utilities/TokenValidationParametersProvider.cs:32-51`
- **Severity:** High
- **Exploitability:** Trivial (operator error)
- **Exposure:** Internet
- **Confidence:** Confirmed
- **Priority:** **P1**

### Description

`AuthenticationConfiguration.ValidateAudience` and `.ValidateIssuer` are plain `bool` properties, so their C# default is `false`. The current ARM template wires them to `"True"` (Key Vault–backed), but one missing secret, one typo, or one fresh environment provisioned without those secrets **silently disables audience + issuer validation** for every incoming JWT. With signature validation alone, any token signed by the `login.microsoftonline.com/common` OpenID config's signing keys is accepted — meaning **any user in any Entra tenant can authenticate to this SafeExchange instance**. No runtime warning is emitted; no integration test covers the failure mode.

### Evidence

```csharp
// AuthenticationConfiguration.cs
public class AuthenticationConfiguration
{
    public bool ValidateAudience { get; set; }          // defaults to false
    public string ValidAudiences { get; set; }
    public bool ValidateIssuer { get; set; }            // defaults to false
    public string ValidIssuers { get; set; }
    public string MetadataAddress { get; set; }
}

// TokenValidationParametersProvider.cs:32-51
this.tokenValidationParameters = new TokenValidationParameters
{
    RequireExpirationTime = true,
    RequireSignedTokens = true,
    ValidateAudience = authConfig.ValidateAudience,   // silently false if config missing
    ValidateIssuer   = authConfig.ValidateIssuer,     // silently false if config missing
};
this.tokenValidationParameters.EnableAadSigningKeyIssuerValidation();

if (authConfig.ValidateAudience)
{
    this.tokenValidationParameters.ValidAudiences = authConfig.ValidAudiences.Split(',', ...);
}
if (authConfig.ValidateIssuer)
{
    this.tokenValidationParameters.ValidIssuers = authConfig.ValidIssuers.Split(',', ...);
}
```

### Attack scenario

Attacker notices (or guesses) that a particular tenant has `Authentication__ValidateIssuer` absent from the Function App configuration. Attacker authenticates against their own Entra tenant, obtains an access token for any MS-issued audience (e.g., Microsoft Graph), replays it to the SafeExchange endpoint. Token signature validates (AAD common signing keys), audience is ignored, issuer is ignored → principal populated → attacker's `TokenMiddlewareCore.GetOrCreateUserAsync` creates a new `User` row. Attacker proceeds to the standard per-secret authorization checks.

Combined with the [P0 finding](P0-critical.md), the attacker immediately reaches `SafeExchangeExternalNotificationDetails` on any known notification GUID and can enumerate recipient UPNs / tombstone access-request workflows.

### Recommendation

1. Default both flags to `true` in source:
   ```csharp
   public bool ValidateAudience { get; set; } = true;
   public bool ValidateIssuer   { get; set; } = true;
   ```
2. Add a startup assertion in `TokenValidationParametersProvider`:
   ```csharp
   if (authConfig.ValidateIssuer && string.IsNullOrWhiteSpace(authConfig.ValidIssuers))
   {
       throw new ConfigurationErrorsException(
           "ValidateIssuer=true but no ValidIssuers configured. Refusing to start.");
   }
   if (!authConfig.ValidateAudience || !authConfig.ValidateIssuer)
   {
       this.log.LogWarning(
           "AAD audience/issuer validation is DISABLED. This is unsafe in production.");
   }
   ```
3. Add an integration test asserting a token from a different tenant is rejected.

### References

- <https://owasp.org/Top10/2025/A02_2025-Security_Misconfiguration/>
- <https://cwe.mitre.org/data/definitions/1188.html>
- <https://cwe.mitre.org/data/definitions/347.html>

---

## [P1] [HIGH] Exception type + message echoed in 500 responses (mass information disclosure)

- **Category:** A02:2025 — Security Misconfiguration
- **CWE:** CWE-209 (Information Exposure Through Error Message), CWE-497
- **Severity:** High
- **Exploitability:** Easy
- **Exposure:** Internet (post-auth; the pre-auth `DefaultAuthenticationMiddleware:76-87` also echoes `Microsoft.IdentityModel` error messages)
- **Confidence:** Confirmed
- **Priority:** **P1**

### Affected files (17 handlers)

- `SafeExchange.Core/Functions/SafeExchangeAccess.cs:276`
- `SafeExchange.Core/Functions/SafeExchangeAccessRequest.cs:420-426`
- `SafeExchange.Core/Functions/SafeExchangeSecretContentMeta.cs:463`
- `SafeExchange.Core/Functions/SafeExchangeSecretMeta.cs:367`
- `SafeExchange.Core/Functions/SafeExchangeSecretStream.cs:513`
- `SafeExchange.Core/Functions/SafeExchangeUserSearch.cs:125`
- `SafeExchange.Core/Functions/SafeExchangeGroupsList.cs:89`
- `SafeExchange.Core/Functions/SafeExchangeGroupSearch.cs:125`
- `SafeExchange.Core/Functions/SafeExchangePinnedGroups.cs:280`
- `SafeExchange.Core/Functions/SafeExchangePinnedGroupsList.cs:104`
- `SafeExchange.Core/Functions/SafeExchangeApplicationsList.cs:86`
- `SafeExchange.Core/Functions/SafeExchangeExternalNotificationDetails.cs:163`
- `SafeExchange.Core/Functions/Admin/SafeExchangeAdminGroups.cs:209`
- `SafeExchange.Core/Functions/Admin/SafeExchangeAdminOperations.cs:138`
- `SafeExchange.Core/Functions/Admin/SafeExchangeApplications.cs:331`
- `SafeExchange.Core/Functions/Admin/SafeExchangeWebhookSubscriptions.cs:322`
- `SafeExchange.Core/Functions/Admin/SafeExchangeWebhookSubscriptionsList.cs:86`
- `SafeExchange.Core/Middleware/DefaultAuthenticationMiddleware.cs:76-87` (pre-auth JWT validation error echo)

### Description

Every top-level function handler catches `Exception` and returns `$"{ex.GetType()}: {ex.Message}"` in the JSON body of a 500. Exceptions from `Microsoft.EntityFrameworkCore.Cosmos`, `Azure.Storage.Blobs`, `Microsoft.Graph`, and `Azure.Identity` leak internal state: Cosmos endpoint hostnames, container / database names, partition keys, Graph query fragments, Azure request IDs, storage account names, and fragments of inner messages that can identify framework versions for targeted CVE exploitation. Additionally, `DefaultAuthenticationMiddleware:76-87` echoes JWT validation errors (`IDX10205: Issuer validation failed ... Did not match validationParameters.ValidIssuer: '<internal>'`) to the **unauthenticated** caller — a free enumeration oracle for audience / issuer / signing-key rotation state.

### Evidence

```csharp
// Representative pattern repeated in every top-level handler
catch (Exception ex)
{
    log.LogWarning(ex, $"Exception in {actionName}: {ex.GetType()}: {ex.Message}");
    return await ActionResults.CreateResponseAsync(
        request, HttpStatusCode.InternalServerError,
        new BaseResponseObject<object>
        {
            Status = "error",
            Error = $"{ex.GetType()}: {ex.Message}"      // ← leaks framework + state
        });
}
```

### Attack scenario

Attacker probes endpoints with malformed payloads (oversized chunks, invalid GUIDs, bad partition keys). The 500 response body reveals backend topology:

```
Microsoft.Azure.Cosmos.CosmosException: Operation returned an invalid status
code 'Forbidden' ActivityId: ... endpoint=https://safeexchange-prod.documents.azure.com
containerName=Objects partitionKey='/tenant'
```

Attacker maps the Cosmos database, identifies framework versions, and narrows CVE targeting. Against the unauth middleware, the attacker iterates token shapes to enumerate the configured `ValidIssuers` / `ValidAudiences` via the error messages — a standalone recon bug even without any downstream auth weakness.

### Recommendation

Return a generic error with a correlation ID; log the detail server-side with the same ID:

```csharp
catch (Exception ex)
{
    var correlationId = Guid.NewGuid().ToString("N");
    log.LogError(ex, "Exception in {Action}, correlationId={CorrelationId}", actionName, correlationId);
    return await ActionResults.CreateResponseAsync(
        request, HttpStatusCode.InternalServerError,
        new BaseResponseObject<object>
        {
            Status = "error",
            SubStatus = "internal_exception",
            Error = $"Internal error. Reference: {correlationId}"
        });
}
```

Fix `DefaultAuthenticationMiddleware` to return a fixed `"Invalid or expired token."` instead of `exception.Message`. Log the exception details server-side only.

### References

- <https://owasp.org/Top10/2025/A02_2025-Security_Misconfiguration/>
- <https://cwe.mitre.org/data/definitions/209.html>

---

## [P1] [HIGH] Stored-subject identity uses mutable UPN / DisplayName, enabling identity rebinding

- **Category:** A06:2025 — Insecure Design (foundational)
- **CWE:** CWE-287, CWE-302, CWE-640
- **Files:**
  - `SafeExchange.Core/Utilities/SubjectHelper.cs:13-23` — returns `(SubjectType.User, tokenHelper.GetUpn(principal))`
  - `SafeExchange.Core/Utilities/TokenHelper.cs:47-71` — `GetUpn` falls back through `upn` → `preferred_username` → `email` → `ClaimTypes.Email`
  - `SafeExchange.Core/Permissions/PermissionsManager.cs:180` — permission row keyed on this subject ID
  - `SafeExchange.Core/Requests/Filters/AdminGroupFilter.cs:76,88`
  - `SafeExchange.Core/Requests/Filters/GlobalAccessFilter.cs:57`
- **Severity:** High
- **Exploitability:** Hard (requires Entra tenant admin cooperation or account lifecycle event)
- **Exposure:** Authenticated-user
- **Confidence:** High
- **Priority:** **P1**

### Description

The authorization system keys user identity on **UPN** (a mutable string that tenant admins can rename at will) rather than on the immutable `oid` (object identifier) claim that `TokenHelper.GetObjectId` already exposes — but which is only used for admin-user membership tests. Worse, the `GetUpn` fallback chain walks through `preferred_username` and `email` — fields that are **not unique across an Entra tenant** (aliases, external mail, federation edge cases). Application identity has a parallel problem: `SubjectHelper` uses `Application.DisplayName` as the subject ID, and while `HandleApplicationUpdate` does not currently allow renaming the display name, the permissions table is keyed on a field that is not the app's stable identity (`AadTenantId + AadClientId` is the real identity).

### Evidence

```csharp
// SubjectHelper.cs:13-23
public static async Task<(SubjectType type, string subjectId)> GetSubjectInfoAsync(
    ITokenHelper tokenHelper, ClaimsPrincipal principal, SafeExchangeDbContext dbContext)
{
    if (tokenHelper.IsUserToken(principal))
    {
        return (SubjectType.User, tokenHelper.GetUpn(principal));   // ← UPN, not OID
    }

    var displayName = await GetApplicationDisplayNameAsync(
        tokenHelper.GetTenantId(principal),
        tokenHelper.GetApplicationClientId(principal),
        dbContext);
    return (SubjectType.Application, displayName);                  // ← DisplayName, not ClientId
}

// TokenHelper.cs:47-71 — the fallback chain
public string GetUpn(ClaimsPrincipal principal)
{
    var result = principal.FindFirst(ClaimTypes.Upn)?.Value;
    if (string.IsNullOrEmpty(result)) result = principal.FindFirst("upn")?.Value;
    if (string.IsNullOrEmpty(result)) result = principal.FindFirst("preferred_username")?.Value;
    if (string.IsNullOrEmpty(result)) result = principal.FindFirst("email")?.Value;
    if (string.IsNullOrEmpty(result)) result = principal.FindFirst(ClaimTypes.Email)?.Value;
    return result ?? string.Empty;
}
```

### Attack scenario (user side)

1. Alice shares a secret with `bob@contoso.com` → permissions row: `SubjectType=User, SubjectId="bob@contoso.com"`.
2. Tenant admin renames Bob's UPN to `bob.v2@contoso.com` (common during M&A, marriage, surname change). The permission row is now stranded — `bob.v2@contoso.com` has no access.
3. Admin later onboards a new employee with UPN `bob@contoso.com` (not unusual in tenants that reuse names after the disable-grace-period).
4. **The new employee immediately inherits read access to Alice's secret**, because `PermissionsManager.HasPermissionAsync` joins on `SubjectId=UPN`. No log entry is produced to signal the identity rebinding.

### Attack scenario (app side)

1. Admin registers `AppA` with DisplayName `"NotificationBot"`. Permissions are granted to `Application/NotificationBot`.
2. Admin deletes `AppA`.
3. Admin registers `AppB` with DisplayName `"NotificationBot"` (different AAD client ID).
4. `AppB` inherits `AppA`'s permissions silently.

### Recommendation

1. Change the subject ID for users to `tokenHelper.GetObjectId(principal)` (the `oid` claim). Migrate `SubjectPermissions.SubjectId` to `oid`. Optionally keep UPN as a `SubjectDisplayName` column for UI.
2. Change the subject ID for applications to `{AadTenantId}/{AadClientId}`.
3. Write a DB migration that rewrites existing permission rows (join on current UPN → look up `User.AadObjectId`, rewrite key; similar for apps using `AadClientId`).
4. Publish the rename plan in release notes. Because this touches the permissions table, coordinate with any external consumers that may reference the old subject IDs.

### References

- <https://owasp.org/Top10/2025/A06_2025-Insecure_Design/>
- <https://cwe.mitre.org/data/definitions/287.html>
- <https://cwe.mitre.org/data/definitions/302.html>
- <https://cwe.mitre.org/data/definitions/640.html>
