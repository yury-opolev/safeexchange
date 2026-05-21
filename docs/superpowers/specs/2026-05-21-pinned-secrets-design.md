# Pinned Secrets

**Status:** Design
**Author:** yury-opolev (with Claude)
**Date:** 2026-05-21
**Branch:** `features/pinned-secs`

## Summary

Let a user mark up to N secrets (default 5, configurable) as "pinned" so the web UI can render them on the main page for quick access. Pin/unpin is a personal, per-user bookkeeping action — it does not touch the secret's data, permissions, or audit log. Modelled after the existing `PinnedGroups` feature.

## Motivation

The main page currently has no notion of "favourites" for secrets — users wade through their full accessible-secret list every time. Pinning a small handful of frequently-used secrets gives the UI a fast-access surface without changing the underlying authorisation model.

## Out of scope

- **Auto-purge of stale pins** when the secret is deleted or the user loses access. Stale pins remain in the DB; the list endpoint surfaces them with `exists` / `canRead` flags so the UI can render an appropriate status and offer an unpin button.
- **Per-secret audit log entries** for pin/unpin actions. Pinning is purely user-side UX, not a security event.
- **Reordering / drag-and-drop.** Order is fixed at `CreatedAt DESC`.
- **Per-user override** of `MaxPinnedSecretsPerUser`. Single global value.
- **Feature kill-switch.** Consistent with `PinnedGroups`, which has none.
- **Notifications** when a pinned secret becomes inaccessible or is deleted. The UI derives this from the list response.

---

## Architecture overview

```
┌────────────────────────────────────────────────────────────┐
│  Client (web UI main page)                                 │
│    GET    /v2/pinnedsecrets-list      ← list pinned        │
│    GET    /v2/pinnedsecrets/{id}      ← is pinned?         │
│    PUT    /v2/pinnedsecrets/{id}      ← pin                │
│    DELETE /v2/pinnedsecrets/{id}      ← unpin              │
└────────────────────────────────────────────────────────────┘
         │
         ▼
┌────────────────────────────────────────────────────────────┐
│  SafeExchange.Functions HTTP triggers                      │
│    SafePinnedSecrets    (PUT/GET/DELETE single + list)     │
└────────────────────────────────────────────────────────────┘
         │
         ▼
┌────────────────────────────────────────────────────────────┐
│  SafeExchange.Core handlers                                │
│    SafeExchangePinnedSecrets        ← single-item ops      │
│    SafeExchangePinnedSecretsList    ← list                 │
└────────────────────────────────────────────────────────────┘
         │
         ▼
┌────────────────────────────────────────────────────────────┐
│  Cosmos containers                                         │
│    PinnedSecrets  (new)  — { UserId, SecretName, CreatedAt}│
│    ObjectMetadata (existing) — joined on SecretName        │
│    Permissions    (existing) — Read check at PUT + list    │
└────────────────────────────────────────────────────────────┘
```

The feature is structurally identical to `PinnedGroups`: a per-(user, target) join table, four endpoints, configurable per-user cap. No new infrastructure required, no audit events, no feature kill switch.

## Data model

### `PinnedSecret` entity

