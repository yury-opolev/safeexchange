# S2S cross-tenant access + application access-picker — design

Date: 2026-06-28
Status: Approved (brainstormed with the owner)
Repos: `safeexchange` (backend, Azure Functions) and `safeexchange.blazorpwa` (Blazor WASM PWA + AdminPanel)
Branch (both repos): `feature/s2s-cross-tenant-and-picker`

## Problem

A SafeExchange instance is configured to accept **user** tokens from a single
"home" tenant (the customers/consumers tenant). A user from that tenant wants to
run a **headless daemon** that authenticates to SafeExchange programmatically as
a registered **S2S application**.

Two things block this today:

1. **Token validation is single-tenant.** `DefaultAuthenticationMiddleware`
   validates every JWT against a single static `TokenValidationParameters` built
   from `Authentication:ValidIssuers` + `MetadataAddress`, both pinned to one
   tenant. A daemon token issued by the developer's **own** tenant fails issuer
   validation (`401`, IDX10205) *before* the `(clientId, tenantId)` DB
   registration in `TokenMiddlewareCore` is ever consulted. The personal/consumers
   authority cannot mint client-credentials tokens at all, so a daemon **must**
   use a real org tenant — which the instance currently rejects.

2. **The registration UI cannot set a tenant.** `RegisterS2SApp.razor` collects
   only display name + client id; the backend self-service endpoint already
   accepts an optional `AadTenantId` but the form never sends one, so the app is
   silently registered under the caller's sign-in tenant.

Separately, when a user grants a secret's access to an application, the access
editor offers a **dropdown** of all registered apps. The owner wants this to be a
**search modal** (magnifying-glass icon) like the user/group pickers, searchable
by name with client/tenant id shown for disambiguation.

## Decision (owner's steer)

- **Do NOT widen issuer trust to all tenants.** Keep user-token validation pinned
  to the configured home tenant. Add a **fixed, configuration-time allowlist of
  tenants from which app-only (S2S) tokens are accepted**. The registration UI
  lets the user pick a tenant **from that list only** — never an arbitrary tenant.
- **Replace the application dropdown with a search modal** backed by a new search
  endpoint; all users can pick any previously-registered app by name.

## Non-goals

- No change to how user tokens are validated (home tenant only — unchanged).
- No Microsoft Graph lookup for applications; application search is over the
  locally-registered `Applications` only.
- No automatic Entra app provisioning — operators do the Entra setup (below).

## Backend design (`safeexchange`)

### 1. S2S tenant allowlist configuration

New property on `AuthenticationConfiguration` (`Authentication` config section):

```
Authentication:S2SAllowedTenants   (string, JSON array; default "" = feature off)
```

Value is a JSON array of `{ "tenantId": "<guid>", "displayName": "<label>" }`.
Stored as a single Key Vault secret `Authentication--S2SAllowedTenants` so it
fits the existing flat-settings deployment model. A pure, unit-tested parser
(`S2SAllowedTenant.ParseList`) turns it into `IReadOnlyList<S2SAllowedTenant>`;
malformed entries are skipped with a warning, non-GUID tenant ids rejected.

`displayName` defaults to the tenant id when absent. Empty / missing config ⇒
empty list ⇒ **feature is off and behavior is byte-for-byte identical to today**.

### 2. Token-kind-aware issuer validation

In `TokenValidationParametersProvider`:

- Keep `ValidateIssuer = true`, `ValidAudiences`, lifetime, signed-token,
  algorithm allowlist — unchanged.
- When the allowlist is **empty**: keep the current behavior exactly, including
  `EnableAadSigningKeyIssuerValidation()`. Zero regression for existing
  instances (regular-tenant or customers-tenant) that don't use the feature.
- When the allowlist is **non-empty**: install a custom `IssuerValidator`
  delegate:
  - **User tokens** (delegated; carry `scp`/`scope`): issuer must be one of the
    configured `ValidIssuers` (home tenant) — unchanged restriction.
  - **App-only tokens** (client-credentials; carry `appid`+`appidacr` or
    `azp`+`azpacr`, no scope): issuer accepted if it is a configured issuer **or**
    one derived from an allowlisted tenant — i.e. `https://login.microsoftonline.com/{tid}/v2.0`
    or `https://sts.windows.net/{tid}/`.
  - Otherwise → `SecurityTokenInvalidIssuerException` (surfaced as the generic
    `401`, never echoing IDX text — existing CWE-209 hardening preserved).

Token kind is classified by a shared, unit-tested helper
(`TokenClassification.IsAppOnlyToken`) that `TokenHelper` is refactored to use
too, so the auth layer and the validator can't drift.

**Signing keys / security rationale.** Azure AD signs every worldwide-cloud
tenant's tokens with the same published key set, and the `iss`/`tid` claims are
themselves signed (tamper-proof). The existing `MetadataAddress` keys therefore
validate the *signature* of an allowlisted-tenant token; the **signed issuer
allowlist is the tenant gate**. `EnableAadSigningKeyIssuerValidation()` binds the
key to the single configured issuer and would reject allowlisted tenants, so it
is not applied when the allowlist is in use; its protection (relevant only to
`common`/multi-tenant apps that trust tenant-supplied keys) is not meaningful for
Microsoft-managed worldwide keys under a strict signed-issuer allowlist. Audience
validation and the `(clientId, tenantId)` DB-registration gate remain in force as
defense in depth. This trade-off is called out explicitly for security review.

