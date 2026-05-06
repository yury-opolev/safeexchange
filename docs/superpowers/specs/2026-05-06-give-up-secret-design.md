# Give-Up Secret + Orphan Detection

**Status:** Design
**Author:** yury-opolev (with Claude)
**Date:** 2026-05-06
**Branch:** `feature/give-up-secret`

## Summary

Add a self-service way for users (and applications) to drop their own access to a secret without needing `RevokeAccess` permission, plus an automatic safety rule that schedules secrets for purge once nobody can manage them anymore. Includes an atomic `PATCH` endpoint for the access list, so admins can swap custodians in a single transaction.

## Motivation

Today a user who no longer wants a secret in their list has no way to remove themselves: `DELETE /v2/access/{secretId}` requires `RevokeAccess`, which most users don't hold. The only workaround is asking an admin.

Once self-revocation exists, a related risk appears: a user can leave a secret in a state where nobody has `GrantAccess`. Such a secret can never be re-shared or managed; it lingers until idle or scheduled expiration removes it. We want a deterministic rule that detects this state and starts a grace-period countdown.

The same orphan risk exists today via `DELETE /v2/access/{secretId}` — an admin with `RevokeAccess` can revoke the last `GrantAccess` holder. The orphan rule must apply uniformly to any path that removes permissions, not just give-up.

## Out of scope

- Recovering orphaned secrets via in-app admin tooling. Recovery is via direct DB intervention until/unless a separate feature adds a UI.
- Webhook notifications and push notifications for orphan events. Audit log only for now.
- Migrations or schema changes — the feature reuses `ExpirationMetadata`.
- Performance hardening of permission queries beyond the new `HasAnyAccessAsync` helper.

---

## Architecture overview

```
┌──────────────────────────────────────────────────────────┐
│  Client (browser / app)                                  │
│    GET    /v2/access-giveup/{secretId}   ← preview       │
│    DELETE /v2/access-giveup/{secretId}   ← act           │
│    PATCH  /v2/access/{secretId}          ← atomic update │
└──────────────────────────────────────────────────────────┘
         │
         ▼
┌──────────────────────────────────────────────────────────┐
│  SafeExchange.Functions HTTP triggers                    │
│    SafeAccess        (POST/GET/DELETE/PATCH)             │
│    SafeAccessGiveUp  (GET/DELETE)                        │
└──────────────────────────────────────────────────────────┘
         │
         ▼
┌──────────────────────────────────────────────────────────┐
│  SafeExchange.Core handlers                              │
│    SafeExchangeAccess         ← extended for PATCH       │
│    SafeExchangeAccessGiveUp   ← new                      │
└──────────────────────────────────────────────────────────┘
         │                                  │
         ▼                                  ▼
┌─────────────────────────┐    ┌─────────────────────────┐
│  PermissionsManager     │    │  OrphanedSecretManager  │
│   + HasAnyAccessAsync() │    │   + PreviewAsync()      │
│                         │    │   + ApplyOrphanRule…()  │
└─────────────────────────┘    └─────────────────────────┘
                                        │
                                        ▼
                              ┌─────────────────────────┐
                              │  ExpirationMetadata     │
                              │  (existing)             │
                              └─────────────────────────┘
```

The give-up flow and the existing `DELETE /v2/access` flow both call `OrphanedSecretManager.ApplyOrphanRuleAsync` on the same `dbContext` before commit. The new `PATCH /v2/access/{secretId}` does the same after applying combined add/remove changes.

## Configuration

### `Features.UseAccessGiveUp: bool`

Master kill-switch. Default `false`. When `false`:

- `GET /v2/access-giveup/{secretId}` → `204 No Content`.
- `DELETE /v2/access-giveup/{secretId}` → `204 No Content`.
- `PATCH /v2/access/{secretId}` works normally but skips the orphan check.
- `DELETE /v2/access/{secretId}` (existing) skips the orphan check.

When `true`, all of the above run their full flow.

### `OrphanedSecretConfiguration` (new section)

```json
"OrphanedSecret": {
  "Ownership": "UserOrApp",
  "GracePeriod": "7.00:00:00"
}
```

| Field | Type | Default | Description |
|---|---|---|---|
| `Ownership` | `OrphanOwnershipMode` | `UserOrApp` | Which subject types count as a custodian |
| `GracePeriod` | `TimeSpan` | `7.00:00:00` | How long after orphan detection before the secret is purged |

`OrphanOwnershipMode`:
- `UserOrApp` — direct `User` or `Application` rows with `CanGrantAccess = true` count as custodian. Group rows do not.
- `UserOrAppOrGroup` — direct `Group` rows with `CanGrantAccess = true` also count.

The default is the strict choice: operators must opt in to group ownership explicitly. Groups are not transitively traversed; only the row's `SubjectType` is consulted.

