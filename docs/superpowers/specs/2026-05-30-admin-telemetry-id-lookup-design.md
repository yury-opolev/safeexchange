# Admin telemetry-id lookup (bounded reversibility) — Design

Date: 2026-05-30
Status: Approved (design), pending implementation plan
Branch: `features/admin-telemetry-id-lookup` (both `safeexchange` backend and
`safeexchange.blazorpwa` admin panel repos)

## Goal

Let an admin, **occasionally and one-off**, correlate a user with their
pseudonymous telemetry id(s) for incident investigation — in **both** directions:

1. **User → ids:** a user reports a problem; the admin opens that user's
   detail page and sees their **current** telemetry id plus the **recently
   retired** ones (still within a retention window), each with the time window
   it was active, so they can research the matching App Insights telemetry.
2. **Id → user:** the admin sees a suspicious `saex.telemetryId` in telemetry
   and looks it up to find which user it belonged to (if still within the
   retention window).

This extends the shipped rotating-telemetry-id feature
(`docs/superpowers/specs/2026-05-30-rotating-telemetry-id-design.md`).

## The problem this solves

Rotation is calendar-weekly (Monday 00:00 UTC) and the current design keeps
**zero** history — a rotated id is overwritten and unresolvable. An admin
investigating Friday's events on Monday cannot map Friday's id to a user,
because by Monday every active user has rotated. We need **bounded, auto-expiring
reversibility**: recent ids resolvable for a defined window, then physically
gone — preserving "no *indefinite* tracking" while enabling real incident
response.

## Scope

Backend (`safeexchange`) + admin panel (`safeexchange.blazorpwa`).
**Staging-only** for this feature (admin panel has no prod). **Out of scope:**
client/PWA telemetry; an explicit per-user map-erasure action (deferred — see
§6); changing the rotation cadence.

## Design

### 1. Data model

**`User` (`SafeExchange.Core/Model/User.cs`)** — add one field:
- `TelemetryIdIssuedAt` (DateTime, UTC) — when the **current** `TelemetryId` was
  generated. Used to compute the `validFrom` of a retired id when it rotates out.
  (Existing fields `TelemetryId`, `TelemetryIdExpiresAt` are unchanged.)

**New entity `TelemetryIdMapEntry` (`SafeExchange.Core/Model/TelemetryIdMapEntry.cs`)**
— one document per **retired** id, in a dedicated container `TelemetryIdMap`:
- `Id` (string) — the retired telemetry id (the document id; globally unique GUID "n").
- `UserId` (string) — owning user's `User.Id`. **Partition key (`/UserId`).**
- `ValidFromUtc` (DateTime) — when this id became the user's current id.
- `ValidToUtc` (DateTime) — the rotation instant it was retired.

**Container TTL:** `TelemetryIdMap` has `DefaultTimeToLive = 2_419_200` seconds
(**28 days**). Map docs are write-once (never updated), so Cosmos purges each
entry 28 days after it is written (= 28 days after `ValidToUtc`). No purge job.

The **current** id is *not* in the map — it lives on the `User` doc. An id enters
the map only when it rotates out.

**Partitioning rationale:** partition by `UserId` so a future per-user erasure
(§6) is a single-partition wipe. The id→user lookup (§4) is therefore a
cross-partition query, which is fine: lookups are rare, one-off admin actions on
a small, short-TTL container.

### 2. Rotation — write a map entry when an id retires

`TelemetryIdRotator.EnsureCurrent` becomes a pure function returning a result
object instead of a bool:

```csharp
public TelemetryIdRotationResult EnsureCurrent(User user, DateTime nowUtc)
```

`TelemetryIdRotationResult` (new, `SafeExchange.Core/Telemetry/`):
- `bool Rotated` — true when a new id was generated (caller persists).
- `string? RetiredTelemetryId` — the id just retired, or null.
- `DateTime RetiredValidFromUtc`, `DateTime RetiredValidToUtc` — its window
  (meaningful only when `RetiredTelemetryId is not null`).