### 3. Allowed-tenants endpoint (for the registration UI)

`GET /v2/s2sapps/allowed-tenants` — authenticated user; gated by
`Features.S2SAppsSelfService` like the other `s2sapps` routes. Returns the
`{ tenantId, displayName }` list (empty when the feature is off, so the UI can
hide/disable the tenant control). Reads the parsed allowlist via
`IOptionsMonitor<AuthenticationConfiguration>` (newly registered in DI).

### 4. Tenant-constrained registration

In `SafeExchangeS2SApps.RunRegister`:

- When the allowlist is **non-empty**: the resolved `AadTenantId` must be a
  member of the allowlist; otherwise `400` with a clear message. (The UI only
  offers allowlisted tenants; this is the server-side enforcement.)
- When the allowlist is **empty**: unchanged (defaults to caller's home tenant).

The admin path (`POST /v2/applications/{id}`) is left flexible; the issuer
allowlist remains the hard security boundary (an app registered for a
non-allowlisted tenant simply can't authenticate).

### 5. Application search endpoint

`POST /v2/application-search` — mirrors `user-search`/`group-search`:

- Authenticated **users only** (applications get `403`, like the other searches).
- Body `SearchInput { searchString }`, validated by `SearchStringValidator`.
- Searches **locally registered, enabled** `Applications` by `DisplayName`
  (case-insensitive contains), capped (e.g. 50) and ordered by name.
- Returns `List<ApplicationSearchOutput { DisplayName, AadClientId, AadTenantId }>`
  so the picker can disambiguate multi-tenant apps.
- Always available (no Graph feature flag — it reads the local DB).

`GET /v2/applications-list` stays for backward compatibility.

## Frontend design (`safeexchange.blazorpwa`)

### 6. Tenant dropdown in `RegisterS2SApp.razor`

- On init, call new `ApiClient.GetS2SAllowedTenantsAsync()` → `GET /v2/s2sapps/allowed-tenants`.
- If the list is non-empty, render a required `<select>` of `{ displayName }`
  (value = tenantId); preselect when there is exactly one. Bind the choice into
  `S2SAppRegistrationRequest.AadTenantId` (the DTO already has the slot).
- If empty (feature off), hide the control and behave as today (server defaults
  to home tenant).

### 7. Application search picker in `CreateData.razor` / `EditData.razor`

- Replace the application-row `<InputSelect>` (bound to
  `StateContainer.RegisteredApplications`) with a read-only `InputText` +
  magnifying-glass button, mirroring the user/group rows.
- Add an `ItemSearchDialog<Application>` instance with an `ItemTemplate` showing
  `DisplayName` and, as secondary text, the client/tenant id.
- New `ApiClient.SearchApplicationsAsync(SearchInput)` → `POST /v2/application-search`.
- `Start…`/`Finish…` callbacks mirror the user/group ones; on pick, write the
  app's `DisplayName` into both `SubjectName` and `SubjectId` (the access grant
  keys on the globally-unique DisplayName, matching current server behavior).

## Entra prerequisites (operator / owner, not code)

For a daemon in the developer's own tenant to obtain a SafeExchange-audience
token cross-tenant:

1. The **SafeExchange API app registration must be multi-tenant**, so its service
   principal can be provisioned (admin-consented) into the developer's tenant.
2. It must **expose an app role** (e.g. `Access.AsApplication`) that is assigned
   to the daemon app — Azure won't mint a `.default` app token for the SafeExchange
   audience without at least one app-role assignment (SafeExchange itself only
   checks the `(clientId, tenantId)` registration, not the role).
3. The developer admin-consents both in their **own** tenant (where they are the
   admin — this is why "no admin in the customers tenant" is no longer a blocker).

## Backward compatibility

- `Authentication:S2SAllowedTenants` empty ⇒ no new behavior anywhere; user-token
  and existing app-token validation byte-for-byte unchanged; registration and the
  app dropdown keep working. Regular-tenant instances are unaffected until an
  operator opts in by populating the allowlist.

## Testing strategy (red-green TDD)

- `S2SAllowedTenant.ParseList`: valid JSON, empty/missing, malformed entries,
  non-GUID tenant ids, display-name default.
- `TokenClassification.IsAppOnlyToken`: v1 (appid/appidacr), v2 (azp/azpacr),
  delegated-with-scope, mixed — and a test asserting `TokenHelper` agrees.
- Issuer validation: empty allowlist ⇒ params identical to today (incl.
  `EnableAadSigningKeyIssuerValidation`); non-empty ⇒ user token from an
  allowlisted (non-home) tenant rejected, app token from an allowlisted tenant
  accepted, app token from a non-allowlisted tenant rejected, home-tenant tokens
  always accepted.
- Registration: allowlisted tenant accepted; non-allowlisted rejected `400`;
  empty allowlist ⇒ legacy default-to-home behavior.
- `application-search`: forbids application callers; validates search string;
  returns only enabled apps; disambiguation fields populated.
- Frontend: build + targeted component checks where feasible.

## Deployment / rollout

1. Merge backend PR first (frontend depends on the new endpoints).
2. Set `Authentication--S2SAllowedTenants` in the target Key Vault to the JSON
   allowlist (staging: include tenant `1472703e-99d8-45ee-aeac-7ec3ae9ab104`).
3. Ensure `Features:S2SAppsSelfService` is enabled for self-service registration.
4. Complete the Entra prerequisites above for the daemon's tenant.