## Owner / custodian definition

A "custodian" of a secret is **any `SubjectPermissions` row with `CanGrantAccess = true` whose `SubjectType` is allowed by the current `OrphanOwnershipMode`**. Rationale: a subject with `CanGrantAccess` can grant themselves any of `Read`, `Write`, `GrantAccess` (the only flag they cannot self-grant is `RevokeAccess`, but that does not impede their custodianship for orphan purposes).

A secret is **orphaned** iff it has zero custodians. The orphan check is a single DB query:

```sql
SELECT EXISTS (
  SELECT 1 FROM Permissions
  WHERE SecretName = @secretId
    AND CanGrantAccess = true
    AND SubjectType IN (<allowed types per Ownership>)
)
```

No Graph traversal. No group-membership enumeration. Empty groups holding `CanGrantAccess` still count as custodian under `UserOrAppOrGroup`; idle expiration handles the truly-unused case.

## Orphan rule mechanics

`OrphanedSecretManager.ApplyOrphanRuleAsync(secretId, dbContext)` is called by every endpoint that removes permissions, immediately before `SaveChangesAsync()`:

```text
let prospective = now + OrphanedSecretConfiguration.GracePeriod
if HasCustodian(secretId): return false
load ExpirationMetadata
if ScheduleExpiration is true and ExpireAt <= prospective:
    return true   // pre-existing earlier expiration wins; never extend life
else:
    set ScheduleExpiration = true
    set ExpireAt = prospective
    return true
```

Key invariants:

1. **Never extends life.** Pre-existing `ExpireAt` earlier than `now + GracePeriod` is preserved.
2. **Idempotent.** Repeated calls produce the same final `ExpireAt`. A second give-up that doesn't change custodian count is a no-op on the schedule.
3. **No rescue is possible from inside the API.** Once orphaned, no subject has `GrantAccess`, so no user/app/group can call `POST /v2/access` or `PATCH /v2/access` to add custodians (those endpoints require `GrantAccess`). Recovery is out-of-band only.
4. **Atomic with the row change.** The orphan check writes to the tracked entity graph but does not commit. The caller's `SaveChangesAsync()` commits everything in one transaction; EF Core / Cosmos optimistic concurrency on the etag handles concurrent racers.

## API surface

### `GET /v2/access-giveup/{secretId}` — preview

**Auth flow:**
1. `globalFilters.GetFilterResultAsync` early-return.
2. `SubjectHelper.GetSubjectInfoAsync`. Disabled apps → `403`.
3. If `Features.UseAccessGiveUp == false` → `204 No Content`.
4. Secret existence check → `404` if missing.
5. Access gate: caller must have at least one permission flag (`Read | Write | GrantAccess | RevokeAccess`) on the secret, direct or via group. Otherwise `403 Forbidden` with the standard `InsufficientPermissions` shape.

**Response (200 OK):**

```json
{
  "status": "ok",
  "result": {
    "hasDirectRow": true,
    "wouldOrphan": true,
    "currentExpireAt": null,
    "prospectiveExpireAt": "2026-05-13T09:00:00Z"
  }
}
```

| Field | Description |
|---|---|
| `hasDirectRow` | Does the caller have a direct `SubjectPermissions` row on this secret |
| `wouldOrphan` | Would the orphan rule fire if the row were removed now (computed against current DB state) |
| `currentExpireAt` | Existing `ExpirationMetadata.ExpireAt` if `ScheduleExpiration` is set, else `null` |
| `prospectiveExpireAt` | What the post-action `ExpireAt` would be: `min(now + GracePeriod, currentExpireAt)` if `wouldOrphan`, else `null` |

### `DELETE /v2/access-giveup/{secretId}` — perform

**Auth flow:** identical to `GET` above, including the access gate.

**Behavior:**
1. Look up the caller's direct `SubjectPermissions` row.
2. If absent (the access gate already rejected callers with no access at all, so this branch only covers "group-only access") → `204 No Content`. No DB writes.
3. If present, remove the row.
4. Call `OrphanedSecretManager.ApplyOrphanRuleAsync(secretId, dbContext)`.
5. `SaveChangesAsync()` (single commit).
6. Return `200 OK`:

```json
{
  "status": "ok",
  "result": {
    "hadDirectRow": true,
    "wasOrphaned": true,
    "expireAt": "2026-05-13T09:00:00Z"
  }
}
```

`expireAt` is the post-state value of `ExpirationMetadata.ExpireAt` if `ScheduleExpiration = true`, else `null`.

### `PATCH /v2/access/{secretId}` — atomic update

**Body:** uses the existing `SubjectPermissionsInput` DTO (per-flag booleans, not a "permission" string), so PATCH body shape is symmetric with what `POST` and `DELETE` accept today:

