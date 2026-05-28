# Implementation plan — spike/s2s-apps

> Companion to `docs/SPIKE-s2s-apps.md`. Executed task-by-task with **red/green
> TDD** (failing test → minimal implementation → refactor → commit) and a
> **code-reviewer subagent** dispatched between phases. Working autonomously —
> no user gates between tasks. Spike branches only; nothing is ever deployed.

## Settings (Key Vault, ARM-parameter-driven, per project convention)

Anything naturally configurable goes through the existing
`settings-features-X` → KV secret `Features--X` → `Features:X` pattern (see how
`UseAccessGiveUp` is wired). New settings the spike introduces:

| Setting                                  | Default | Purpose                                          |
|------------------------------------------|---------|--------------------------------------------------|
| `Features:S2SAppsSelfService`            | `false` | Master flag for the new self-service surface     |
| `Features:RequireApplicationOwnership`   | `false` | Enforce the ≥2-owner-with-user invariant         |
| `Limits:AdminListDefaultPageSize`        | `25`    | Default `pageSize` for admin paginated lists     |
| `Limits:AdminListMaxPageSize`            | `100`   | Hard cap                                          |
| `Limits:OwnerlessGracePeriodDays`        | `30`    | Advisory grace for migrated apps below invariant |

All defined in `Features.cs` / a new `Limits.cs` config class, wired through
the IOptions pattern. Param files (`services-parameters-{test,prd}.arm.json`)
stay at `False` for these features so prod doesn't accidentally enable them —
the spike branch flips them via local appsettings.

## Phase A — Backend foundation (`safeexchange` repo)

### A1. `ApplicationOwner` entity + Cosmos container

- **New:** `SafeExchange.Core/Model/ApplicationOwner.cs`, enum `OwnerSubjectType`.
- **Edit:** `SafeExchangeDbContext` — `DbSet<ApplicationOwner>` + EF config
  (container `ApplicationOwners`, composite key, partition by `ApplicationId`).
- **Tests (red first):** add to `SafeExchange.Tests` —
  `ApplicationOwnerSchemaTests.CanRoundtripOwner_AgainstInMemory`,
  `OwnerKey_IsCompositeAndUnique`.

### A2. `User.Enabled` field (+ small migration shim)

- Add `Enabled` (default `true`) to `User`. EF auto-handles new field.
- Tests: existing users default to enabled; can be toggled.

### A3. KV-backed settings classes

- Add `Features.S2SAppsSelfService`, `Features.RequireApplicationOwnership`,
  new `Limits` class with the three int settings. Register in DI. Tests:
  default values match table above when no overrides.

### A4. Self-service endpoints — `SafeExchange.Core/Functions/SafeExchangeS2SApps.cs`

One function class, methods per verb following the existing pattern (see
`SafeExchangeAccessGiveUp`). Endpoints listed in the design doc. Auth via
`GlobalFilters.GetFilterResultAsync` (regular, not admin). Returns 404 /
`Status="disabled"` shape when `Features:S2SAppsSelfService=false` (same shape
used by give-up so the client handles it uniformly).

Tests (red first, one per behaviour):
- Caller becomes owner #1 on register.
- Register refuses without a 2nd owner.
- Register refuses if proposed owners violate "at least one User".
- Non-owner gets 403 on read/update/delete.
- Owner can add/remove owners, but removal that would drop the count below 2
  or leave zero User owners returns 409.

### A5. Admin endpoints (paginated, searchable)

- `SafeExchange.Core/Functions/Admin/SafeExchangeAdminUsers.cs` —
  `GET /admin/users?q=&page=&pageSize=`, `PATCH /admin/users/{upn}/enabled`.
- Extend `SafeExchange.Core/Functions/Admin/SafeExchangeApplications.cs` with
  `GET /admin/applications?q=&page=&pageSize=` and a focused
  `PATCH /admin/applications/{name}/enabled`.
- `SafeExchange.Core/Functions/Admin/SafeExchangeAdminAudit.cs` —
  `GET /admin/audit?secretName=&page=&pageSize=` over `SecretAuditAnchor` /
  `SecretAuditEvent`; explicitly includes anchors for purged secrets.
- Pagination contract: `{ items, page, pageSize, total, hasMore }`.
- Tests for: pagination math, filter is substring + case-insensitive, admin
  gate refuses non-admins, enable/disable flips the field.

### A6. `ApplicationOwnershipMigration` class

- `SafeExchange.Core/Migrations/ApplicationOwnershipMigration.cs`. Idempotent
  scan + parse of `CreatedBy`. Sets `OwnersAttentionRequired` / `MigrationStatus`.
- Tests: empty DB → no-op; pre-existing Application with no owners → owner
  created + flag set; second invocation → no-op (idempotency).
