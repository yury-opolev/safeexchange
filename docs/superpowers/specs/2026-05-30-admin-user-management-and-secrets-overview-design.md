# Admin User Management + Secrets Overview — Design

Date: 2026-05-30
Status: Approved (design), pending implementation plan

## Context & goals

Two related admin-panel improvements, delivered together:

1. **User management consistency** — the Users list currently has inline
   Enable/Disable buttons, inconsistent with the Applications list. Make it
   match: a **Manage** button per row that opens a per-user page (mirroring
   Applications → Manage S2S App), where the admin sees the user's details and
   enables/disables them.
2. **Admin Secrets Overview** — a new admin, read-only, paginated overview of
   **all** secrets showing metadata only (**never content**): name, owner,
   created, last accessed, scheduled/idle deletion, attachment count, tags,
   audit, and **who has access**. Sortable/filterable by last-accessed so an
   admin can surface long-unused secrets that are cleanup candidates.

Primary cross-cutting goal: **stylistic consistency** across the admin panel
(reuse the existing `admin-card` / `admin-pill` / `admin-pager` styles and the
`ApiClient` / `PaginatedResult<T>` conventions).

## Repos & branches

This work spans both repositories. **One feature branch per repo**, same name:

- Backend: `safeexchange` — branch `features/admin-user-and-secrets`
- Frontend: `safeexchange.blazorpwa` — branch `features/admin-user-and-secrets`

API-first: backend endpoints + contract land before/with the frontend wiring.

## Out of scope

- Any **automated cleanup/deletion** action on secrets (this is visibility
  only; a cleanup action is a separate future feature).
- The **rotating telemetry ID** privacy feature (separate spec/branch).
- Microsoft.Graph 5→6 and ApplicationInsights 2→3 upgrades (deferred).
- **Group memberships** on the user detail page (intentionally omitted).
- Ever exposing secret **content**, or attachment **names/sizes** (count only).

---

## Feature 1 — User management consistency

### Backend (`safeexchange`)

- Provide an admin **user-detail** read returning the fields below. During
  planning, first check whether the existing admin user-list payload
  (`UserOverview`) already carries them; if so, reuse it and skip a new
  endpoint. Otherwise add `GET /v2/admin/users/{upn}` (confirm exact route
  prefix against existing admin routes — see SafeExchangeAdminUsers).
- Fields: `id`, `enabled`, `displayName`, `contactEmail`, `aadUpn`,
  `aadObjectId`, `aadTenantId`, `createdAt`, `modifiedAt`,
  `receiveExternalNotifications`, `consentRequired`. **Not** `groups`.
- Reuse the existing enable/disable toggle (backing `SetUserEnabledAsync`).
- Admin-only authorization (same gate as the other admin endpoints).

### Frontend (`safeexchange.blazorpwa`)

- `Pages/Users.razor`: replace the inline Enable/Disable button with a
  **Manage** button → navigate to `users/{upn}` (mirrors `Applications.razor`).
- New `Pages/ManageUser.razor`, mirroring `ManageS2SApp.razor`: header with
  display name + enabled/disabled pill + an Enable/Disable action (with the
  existing `ConfirmDialog`), then a key/value grid of the fields above.
- `ApiClient`: add `GetUserDetailAsync(upn)` if a new endpoint is introduced;
  `SetUserEnabledAsync` already exists.

---

## Feature 2 — Admin Secrets Overview

### Data-model change (backend)

- Add `LastAccessedAt` (**nullable** `DateTime?`) to `ObjectMetadata`.
  Additive and backward-compatible: existing/never-accessed secrets read as
  `null` until first access after deploy.
- Set `LastAccessedAt = DateTime.UtcNow` when a secret's **content is read**
  (the content download path). Reading *metadata/listing* does **not** count as
  access. This is one extra Cosmos write per content read — accepted, additive.
- Index `LastAccessedAt` so Cosmos can `ORDER BY` / `WHERE` on it (EF Core
  Cosmos configuration).

### Backend endpoints (admin-only)