Logic:
- If `TelemetryId` is empty (first-ever) **or** `nowUtc >= TelemetryIdExpiresAt`:
  - If the existing `TelemetryId` is **non-empty** (a real rotation, not first
    creation), capture `RetiredTelemetryId = user.TelemetryId`,
    `RetiredValidFromUtc = user.TelemetryIdIssuedAt`, `RetiredValidToUtc = nowUtc`.
  - `user.TelemetryId = Guid.NewGuid().ToString("n")`,
    `user.TelemetryIdIssuedAt = nowUtc`,
    `user.TelemetryIdExpiresAt = NextWeekBoundaryUtc(nowUtc)`.
  - return `Rotated = true`.
- Otherwise return `Rotated = false` with no retired id.

`NextWeekBoundaryUtc` is unchanged. The rotator stays pure and DB-free (still
unit-tested with `DateTimeProvider.UseSpecifiedDateTime`).

**`TokenMiddlewareCore.RunAsync`** (caller): after `EnsureCurrent`, when
`result.RetiredTelemetryId is not null`, add a `TelemetryIdMapEntry`
`{ Id = RetiredTelemetryId, UserId = user.Id, ValidFromUtc, ValidToUtc }` to the
context and persist it in the same `SaveChangesAsync` that already runs when
`Rotated`. First-ever creation writes no map entry (no retired id).

### 3. Feature 1 — show the user's ids on the detail page

**Backend — `UserDetailOutput`** gains:
- `CurrentTelemetryId` (string), `TelemetryIdActiveSinceUtc` (DateTime =
  `TelemetryIdIssuedAt`), `TelemetryIdRotatesAtUtc` (DateTime =
  `TelemetryIdExpiresAt`).
- `RecentTelemetryIds` (`List<TelemetryIdWindowOutput>`) — the user's retired
  ids still in the map. `TelemetryIdWindowOutput` = `{ Id, ValidFromUtc,
  ValidToUtc }`.

**`SafeExchangeAdminUsers.RunDetail`** maps the three current-id fields from the
`User`, then queries `TelemetryIdMap` `WHERE UserId == user.Id` (single-partition,
efficient), orders by `ValidToUtc` descending, and fills `RecentTelemetryIds`.

**Admin panel — `ManageUser.razor`**: add to the detail `dl`:
- **Telemetry id (current)** → `<code>@CurrentTelemetryId</code>`, with a hint
  line: *"current pseudonym; active since <ActiveSince>, rotates after
  <RotatesAt>."* Render "—" if empty.
- **Recent telemetry ids** → a compact table (id `<code>`, active-from,
  active-to) of `RecentTelemetryIds`. Empty → "none retained (within 28 days)."
  This is the id+window set to paste into App Insights for a reported issue.

`UserDetail` client model (`SafeExchange.Client.Common.Model`) gains the matching
fields + a `TelemetryIdWindow` DTO.

### 4. Feature 2 — find a user by telemetry id

**Backend — new endpoint** `GET v2/admin/users/by-telemetry-id/{telemetryId}`
(handler `SafeExchangeAdminUsers.RunByTelemetryId`, wired in
`SafeExchange.Functions/Functions/SafeAdminUsers.cs` as a new `[Function]`; the
3-segment route does not collide with `users/{upn}`):
- Admin-gated via `GlobalFilters.GetAdminFilterResultAsync` (same as the other
  admin user ops).
- Validate `telemetryId` against `^[0-9a-f]{32}$`; malformed → `400`.
- Resolve, **current first**: `Users.FirstOrDefault(u => u.TelemetryId ==
  telemetryId)`. Miss → query `TelemetryIdMap` cross-partition `WHERE c.id ==
  telemetryId` → if found, load `Users.FirstOrDefault(u => u.Id == entry.UserId)`.
- Found → `200` with the same `UserDetailOutput` as §3 (so the panel can show
  detail directly). Neither → `404 not_found`.