- Manual trigger: `POST /admin/migrate-applications` (admin-only function).
- Local seed shim (env `SEED_TEST_APPS=true`) creates two test apps so the
  local UI is testable right after `func host start`.

### Phase A code review

Dispatch the **code-reviewer subagent** with `BASE_SHA = main HEAD` and
`HEAD_SHA = end of Phase A`, plan reference = this doc. Apply fixes.

## Phase B — Frontend main PWA (`safeexchange.blazorpwa` repo, `SafeExchange.PWA`)

### B1. ApiClient methods for `/s2sapps` and admin endpoints

- `SafeExchange.Client.Common/ApiClient/ApiClient.S2SApps.cs` (partial class) —
  one method per endpoint. Tests via the existing
  `SafeExchange.Client.Common.Tests` pattern using `StubHttpClientFactory`.

### B2. `/s2sapps` page

- `SafeExchange.PWA/Pages/MyS2SApps.razor` (list + register form + per-row
  manage). Reuses the existing user/group search components for picking owners.
- NavMenu link shown when authenticated.
- Tests: a small bUnit-style render test for the list and the validation
  ("must add at least one more owner") on the register form.

### Phase B code review

## Phase C — Admin panel project (new WASM app, separate static web app)

### C1. Scaffold `SafeExchange.AdminPanel`

- New project peer of `SafeExchange.PWA`. Files: csproj, `Program.cs`,
  `App.razor`, `_Imports.razor`, `wwwroot/index.html`, `wwwroot/appsettings.json`,
  `wwwroot/css/admin.css`, `Shared/AdminLayout.razor`.
- References `SafeExchange.Client.Common` (API client). Does NOT reference
  `SafeExchange.Client.Web.Components` — admin owns its own components so the
  visual language can diverge cleanly.
- MSAL config: same client id, separate scope acquisition. `Program.cs` mirrors
  `SafeExchange.PWA/Program.cs` and `ServicesHelper`.

### C2. `AdminLayout` + base theme

- Bootstrap 5 + Bootstrap-Icons (same dependencies).
- CSS: `[data-bs-theme="dark"]` baseline; admin accent
  `--admin-accent: #36b1bf`; mobile-first single column; table-to-cards
  transformation; permanent `ADMIN` chip in the top bar.
- Tests: snapshot of the layout markup; theme variables present.

### C3. `/users` page

- Paginated grid, search-as-you-type (debounced) hitting `/admin/users`.
- Per-row enable/disable toggle with confirmation.
- Tests: search debounce, page navigation, toggle calls correct endpoint.

### C4. `/applications` page

- Same shape as users; search accepts name OR client-id GUID substring
  (server-side decides which field to match by regex).
- Per-row: enable/disable always; **delete** only when the current admin is an
  owner of that app; **register** action button.
- Tests: filter routing; delete-button visibility logic; register flow.

### C5. `/audit` page

- Search by secret-name substring; results include historical (purged-secret)
  anchors. Pagination.
- Tests: search returns historical anchors when the secret no longer exists.

### Phase C code review

## Phase D — Local-runnable bring-up

- README at `docs/SPIKE-s2s-apps-LOCAL.md`: prerequisites (Cosmos emulator,
  Azurite, .NET SDK), commands to launch backend + main PWA + admin panel.
- Borrow the dev-auth bypass and local-backend tooling from the
  `spike/images-as-attachments` branch — cherry-pick the LocalDev harness onto
  `spike/s2s-apps` (this branch is never deployed, so the local-only auth
  bypass is acceptable).
- Smoke checklist: register an app from main PWA, see it in admin panel,
  disable it from admin, observe self-service detail showing "disabled" banner.

## Process notes

- **TDD discipline:** for every task, write the failing test first, watch it
  fail with a clear reason, implement the minimum to pass, refactor if needed,
  commit. Each task = at least one tightly-scoped commit.
- **Code-reviewer subagent:** dispatched at the end of each Phase (A/B/C). Apply
  fixes before starting the next phase. Critical issues block; Important issues
  get fixed; Minor noted for future.
- **Loose coupling:** the new entity, the migration, the self-service
  endpoints, the admin endpoints and the admin panel each live in their own
  files / projects. The admin panel never imports `SafeExchange.Client.Web.Components`,
  so its visual language can evolve independently.
- **Settings:** any threshold, page-size default, grace period, or feature
  flag goes through the KV/ARM/`Features:` pipeline — `appsettings.json` only
  carries non-production defaults.
- **Deployment:** none. The spike is never built into `deploy-pwa.ps1` or the
  backend deploy script. The admin panel project is added to the solution but
  *not* added to any deploy target.