Cosmos container `PinnedSecrets`, partition key `"PSEC"` (matches `PinnedGroups`' `"PGRP"`).

| Field | Type | Notes |
|---|---|---|
| `PartitionKey` | string | constant `"PSEC"` |
| `UserId` | string | caller's user id; part of composite key |
| `SecretName` | string | the `ObjectMetadata.ObjectName`; part of composite key |
| `CreatedAt` | DateTime | UTC, used for list ordering (DESC) |

Composite key `{ UserId, SecretName }` enforces "a user can pin a given secret at most once" at the DB level. No FK constraint to `ObjectMetadata` (Cosmos doesn't have them); we rely on the auth check at PUT time and on the list endpoint's join with `ObjectMetadata` to gracefully handle stale pins.

### `PinnedSecretOutput` DTO

Used by `GET /v2/pinnedsecrets/{id}`, `PUT /v2/pinnedsecrets/{id}`, and `GET /v2/pinnedsecrets-list`.

```json
{
  "secretName": "prod-db-creds",
  "exists": true,
  "canRead": true,
  "canWrite": false,
  "canGrantAccess": false,
  "canRevokeAccess": false,
  "tags": ["prod", "database"]
}
```

| Field | Source | Value when stale |
|---|---|---|
| `secretName` | URL / pin row | always the pinned name |
| `exists` | `ObjectMetadata` lookup | `false` if no metadata row for `secretName` |
| `canRead` / `canWrite` / `canGrantAccess` / `canRevokeAccess` | caller's `SubjectPermissions` row for that secret | all `false` if no permission row |
| `tags` | `ObjectMetadata.Tags` | empty array if `exists = false` **or** caller has no `CanRead` (matches existing `secret-list` behaviour — tags are gated by Read) |

A UI tile rendering against this DTO sees three distinct states:
- **Live:** `exists: true`, `canRead: true` → render normally.
- **Access lost:** `exists: true`, `canRead: false` → render greyed-out / "no access" badge.
- **Deleted:** `exists: false` → render greyed-out / "deleted" badge, offer unpin button.

### No input DTO for `PUT`

Unlike `PinnedGroups` (where Entra IDs need cached display name + mail), secrets are local entities — the secret name is fully in the URL path, so the `PUT` request body is empty.

## Configuration

New section in `appsettings.json` / `local.settings.json`:

```json
"PinnedSecrets": {
  "MaxPinnedSecretsPerUser": 5
}
```

```csharp
namespace SafeExchange.Core.Configuration;

public class PinnedSecretsConfiguration
{
    public int MaxPinnedSecretsPerUser { get; set; } = 5;
}
```

Bound in `SafeExchangeStartup` the same way `OrphanedSecretConfiguration` is bound — `services.Configure<PinnedSecretsConfiguration>(...)` — and injected into `SafeExchangePinnedSecrets` via constructor. Default of 5 baked into the class so a missing config section still works.

## API surface

All endpoints share the standard SafeExchange prelude:

1. `globalFilters.GetFilterResultAsync` early-return.
2. `SubjectHelper.GetSubjectInfoAsync`.
3. If subject type is `Application` → `403 Forbidden, "Applications cannot use this API."` (pinning is user-only UX, matches `PinnedGroups`).

### `PUT /v2/pinnedsecrets/{secretId}` — pin

1. Standard prelude.
2. `secretId` format check (existing secret-name validation used by `SafeExchangeSecretMeta`).
3. Look up `ObjectMetadata` by `secretId`. If missing → `404 not_found`.
4. **Read check:** `permissionsManager.IsAuthorizedAsync(subjectType, subjectId, secretId, PermissionType.Read)`. If `false` → `403 Forbidden` with `InsufficientPermissions(Read, secretId, "")`.
5. **Existence check first:** if a row already exists for `(UserId, SecretName)` → return it idempotently (`200 OK` with existing row); no error, no second row.
6. **Cap check:** count existing pinned rows for this user. If `>= MaxPinnedSecretsPerUser` → `400 Bad Request` with message:
   > `"Pinned secret count is {N}, which is higher or equal than allowed no. of {Max} pinned secrets. Please unpin secrets before adding new ones."`
   (mirrors `PinnedGroups` wording).
7. Otherwise insert a new `PinnedSecret { UserId, SecretName, CreatedAt = UtcNow }`, using `DbUtils.TryAddOrGetEntityAsync` so a concurrent PUT collision becomes a silent get. `SaveChangesAsync()`.
8. Return `200 OK` with `BaseResponseObject<PinnedSecretOutput>`.

### `GET /v2/pinnedsecrets/{secretId}` — is-pinned check

1. Standard prelude + format check.
2. Look up the pin row by `(UserId, SecretName)`.
3. If absent → `200 OK, { status: "no_content", result: null }` (user just isn't pinning this secret — distinct from "pinned but stale").
4. If present, build `PinnedSecretOutput` using the same `exists` + permission resolution logic as the list endpoint.
5. Return `200 OK` with `PinnedSecretOutput`.

No `Read` re-check here: the user already had access at pin time, and we explicitly accepted that a pin may outlive access. Exposing "is this in my pin list" to the original pinner is fine; we're not leaking secret contents.

### `DELETE /v2/pinnedsecrets/{secretId}` — unpin

1. Standard prelude + format check.
2. Look up the pin row by `(UserId, SecretName)`.
3. If absent → `200 OK, { status: "no_content", result: "Pin for secret '{id}' does not exist." }` (matches `PinnedGroups` deletion shape — idempotent, never 404).
4. If present, remove and `SaveChangesAsync()`.
5. Return `200 OK, { status: "ok", result: "ok" }`.

No `Read` re-check, no `Revoke` permission needed. Unpinning is purely user-side bookkeeping; users always control their own pin list.

### `GET /v2/pinnedsecrets-list` — list pinned secrets

1. Standard prelude.
2. Load all `PinnedSecret` rows where `UserId == caller`, ordered by `CreatedAt DESC`.
3. Batched query: load `ObjectMetadata` for those secret names (`WHERE ObjectName IN (...)`) to determine `exists` and pull `Tags`.
4. Batched query: load the caller's `SubjectPermissions` rows for those names to determine per-secret access flags.
5. Build response: one `PinnedSecretOutput` per pin row in `CreatedAt DESC` order. **Stale pins are always returned** with `exists` / `canRead` / etc. set accordingly. None are filtered out.

With `Max = 5` this is trivially cheap. No N+1.

### Authorisation summary

| Endpoint | Required permission | Notes |
|---|---|---|
| `PUT /v2/pinnedsecrets/{id}` | `Read` on the secret + under cap | Idempotent |
| `GET /v2/pinnedsecrets/{id}` | none beyond auth | Returns own pin metadata only |
| `DELETE /v2/pinnedsecrets/{id}` | none beyond auth | Idempotent |
| `GET /v2/pinnedsecrets-list` | none beyond auth | Stale pins surfaced with flags |

## Validation

| Input | Rule | Failure |
|---|---|---|
| `secretId` path param | Matches existing secret-name validation used by `SafeExchangeSecretMeta` (length cap, reserved-name pattern). | `400 bad_request` |
| HTTP method | One of `PUT`, `GET`, `DELETE` on `/{id}`, `GET` on `-list`. | `400 bad_request, "Request method '{m}' not allowed."` |
| Caller subject type | Must be `User`. `Application` → `403 Forbidden`. | `403 forbidden` |

## Cap-check semantics

Counted at PUT time only:

- `Count(PinnedSecrets WHERE UserId = caller)` — **counts all pin rows for the user, including stale ones** (pins for deleted/access-lost secrets). Rationale: simplicity and predictability; the cap is "rows the user is holding," not "live tiles on the page." If we counted only live ones, a user could end up with >5 rows after secrets get deleted and silently expand back to 5 live tiles when re-pinning — and the meaning of "5" gets fuzzy.
- Existing pin for the same `(UserId, SecretName)` is **not** double-counted: the idempotent PUT path returns the existing row before the cap check would fire. (Order: existence check first; cap check only when we'd actually insert.)

The user can always unpin stale entries to free a slot — that's the safety valve. The list DTO's `exists` and `canRead` flags let the UI prompt for cleanup.

## Concurrency

- **Two concurrent PUTs for the same (user, secret):** composite key `{ UserId, SecretName }` rejects the duplicate at the DB layer; `DbUtils.TryAddOrGetEntityAsync` re-reads and returns the winning row. Same pattern as `PinnedGroups`.
- **Two concurrent PUTs for two different secrets, both racing the cap:** there's a small window where both succeed and the user ends up at `Max+1`. Acceptable trade-off — matches `PinnedGroups` (which doesn't lock either). Hard-enforcing would require a transaction across two containers, which Cosmos doesn't really do efficiently. The next PUT will be rejected and the over-count self-heals on unpin.
- **PUT racing with DELETE of the secret itself:** PUT loads `ObjectMetadata` for the auth check; if the secret is deleted between auth check and insert, the pin row gets created against a now-missing secret. That's the same "stale pin" state we already handle in the list endpoint, so no special handling needed.

## Logging

All via existing `ILogger.LogInformation` — no new logging infrastructure.

| Event | Log line summary |
|---|---|
| Pin created | `"User '{userId}' pinned secret '{secretId}'."` |
| Pin already present (idempotent PUT) | `"User '{userId}' attempted to pin secret '{secretId}' but pin already exists."` |
| Cap rejection | `"User '{userId}' has {N} pinned secrets, which is >= max. allowed {Max}."` |
| Read check failed | covered by the existing `permissionsManager` log path |
| Pin deleted | `"User '{userId}' unpinned secret '{secretId}'."` |
| Unpin no-op | `"User '{userId}' attempted to unpin secret '{secretId}' but pin does not exist."` |

## Migration

Cosmos containers are created on first write via `ToContainer("PinnedSecrets")` in `OnModelCreating`, same as every other container in the project. **No data migration needed** — there is no existing data to backfill. (No new migration script added to `MigrationsHelper`.)

The new container also needs to be provisioned in the ARM template at `deployment/current/arm/services-template.arm.json` alongside the existing `PinnedGroups` container so that fresh deployments include it.

## File layout

### New files

| Path | Purpose |
|---|---|
| `SafeExchange.Core/Configuration/PinnedSecretsConfiguration.cs` | Config class — `MaxPinnedSecretsPerUser` (default 5) |
| `SafeExchange.Core/Model/PinnedSecret.cs` | Entity — `{ PartitionKey, UserId, SecretName, CreatedAt }` |
| `SafeExchange.Core/Model/Dto/Output/PinnedSecretOutput.cs` | DTO — `{ secretName, exists, canRead, canWrite, canGrantAccess, canRevokeAccess, tags }` |
| `SafeExchange.Core/Functions/SafeExchangePinnedSecrets.cs` | Handler — PUT/GET/DELETE for single pin |
| `SafeExchange.Core/Functions/SafeExchangePinnedSecretsList.cs` | Handler — GET list |
| `SafeExchange.Functions/Functions/SafePinnedSecrets.cs` | Function triggers — routes `/v2/pinnedsecrets/{id}` + `/v2/pinnedsecrets-list` |
| `SafeExchange.Tests/Tests/PinnedSecretsTests.cs` | Endpoint tests (mirrors `PinnedGroupsTests.cs`) |

### Modified files

| Path | Change |
|---|---|
| `SafeExchange.Core/DatabaseContext/SafeExchangeDbContext.cs` | + `DbSet<PinnedSecret> PinnedSecrets`; + `ToContainer("PinnedSecrets")` + `HasKey({ UserId, SecretName })` in `OnModelCreating` |
| `SafeExchange.Core/SafeExchangeStartup.cs` | + bind `PinnedSecretsConfiguration` section, register in DI |
| `appsettings.json` / `local.settings.json` template (if checked in) | + `PinnedSecrets` section |
| `deployment/current/arm/services-template.arm.json` | + `PinnedSecrets` Cosmos container definition (mirror `PinnedGroups`) |
| `docs/api-endpoints.md` | + new `## Pinned Secrets` table |
| `docs/data-model.md` | + `PinnedSecret` entity row |
| `docs/architecture.md` | (optional) endpoint-group table row for `/v2/pinnedsecrets/*` |

## Testing strategy

Endpoint tests in `SafeExchange.Tests/Tests/PinnedSecretsTests.cs`, following the same structure as `PinnedGroupsTests.cs`. `[OneTimeSetUp]` builds a `TestStartup`; each test uses a fresh in-memory `SafeExchangeDbContext`.

**Shared fixtures:**
- 3 callers: `alice` (User), `bob` (User), `charlie` (Application).
- 7 secrets owned by `alice`: `s-1` … `s-7`. `alice` has direct `CanRead` on all 7.
- `bob` has direct `CanRead` on `s-1` only.
- `MaxPinnedSecretsPerUser = 5` for the test config.

### `PUT /v2/pinnedsecrets/{id}`

| Test | Expected |
|---|---|
| `Application` caller | `403 "Applications cannot use this API."` |
| Invalid secret-id format | `400 bad_request` |
| Secret does not exist | `404 not_found` |
| Caller has no `Read` on secret | `403 InsufficientPermissions(Read, ...)` |
| Happy path: alice pins `s-1` | `200`, row created with `CreatedAt` set, DTO has `exists=true, canRead=true` |
| Idempotent re-PUT same `(user, secret)` | `200`, no second row inserted, existing row returned |
| Alice already at cap (5 pins), tries 6th | `400` cap message, no row created |
| Concurrent PUTs same secret | both `200`, exactly one row (composite-key dedup via `DbUtils.TryAddOrGetEntityAsync`) |

### `GET /v2/pinnedsecrets/{id}`

| Test | Expected |
|---|---|
| `Application` caller | `403` |
| Pin not present | `200 no_content, result: null` |
| Pin present, secret live, caller has `CanRead` | DTO `exists=true, canRead=true, tags=[...]` |
| Pin present, caller's permission row removed since pinning | DTO `exists=true, canRead=false, tags=[]` |
| Pin present, secret deleted (no `ObjectMetadata`) | DTO `exists=false, all flags false, tags=[]` |

### `DELETE /v2/pinnedsecrets/{id}`

| Test | Expected |
|---|---|
| `Application` caller | `403` |
| Pin not present | `200 no_content` (idempotent) |
| Pin present | `200 ok`, row removed |
| Delete pin for secret caller cannot Read | `200 ok` (no Read re-check; user can always unpin) |
| Re-DELETE | `200 no_content` (idempotent) |

### `GET /v2/pinnedsecrets-list`

| Test | Expected |
|---|---|
| `Application` caller | `403` |
| Caller has 0 pins | `200 no_content`, empty list |
| Caller has 3 pins, all live, all readable | 3 DTOs, `exists=true, canRead=true`, ordered `CreatedAt DESC` |
| Mix: 2 live+readable, 1 access-lost, 1 secret-deleted | 4 DTOs all returned, flags set per the three states, order preserved |
| Tags reflected when `canRead=true`; empty when `canRead=false` or `exists=false` | matches secret-list behaviour |
| Two users with overlapping pins | each user sees only their own |

No separate unit tests — the handler is thin enough that endpoint-level tests cover the logic. Consistent with how `PinnedGroups` is tested.

## Open questions / non-goals

- **Auto-purge of stale pins on access loss or secret deletion.** Explicitly deferred. The list DTO's `exists` + `canRead` flags handle the UX; a future feature could add a sweep job.
- **Per-user override of `MaxPinnedSecretsPerUser`.** Single global value; revisit if needed.
- **Audit log integration.** Pin/unpin actions are not security events; explicitly excluded.
- **Reordering / drag-and-drop.** `CreatedAt DESC` is the only order; no `Order` field on the row.
- **Feature kill-switch.** Not added; consistent with `PinnedGroups`.
- **Notifications when a pinned secret becomes inaccessible.** UI can derive this from the list response.
