# Rotating Telemetry ID (telemetry privacy) — Design

Date: 2026-05-30
Status: Approved (design), pending implementation plan
Branch: `features/rotating-telemetry-id` (safeexchange backend repo)

## Goal

Stop emitting real user identifiers — UPN, email, Entra object id (oid),
display name, tenant id — into Application Insights telemetry. Instead,
correlate a user's telemetry to a **pseudonymous "telemetry ID"** that
**rotates weekly**, so a user cannot be tracked across time.

## Scope

Backend only (SafeExchange Azure Functions). **Out of scope:** client/PWA
telemetry; telemetry already emitted historically; an admin UI to look up a
telemetry id.

## Design

### 1. Data model — `User`

Add to `SafeExchange.Core/Model/User.cs`:
- `TelemetryId` (string, default `string.Empty`) — current pseudonym, a GUID
  in `"n"` format.
- `TelemetryIdExpiresAt` (DateTime) — UTC instant at/after which it must rotate.

Backward-compatible: existing user documents deserialize with empty/default
values. A **migration backfills all existing users** (see §6) so every document
carries the fields immediately; the lazy rotation path (§2) then handles any
user created between the migration pass and their next request, plus all
ongoing weekly rotation.

### 2. Rotation — lazy, on-demand, calendar-aligned weekly

A pure, unit-testable component `TelemetryIdRotator` (`SafeExchange.Core/Telemetry/`):

- `bool EnsureCurrent(User user, DateTime nowUtc)` — if
  `string.IsNullOrEmpty(user.TelemetryId)` **OR** `nowUtc >= user.TelemetryIdExpiresAt`:
  - `user.TelemetryId = Guid.NewGuid().ToString("n")`
  - `user.TelemetryIdExpiresAt = NextWeekBoundaryUtc(nowUtc)`
  - return `true` (caller persists). Otherwise return `false`.
- `static DateTime NextWeekBoundaryUtc(DateTime nowUtc)` — the **start of the
  next Monday, 00:00:00 UTC**, strictly after `nowUtc`'s date:
  ```csharp
  var date = nowUtc.Date;
  int days = ((int)DayOfWeek.Monday - (int)date.DayOfWeek + 7) % 7;
  if (days == 0) { days = 7; }            // today is Monday -> next Monday
  return DateTime.SpecifyKind(date.AddDays(days), DateTimeKind.Utc);
  ```
  The boundary day (`Monday`) and zone (`UTC`) are constants, easy to change.

**Lazy:** invoked per request where the authenticated `User` is resolved
(`TokenMiddlewareCore`, after the user lookup/create). When it returns `true`,
the existing `SaveChanges` on that path persists the rotation — **one small
write only when a user crosses the boundary**. There is **no scheduled job and
no Users-table sweep**, so no CPU/RU spike. Inactive users are never touched;
their id rotates on their next request.

**Why calendar-aligned:** every user's id flips at the same Sunday→Monday
instant, so there's no per-user *timing* signal to fingerprint with either.

`EnsureCurrent` takes `nowUtc` as a parameter (caller passes
`DateTimeProvider.UtcNow`) so tests can pin time via the existing
`DateTimeProvider.UseSpecifiedDateTime`.

### 3. Per-request stash + stamping

- `TelemetryContext` — an `AsyncLocal<string?>` holding the current request's
  telemetry id, mirroring `SessionCorrelationMiddleware.Current`. Set right
  after rotation in the auth path; restored to the previous value at request end.
- `TelemetryIdTelemetryInitializer : ITelemetryInitializer` — stamps
  `saex.telemetryId` on **every** telemetry item from `TelemetryContext.Current`
  (mirrors `SessionCorrelationTelemetryInitializer`: no-op when empty; never
  overwrites an existing value). Registered in
  `SafeExchangeStartup.ConfigureServices`.

### 4. Source instrumentation fix — emit the telemetry id, not identity

Update the log statements that currently interpolate UPN / email / oid /
display-name / tenant-id into **message text** so they no longer contain those
values — replace with the telemetry id (from `TelemetryContext.Current`) where a
"who" reference is useful, or drop it where it adds nothing. Correlation is
preserved by the stamped `saex.telemetryId` dimension. Known hotspots:
- `TokenMiddlewareCore` — `user.AadUpn`, `AadObjectId`, `AadTenantId` in the
  group-sync logs, and the "Principal … is authenticated" trace.
- Every handler's `"… by {subjectType} {subjectId} …"` pattern (`subjectId` is
  the UPN) — the bulk of occurrences.