1. **List secrets** (paginated, searchable, sortable, filterable):
   `GET /v2/admin/secret-list?q=&page=&sortBy=&sortDir=&accessedBefore=&neverAccessed=`
   - `q`: substring match on name and/or tag.
   - `sortBy`: `name` | `created` | `lastAccessed` (default `created`).
   - `sortDir`: `asc` | `desc`.
   - `accessedBefore`: ISO date — only secrets last accessed before this.
   - `neverAccessed`: bool — only secrets with `LastAccessedAt == null`.
   - **Null handling:** when sorting by `lastAccessed` ascending (oldest
     first — the cleanup view), `null` (never accessed) sorts as the *oldest*
     (top). `neverAccessed=true` matches exactly those rows.
   - Response item (`SecretAdminOverview`): `objectName`, `createdAt`,
     `createdBy` (owner), `lastAccessedAt`, `expiresAt` (from
     `ExpirationMetadata`, nullable), `idleDeleteAt` (`ExpireIfUnusedAt`),
     `attachmentCount` (`Content.Count(c => !c.IsMain)`), `tags`,
     `auditEnabled`, derived `status` (`active` / `expires <date>` /
     `idle-delete in Nd`). Wrapped in `PaginatedResult<SecretAdminOverview>`.
2. **Secret metadata detail**: `GET /v2/admin/secret/{name}` — full metadata,
   **no content payload, no chunk data**. Returns name, created+by,
   modified+by, `lastAccessedAt`, `expiresAt`, `idleDeleteAt`,
   `attachmentCount`, `tags`, `auditEnabled`, `keepInStorage`.
3. **Secret access list**: `GET /v2/admin/secret/{name}/access` — the
   `SubjectPermissions` rows: `subjectName`, `subjectType` (User|Group),
   `canRead`, `canWrite`, `canGrantAccess`, `canRevokeAccess`.

All three are admin-only (reuse the existing admin authorization used by
`SafeExchangeAdminUsers` / `SafeExchangeAdminApplications`) and use
`PaginationHelper` where paginated.

### Frontend (`safeexchange.blazorpwa`)

- New `Pages/Secrets.razor` — list mirroring `Applications.razor`:
  search box; a **sort** control (Last accessed ↑/↓, Name, Created); a
  **filter** (accessed-before date and a quick "never accessed" toggle);
  status pills; a **Metadata** button → `secrets/{name}`; `admin-pager`.
- New `Pages/SecretDetails.razor`: metadata key/value grid + a **"Who has
  access"** table (subject, type, Read/Write/Grant/Revoke), and an explicit
  "secret content is never retrievable here" note. **Attachment count only —
  no attachment names/sizes; never content.**
- Add a **Secrets** card to the admin `Index.razor` and the admin nav.
- `ApiClient` + DTOs: `ListSecretsAsync(q, page, sortBy, sortDir,
  accessedBefore, neverAccessed)`, `GetSecretDetailAsync(name)`,
  `GetSecretAccessAsync(name)`.

---

## Testing (red → green TDD)

Implementation uses `subagent-driven-development`; each task is test-first.

**Backend (unit + Cosmos-emulator integration):**
- `attachmentCount` excludes the `IsMain` content item.
- `LastAccessedAt` is set on content read, and **not** on metadata/listing.
- Secrets list: sort by `lastAccessed` asc puts `null` first; `neverAccessed`
  filter returns only null rows; `accessedBefore` filter boundary; pagination.
- Access list returns correct permission flags and subject types.
- Admin authorization required on all new endpoints (non-admin → forbidden).
- User detail returns the expected fields and **omits groups**.

**Frontend (component tests, matching existing patterns):**
- `Secrets` list renders pills and wires sort/filter to the API call.
- `SecretDetails` renders the access table and never exposes content.
- `ManageUser` renders the fields and the enable/disable action.

## Deploy plan

Per the established workflow, after the spec → plan → implementation:
- Backend: `func azure functionapp publish safeexchange-staging` → verify →
  `safeexchange-backend` (prod).
- Frontend: `deploy-pwa.ps1 -Environment test` → verify → `prd`.
- Backend ships first (frontend depends on the new endpoints).

## Open confirmations (resolve in planning)

- Exact "access" trigger for `LastAccessedAt`: **proposed = content download**
  (not metadata GET). Confirm during planning.
- Whether the existing admin user-list payload already carries all detail
  fields (to avoid adding a redundant user-detail endpoint).
- Exact admin route prefixes (align with existing `SafeExchangeAdmin*` routes).