```json
{
  "add": [
    {
      "subjectType": "User",
      "subjectName": "alice@contoso.com",
      "subjectId": "alice@contoso.com",
      "canRead": true,
      "canWrite": true,
      "canGrantAccess": true,
      "canRevokeAccess": true
    }
  ],
  "remove": [
    {
      "subjectType": "User",
      "subjectName": "bob@contoso.com",
      "subjectId": "bob@contoso.com",
      "canRead": true,
      "canWrite": true,
      "canGrantAccess": true,
      "canRevokeAccess": true
    }
  ]
}
```

Both lists optional but at least one must be non-empty. Otherwise `400 Bad Request`.

**Auth flow:**
1. Standard prelude (filters, subject resolution, secret existence).
2. If `add` non-empty: caller must have `PermissionType.GrantAccess` (direct or via group). Otherwise `403`.
3. If `remove` non-empty: caller must have `PermissionType.RevokeAccess` (direct or via group). Otherwise `403`.
4. Note: `Features.UseAccessGiveUp` does **not** gate access to PATCH; it only gates the orphan-rule call inside.

**Execution (single transaction):**
1. Apply each `remove` via `PermissionsManager.UnsetPermissionAsync`.
2. Apply each `add` via `PermissionsManager.SetPermissionAsync`. The existing `userCanRevokeAccess` masking is preserved: a caller without `RevokeAccess` cannot grant the `RevokeAccess` flag (the flag is stripped silently — matches existing POST behavior).
3. If `Features.UseAccessGiveUp == true`: call `OrphanedSecretManager.ApplyOrphanRuleAsync(secretId, dbContext)`.
4. `SaveChangesAsync()`.

**Response:** `200 OK` with the existing `BaseResponseObject<string> { Status: "ok", Result: "ok" }` envelope. Matches `POST` and `DELETE` shape.

### Existing `DELETE /v2/access/{secretId}` integration

After the existing per-subject revocation loop in `SafeExchangeAccess.RevokeAccessAsync`, immediately before `SaveChangesAsync()`:

```csharp
if (this.features.UseAccessGiveUp)
{
    await this.orphanedSecretManager.ApplyOrphanRuleAsync(secretId, this.dbContext);
}
await this.dbContext.SaveChangesAsync();
```

The endpoint's response shape is unchanged. Orphan effects are observable only via audit logs and subsequent `GET /v2/secret/{id}` reads of `ExpireAt`.

### Existing `POST /v2/access/{secretId}`

Unchanged. Adding permissions can never reduce custodian count, so no orphan check is needed.

### Existing `GET /v2/access/{secretId}`

Unchanged. Read-only; no state transition.

## Authorization summary

| Endpoint | Required permission | Notes |
|---|---|---|
| `GET /v2/access/{id}` | `Read` | Existing |
| `POST /v2/access/{id}` | `GrantAccess` | Existing |
| `PATCH /v2/access/{id}` | `GrantAccess` if adding; `RevokeAccess` if removing | New |
| `DELETE /v2/access/{id}` | `RevokeAccess` | Existing; orphan check added |
| `GET /v2/access-giveup/{id}` | Any flag (direct or via group) | New |
| `DELETE /v2/access-giveup/{id}` | Any flag (direct or via group) | New |

## Concurrency

Two simultaneous `DELETE /v2/access-giveup` calls from two custodians both currently holding `CanGrantAccess`:

1. Each transaction loads its own row, deletes it, and queries `HasCustodianAsync`.
2. The first commit succeeds and either schedules (if no remaining custodian) or doesn't (if remaining custodian still exists).
3. The second commit retries against fresh state via EF Core / Cosmos optimistic concurrency, recomputes `HasCustodianAsync`, and either no-ops the schedule (if pre-existing schedule covers it) or applies it.

No new locking primitive required. Failures retry through the existing `ActionResults.TryCatchAsync` path.

## Audit logging

All log lines via the existing `ILogger.LogInformation` pattern. No new logging infrastructure.

| Event | Log line summary |
|---|---|
| Give-up DELETE: row removed | `"Subject {type} '{id}' relinquished access to '{secretId}'."` |
| Give-up DELETE: no-op | `"Subject {type} '{id}' attempted give-up on '{secretId}' but had no direct row."` |
| Orphan rule fired | `"Secret '{secretId}' has no custodian after permission change. Scheduled for purge at {expireAt}."` |
| Orphan rule no-op | `"Secret '{secretId}' orphan check: still has custodian (no schedule applied)."` |
| PATCH summary | `"Subject {type} '{id}' applied {N} adds and {M} removes to '{secretId}'."` |

## DTOs and code layout

### New files