- `ContactEmail` / `Mail` validation logs.

`subjectId`/UPN is still used for **business logic** (permissions, ownership) —
only its appearance in **log messages** changes.

### 5. PII-redaction safety net — feature-flagged

`PiiRedactionTelemetryProcessor : ITelemetryProcessor` (`SafeExchange.Core/Telemetry/`):
- Gated by a feature flag `Features.RedactTelemetryPii` (bool), read **live**
  via `IOptionsMonitor<Features>` on each `Process(...)`, so it can be toggled
  through Key Vault **without a redeploy** (the config provider's reload interval
  applies).
- **Disabled** → pure pass-through (just call the next processor).
- **Enabled** → for `TraceTelemetry` / `ExceptionTelemetry` message text:
  short-circuit on `message.IndexOf('@') >= 0`, then apply a single **linear,
  non-backtracking** email/UPN regex and replace matches with `[redacted]`.
  - Deliberately does **not** touch GUIDs (oid / tenant / secret ids / the
    `telemetryId` itself) or display names — those rely on the source fix (§4).
    GUID redaction would gut legitimate diagnostics.
- Always registered; behavior controlled by the flag. Then it forwards to the
  next processor in the chain.

**Runtime cost:** a bool check when disabled; when enabled, a fast `'@'` scan
per item and a regex only on the rare messages that contain `@`. Microseconds;
negligible.

### 6. Migration — backfill existing users

Add a numbered migration following the existing pattern in
`SafeExchange.Core/Migrations/` (a `MigrationItemNNNNN` model class plus a pass
in `MigrationsHelper`; the latest is `MigrationItem00010`, and
`MigrationItem00007_User` is the User-shaped precedent — read both before
writing the new one). The new migration (next free number, e.g.
`MigrationItem00011`) iterates the **Users** container and, for each user with
an empty `TelemetryId`, sets:
- `TelemetryId = Guid.NewGuid().ToString("n")`
- `TelemetryIdExpiresAt = TelemetryIdRotator.NextWeekBoundaryUtc(DateTimeProvider.UtcNow)`

and persists. **Idempotent:** users that already have a non-empty `TelemetryId`
are skipped, so the pass is safe to re-run. It runs via the **existing**
migrations trigger (the same mechanism `MigrationItem00010` uses — do not invent
a new one).

### Config & per-environment enablement

- `Features.RedactTelemetryPii` (bool) — a **Key Vault secret** per the project
  invariant (all flags via Key Vault). **Default: false.**
- **Enable in staging, NOT in prod (yet):** set the Key Vault secret to `true`
  in the staging vault; leave it `false`/absent in prod. Wire the flag into the
  `Features` options class and add the secret to the deployment (ARM/KV) for
  staging.

### Admin reverse-mapping (note, not built here)

Because the **current** `TelemetryId` is stored on the user, an admin could map
a *current* telemetry id → user for a live incident (e.g. a future admin
lookup). After rotation the old id is unrecoverable — that is the privacy
guarantee. Surfacing the current id on the admin user-detail page is an optional
follow-up, **out of scope** here.

## Testing (red/green TDD)

- `TelemetryIdRotator`:
  - empty id → generates an id and sets expiry to next Monday 00:00 UTC.
  - `nowUtc < expiry` → unchanged, returns `false`.
  - `nowUtc >= expiry` → new (different) id + new expiry, returns `true`.
  - `NextWeekBoundaryUtc` for each weekday, including Monday and exactly-on-boundary.
- `TelemetryIdTelemetryInitializer`: stamps when set; no-op when empty; does not
  overwrite an existing `saex.telemetryId`.
- `PiiRedactionTelemetryProcessor`: redacts email/UPN in message text; leaves
  clean text intact; does **not** redact GUIDs / the telemetry id; pure
  pass-through when the flag is disabled; always forwards to the next processor.
- Integration (where feasible against the Cosmos emulator): an authenticated
  request resolves the user, populates `TelemetryContext`, and persists a
  rotation when the boundary is crossed.

## Deployment

Backend only. ARM/Key Vault: add the `RedactTelemetryPii` flag (staging = true,
prod = false). Then `func publish` → **staging** → verify (telemetry shows
`saex.telemetryId`; no UPN/email in message text; redaction active in staging) →
**prod** (redaction flag stays off in prod for now). The §6 migration runs via
the existing migrations trigger and backfills the new `User` fields.

## Out of scope

- Client/PWA telemetry; historical telemetry; admin UI for telemetry-id lookup;
  redacting oid/tenant/display-name via the runtime processor (handled by §4).