**Admin panel — `Users.razor`**: a dedicated **"Find by telemetry id"** field
below the existing UPN/name search (the user picked the separate-field UX). On
look-up: call `ApiClient.FindUserByTelemetryIdAsync(id)`; on success
`NavigateTo("users/{upn}")`; on `404` show inline:
*"No active user has that telemetry id. Ids older than ~28 days are not retained,
so they can't be resolved."* Client-side trim/lowercase + 32-hex guard before
calling.

`ApiClient` gains `FindUserByTelemetryIdAsync(string telemetryId)`.

### 5. Purge — automatic

Cosmos **container TTL (28 days)** is the only purge mechanism for the normal
lifecycle. Map docs are write-once, so `_ts` = creation = `ValidToUtc`; entries
self-destruct 28 days later. No timer/sweep/job. **Disable ≠ erase** — a disabled
user keeps their map entries (incident attribution may still be needed; TTL still
bounds them).

### 6. User deletion / erasure — deferred, designed-for

SafeExchange has **no user hard-delete** today (admin user management is
enable/disable only). So there is no deletion path to hook. Implications:
- No code needed now; TTL guarantees a (hypothetically) deleted user's entries
  vanish within 28 days regardless.
- Partitioning by `UserId` makes a future per-user erasure a single-partition
  delete — a few lines to add when/if user deletion (or an on-demand
  right-to-be-forgotten action) is introduced. **Explicitly out of scope here.**

### Config & infrastructure

- **ARM** (`deployment/current/arm/services-template.arm.json`): add the
  `TelemetryIdMap` Cosmos container — partition key `/UserId`,
  `defaultTtl = 2419200` — following the existing container definitions. Both
  environments get the container (rotation writes map entries wherever the
  backend runs; the admin-panel UI is what stays staging-only).
- **EF Core** (`SafeExchangeDbContext`): `DbSet<TelemetryIdMapEntry>`; in
  `OnModelCreating`, `ToContainer("TelemetryIdMap")`, `HasPartitionKey(e =>
  e.UserId)`, `HasDefaultTimeToLive(2_419_200)`, key = `Id`.
- **Migration** (`MigrationItem00012`, following the `00011` pattern): backfill
  `TelemetryIdIssuedAt` for existing users where it is unset and `TelemetryId`
  is non-empty, to `TelemetryIdExpiresAt - 7 days` (the start of the current
  calendar week — accurate for calendar-aligned weekly rotation). Idempotent
  (skip users already set). The `TelemetryIdMap` container itself needs no
  backfill — it fills forward as users rotate.

## Testing (red/green TDD)

- **`TelemetryIdRotator`**: returns `Rotated=false` when not due; on first-ever
  creation `Rotated=true` with `RetiredTelemetryId == null`; on a real rotation
  `Rotated=true` with the retired id + correct `ValidFrom`(=old `IssuedAt`)
  `/ValidTo`(=now); sets `TelemetryIdIssuedAt` on the new id.
- **Rotation persistence** (Cosmos emulator): a boundary-crossing request writes
  exactly one `TelemetryIdMapEntry` with the right window and userId; no entry on
  first creation.
- **`SafeExchangeAdminUsers.RunDetail`**: returns current id + active/rotates
  timestamps; `RecentTelemetryIds` lists the user's map entries (newest first);
  empty when none.
- **`RunByTelemetryId`**: resolves a **current** id; resolves a **retired** id
  via the map; `404` when unknown/expired; `400` on malformed input; admin-gated.
- Test fixtures' `OneTimeSetup` create the `TelemetryIdMap` container
  (`DefineContainer` with partition key `/UserId` + TTL).
- Admin panel: no client unit tests exist (consistent with the repo); verify on
  staging.

## Deployment

Backend `func publish safeexchange-staging` (provisions the new container via the
ARM deploy `deployment/deploy.ps1 -Environment test`, runs migration `00012` via
the admops endpoint) + admin panel via `deploy-pwa.ps1` staging. **Staging-only.**
Prod promotion is a later, separately-approved step (it carries the backend
rotation+map changes + the container + migration `00012`; the admin-panel UI
stays staging-only).

## Out of scope

- Client/PWA telemetry; historical telemetry older than the 28-day window;
  explicit per-user map erasure; changing rotation cadence; prod admin panel.