| Path | Purpose |
|---|---|
| `SafeExchange.Core/Configuration/OrphanOwnershipMode.cs` | enum |
| `SafeExchange.Core/Configuration/OrphanedSecretConfiguration.cs` | config class |
| `SafeExchange.Core/Permissions/IOrphanedSecretManager.cs` | service interface |
| `SafeExchange.Core/Permissions/OrphanedSecretManager.cs` | service impl |
| `SafeExchange.Core/Functions/SafeExchangeAccessGiveUp.cs` | give-up handler |
| `SafeExchange.Core/Model/Dto/Input/AccessUpdateInput.cs` | PATCH body DTO |
| `SafeExchange.Core/Model/Dto/Output/GiveUpPreviewOutput.cs` | preview response DTO |
| `SafeExchange.Core/Model/Dto/Output/GiveUpResultOutput.cs` | action response DTO |
| `SafeExchange.Functions/Functions/SafeAccessGiveUp.cs` | give-up function trigger |

### Modified files

| Path | Change |
|---|---|
| `SafeExchange.Core/Configuration/Features.cs` | + `UseAccessGiveUp` field, update Clone/Equals/GetHashCode |
| `SafeExchange.Core/Permissions/IPermissionsManager.cs` | + `HasAnyAccessAsync` method |
| `SafeExchange.Core/Permissions/PermissionsManager.cs` | + `HasAnyAccessAsync` impl |
| `SafeExchange.Core/Functions/SafeExchangeAccess.cs` | + ctor takes `IOrphanedSecretManager`; + `case "patch"` and `PatchAccessAsync` method; orphan-check hook in `RevokeAccessAsync` |
| `SafeExchange.Core/SafeExchangeStartup.cs` | + bind `OrphanedSecretConfiguration`; + register `IOrphanedSecretManager` |
| `SafeExchange.Functions/Functions/SafeAccess.cs` | + accept `patch` in `HttpTrigger`; + propagate `IOrphanedSecretManager` through ctor |
| `docs/api-endpoints.md` | + document new endpoints |

## Testing strategy

### Unit — `OrphanedSecretManagerTests.cs`

- Custodian present (user / app / group with each `Ownership` setting) → no schedule applied
- No custodian → schedule applied
- Pre-existing earlier `ExpireAt` → unchanged (never extends life)
- Pre-existing later `ExpireAt` → lowered to `now + GracePeriod`
- Idempotency: applying twice produces same final state

### Endpoint — `SafeExchangeAccessGiveUpPreviewTests.cs`

- Feature flag off → `204`
- Secret missing → `404`
- No access at all → `403`
- Group-only access → `200`, `hasDirectRow = false`, `wouldOrphan = false`
- Direct row, not last custodian → `wouldOrphan = false`
- Direct row, last custodian → `wouldOrphan = true`, `prospectiveExpireAt` set
- Last custodian + earlier scheduled `ExpireAt` → `prospectiveExpireAt = currentExpireAt`
- Application caller, only app custodian → behaves identically to user case

### Endpoint — `SafeExchangeAccessGiveUpActionTests.cs`

- Feature flag off → `204`, no DB writes
- Secret missing → `404`, no DB writes
- No access at all → `403`, no DB writes
- Group-only access → `204`, no DB writes
- Direct row, not last custodian → `200`, row removed, no schedule
- Last custodian → `200`, row removed, schedule applied
- App last custodian → schedule applied
- Group ownership setting interactions
- Concurrent DELETEs → both succeed, single final schedule
- Repeated DELETE after row gone → idempotent `204`

### Endpoint — `SafeExchangeAccessRevokeOrphanTests.cs`

- Feature flag on, body revokes last custodian → orphan applied
- Feature flag off, same → no orphan applied
- Body revokes non-custodian → no orphan applied
- Self-revoke causing orphan → orphan applied

### Endpoint — `SafeExchangePatchAccessTests.cs`

- Empty `add` and `remove` → `400`
- `add` without `GrantAccess` → `403`
- `remove` without `RevokeAccess` → `403`
- Swap custodian (remove self + add new) → no orphan
- Self-removal without add (last custodian) → orphan applied
- `RevokeAccess` flag in adds without caller's `RevokeAccess` → silently stripped
- Same subject in both lists → consistent end state
- Feature flag off, remove that would orphan → no orphan check fires

### Concurrency tests

- Two simultaneous give-ups → eventual single schedule
- Give-up racing with PATCH → final state matches post-state semantics

## Open questions / non-goals

- **Recovery UX for orphaned secrets.** Out of scope. Operators handle via direct DB tooling.
- **Webhook event for orphan.** Out of scope per Q7. May be added later behind its own flag.
- **Push notifications to remaining direct readers.** Out of scope per Q7.
- **Privileged "rescue" endpoint that bypasses `GrantAccess` requirement.** Not designed; defer until requested.
- **Per-secret grace-period override.** Not designed; the global `GracePeriod` is the single source of truth.
