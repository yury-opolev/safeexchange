# Spike: Self-Service S2S Apps + Admin Panel

> Scope: **never deployed.** Lives on branch `spike/s2s-apps` in both
> `safeexchange` and `safeexchange.blazorpwa`. The spike's job is to validate the
> end-to-end design with a local-runnable stack. If it proves out, pieces can be
> graduated to `feature/*` branches and shipped through the normal pipeline.

## The two halves

This spike has two halves on the same branch:

1. **S2S apps** — a self-service surface so end users can register an Entra-app
   subject themselves (no admin needed), with an enforced co-ownership rule.
2. **Admin panel** — a **separate static web app** (its own Blazor WASM project)
   that surfaces the existing admin API and the new admin-only operations from
   this spike. Same Blazor/Bootstrap stack as the main site, distinct visuals,
   mobile-first.

Both halves share the same backend (`safeexchange` repo) on `spike/s2s-apps`.

## What was already in the codebase before this spike

- `SafeExchange.Core.Model.Application` — entity with `DisplayName`, `AadTenantId`,
  `AadClientId`, `ContactEmail`, `Enabled`, `ExternalNotificationsReader`,
  `CreatedBy` (free-text). Stored in Cosmos container `Applications`.
- `SubjectType.Application` — the auth pipeline already maps a token's
  `(tenantId, appId)` to an `Application` row's `DisplayName` and treats it as a
  subject for `SubjectPermissions` (see `SubjectHelper.GetSubjectInfoAsync`).
  **S2S authentication is plumbed end-to-end already.**
- `Admin/SafeExchangeApplications.cs`, `Admin/SafeExchangeAdminGroups.cs`,
  `Admin/SafeExchangeAdminOperations.cs`, `Admin/SafeExchangeWebhookSubscriptions.cs`
  — admin-gated CRUD, not surfaced anywhere in the web client today.
- `SafeExchangeApplicationsList.cs` — non-admin endpoint that lists *all* apps.
  Will be retired in favour of the new owner-scoped `GET /s2sapps/mine`.
- Audit: `SecretAuditAnchor` + `SecretAuditEvent` already retain events
  independently of secret deletion (anchors persist past secret purge), so
  historical audit-after-delete works at the storage layer; we just need an
  admin-surface query.

## Decisions for this spike

### Data: `ApplicationOwner` (new)

Many-to-many between `Application` and a principal (User OID **or** Group OID).
New Cosmos container `ApplicationOwners`.

```csharp
public enum OwnerSubjectType { User = 0, Group = 1 }

public class ApplicationOwner
{
    public string Id { get; set; }              // {SubjectType}:{SubjectId}
    public string PartitionKey { get; set; }    // = ApplicationId
    public string ApplicationId { get; set; }   // FK to Application.Id
    public OwnerSubjectType SubjectType { get; set; }
    public string SubjectId { get; set; }       // user UPN/OID or group OID
    public DateTime AddedAt { get; set; }
    public string AddedBy { get; set; }
}
```

Composite EF key `{ ApplicationId, SubjectType, SubjectId }`. Partition by
`ApplicationId` so all owners of one app live in one partition (typical reads
are "list the owners of X" and "is principal Y an owner of X").

A user appears as an owner if they are listed directly **or** if any of their
groups (resolved via the existing `GroupDictionary` plumbing) is listed.

### Ownership invariant (clarified)

An S2S app must always satisfy one of these:

- **≥ 2 distinct user owners**, or
- **≥ 1 user owner AND ≥ 1 group owner**.

Equivalently: the owner set must contain ≥ 2 principals, and **at least one must
be a User** (so two-groups-only is not allowed — guarantees a human accountable
for the app). Enforced server-side on register and on owner-remove. Admin-created
apps follow the same rule.

For self-service, the caller is automatically owner #1 (a User), so the form just
needs them to supply one more principal (user OR group) before it submits.

### "Just a GUID" — minimum user input

Existing `Application` requires several fields. We sensibly default everything
the caller doesn't truly need to choose:

| Field         | Sourcing                                                              |
|---------------|-----------------------------------------------------------------------|
| `DisplayName` | user supplies                                                         |
| `AadClientId` | user supplies (the Entra app's client GUID)                           |
| `AadTenantId` | defaulted to the **caller's home tenant** (read from the token)        |
| `ContactEmail`| defaulted to the **caller's UPN / email** (read from the token)        |
| 2nd owner     | user picks via the existing user/group search                          |

Power users can override; the typical user types the display name, pastes the
Entra client id, and picks a co-owner.

### Authorisation matrix

| Operation                                  | Who can do it                                       |
|--------------------------------------------|-----------------------------------------------------|
| Register a new app (self-service)          | Any authenticated user (becomes owner #1)           |
| Read/list owners                           | Any owner of that app                               |
| Update fields (email, etc.)                | Any owner of that app                               |
| Add/remove an owner                        | Any owner of that app (keeps the ≥2 / user invariant)|
| Delete an app                              | Any owner — **or admin if the admin is an owner**   |
| Enable / disable an app                    | Any owner — or admin                                |
| **Register an app (admin-side)**           | Admin (no quota; admins can register as many as they want) |
| **Delete a user-owned app**                | **No** — admins cannot delete apps they don't own; they may only enable/disable. Forces user co-owners to confirm a removal. |
| **List ALL apps** (registry-wide)          | Admin only                                          |
| **Filter apps** by name or client-id GUID  | Admin only (admin panel)                            |
| **List all users**                         | Admin only                                          |
| **Filter users** by name / UPN             | Admin only                                          |
| **Enable / disable a user**                | Admin only                                          |
| **Search audit by secret name (substring)**| Admin only — returns events even for purged secrets |

## Endpoints introduced by this spike

### Self-service (regular auth gate)

```
GET    /s2sapps/mine                            list apps where caller is owner
POST   /s2sapps                                  register (caller = owner #1)
GET    /s2sapps/{displayName}                    detail (owner-only)
PATCH  /s2sapps/{displayName}                    update fields (owner-only)
DELETE /s2sapps/{displayName}                    delete + owner rows (owner-only)
GET    /s2sapps/{displayName}/owners              list owners (owner-only)
POST   /s2sapps/{displayName}/owners              add owner (owner-only)
DELETE /s2sapps/{displayName}/owners/{principal}  remove owner (owner-only; keeps invariant)
```

### Admin-only (admin gate, used by the admin panel)

```
GET    /admin/users?q=&page=&pageSize=          paginated user search (name/UPN substring)
PATCH  /admin/users/{upn}/enabled                enable/disable a user

GET    /admin/applications?q=&page=&pageSize=    paginated app search (name OR clientId GUID substring)
PATCH  /admin/applications/{displayName}/enabled enable/disable an app (overlaps existing PATCH, but a focused endpoint reads cleanly in the panel)

GET    /admin/audit?secretName=&page=&pageSize=  audit events by secret-name substring, including historical/purged-secret anchors
```

Existing admin CRUD for applications (`Admin/SafeExchangeApplications.cs`) stays
as the admin's "create / hard-delete one of their own" surface.

Pagination: I'll use simple `page` / `pageSize` query params (continuation tokens
would be more Cosmos-native, but the spike doesn't need that scale). Default
`pageSize=25`, max 100.

Users currently have no `Enabled` field on `User` (verify in the next turn);
if not, this spike adds it.

## Frontend layout — the two halves

### Main PWA (`SafeExchange.PWA`) gains one page

- `/s2sapps` — "My S2S Apps": list + register + per-row manage. Plus a NavMenu
  link that shows when authenticated.
- No admin pages here — admin lives in a separate project.

### NEW project: `SafeExchange.AdminPanel`

A second Blazor WebAssembly PWA in the `safeexchange.blazorpwa` repo, peer of
`SafeExchange.PWA`. Same MSAL config (same client id, separately published —
served from a different origin in eventual prod, e.g. `admin.safeexchange.dk`).
References `SafeExchange.Client.Common` (the API client) but **not**
`SafeExchange.Client.Web.Components` — admin uses its own focused components so
the visual language can diverge cleanly.

Routes:

- `/` — dashboard (counts: users, apps, recent audit, etc.).
- `/users` — paginated user list + search + enable/disable toggle.
- `/applications` — paginated app list + search by name or client-id GUID
  + per-row enable/disable + (admin-as-owner) delete.
- `/audit` — search audit by secret name substring (historical, paginated).

Visual direction (locked in here so the implementation turn doesn't redebate):

- **Dark by default** (admin overrides the user's theme preference while inside
  the admin panel; main site keeps its own).
- **Accent:** desaturated cyan/teal `--admin-accent: #36b1bf` (or similar) — no
  bright primary blue; admin should feel utilitarian.
- **Mobile-first:** single-column on phones, table → cards transformation. Top
  nav with hamburger menu and a permanent `ADMIN` chip so it's impossible to
  mistake an admin tab for the user app.
- Same Bootstrap 5 + Bootstrap-Icons foundation — re-themed via CSS variables.

## Migration of existing Applications

Existing `Application` rows in prod were registered through the admin API. They
have no formal ownership today — only a free-text `CreatedBy` (typically
`"User someone@example.com"` or `"Admin someone@example.com"`). Once ownership
becomes a hard invariant, every existing app must be reconciled.

**The spike doesn't deploy anywhere, so it doesn't touch production data.** But
the migration logic has to ship with the feature so that *whenever* this graduates
to a feature branch, the prod rollout is one-shot.

### Migration class — `SafeExchange.Core/Migrations/ApplicationOwnershipMigration.cs`

Idempotent, runnable as:
- a one-off admin function (`POST /admin/migrate-applications`), invoked manually
  during the prod cutover, **or**
- programmatically in tests / local-bring-up to seed the local DB.

Pseudocode:

```csharp
foreach (app in dbContext.Applications)
{
    if (await OwnersAlreadyExist(app.Id)) continue;          // idempotent

    // 1. Parse the legacy `CreatedBy` string.
    //    Format observed: "{SubjectType} {SubjectId}" -- e.g.
    //    "User alice@contoso.com" or "Admin bob@contoso.com".
    var parsed = ParseLegacyCreatedBy(app.CreatedBy);

    if (parsed.IsUser)
    {
        // 2. First owner = the human who created it.
        AddOwner(app.Id, OwnerSubjectType.User, parsed.SubjectId, addedBy: "migration");
    }

    // 3. The legacy data gives us at most one owner — the invariant
    //    requires two. Flag the app so the admin panel surfaces it as
    //    "needs second owner" and the app's owner sees a top-of-page
    //    prompt next time they open /s2sapps.
    app.OwnersAttentionRequired = true;
    if (parsed.IsUser == false)
    {
        // CreatedBy was "Admin …" or unparseable — no owner exists yet.
        // App stays Enabled but without an owner; admin must assign one
        // in the admin panel (Applications → "Migrated, unassigned" view).
        app.MigrationStatus = MigrationStatus.NeedsAssignment;
    }
}
```

New fields on `Application` (small, additive):

- `bool OwnersAttentionRequired` — set true on migrated rows, cleared once a
  second owner is added through the regular `/s2sapps/{name}/owners` flow.
- `MigrationStatus MigrationStatus` (enum: `None`, `OwnerAssigned`,
  `NeedsAssignment`) — strictly metadata for the admin panel's filtered view.

### Behaviour while a migrated app is below the invariant

- The **app keeps working** (existing S2S clients keep getting tokens / hitting
  the API) — we never break running prod traffic on rollout day.
- The first owner sees a banner on `/s2sapps`: "This app needs a second owner —
  add one to keep it active beyond {grace_period}."
- Admin panel has a saved filter: `Applications → Needs attention` (where
  `OwnersAttentionRequired || MigrationStatus == NeedsAssignment`).
- A grace period (e.g. 30 days) is *advisory* in the spike — no enforcement.
  Whether/how to auto-disable after the grace period is a separate decision and
  documented as a follow-up.

### Why not "just pick any admin as the 2nd owner"

That would silently grant a stranger admin authority over someone else's app and
violate the principle that ownership is consent-based. The migration honours
the original creator and asks them to nominate the second owner.

### Spike-local migration

For local dev, the migration class is also wired so that `func host start`
seeds a couple of test Application + ApplicationOwner rows (driven by env
flag `SEED_TEST_APPS=true`). That makes the local UI testable end-to-end
without having to register Entra apps by hand.

## Scope split (what this turn delivers vs. what's next)

This turn (the foundation):

- ✅ Design doc (this file).
- ✅ Spike branches in both repos, pushed to origin so they're visible.
- ⏳ Backend: `ApplicationOwner` entity + DbContext registration.
- ⏳ Backend: skeleton of `SafeExchangeS2SApps` self-service function.
- ⏳ Frontend: stub `/s2sapps` page in the main PWA + ApiClient method.

Next turn(s):

- Backend: finish the self-service endpoints + the new admin endpoints
  (paginated user list, paginated app list, audit search).
- Backend: ownership-aware authorisation everywhere (delete app, manage owners).
- Backend: tests using the existing `SafeExchange.Tests` patterns.
- Frontend: scaffold `SafeExchange.AdminPanel` as a new WASM project — same MSAL
  config, distinct theme/layout, mobile-first.
- Frontend: implement the admin pages (users, applications, audit).
- Frontend: finish the `/s2sapps` UX (register form, owner management).
- Local dev story documented end-to-end (which dev-auth, which Cosmos emulator
  config — borrow from the `spike/images-as-attachments` branch).

## Running locally (target end-state)

- **Backend** (`safeexchange`, branch `spike/s2s-apps`): Cosmos emulator +
  Azurite + `func host start` from `SafeExchange.Functions/`. Use the
  dev-auth bypass from the `spike/images-as-attachments` branch to log in as a
  test account without real Entra.
- **Main PWA** (`safeexchange.blazorpwa`, branch `spike/s2s-apps`):
  `dotnet run --project SafeExchange.PWA`. The `/s2sapps` link appears in the
  nav once signed in.
- **Admin panel**: `dotnet run --project SafeExchange.AdminPanel` once
  scaffolded. Separate origin (different port).

## Notes for the next session

- Cosmos container creation: EF Cosmos provider auto-creates on first use, so
  adding `ApplicationOwners` to the context is enough; no migration required.
- The existing `User` model: check whether it has an `Enabled` field before the
  admin-user-enable endpoint is wired. Add the field if missing.
- For the audit search, `SecretAuditAnchor` keys by `AuditInstanceId`, not by
  secret name — to search by name we need an index/lookup. Cheapest: scan
  `SecretAuditAnchor`'s name field (it should hold the secret name; verify) and
  page through results. If absent, we add a `SecretName` field to the anchor
  going forward and back-fill once.
- The "≥2 owners with at least one user" invariant must be checked atomically
  with owner-remove — the existing code uses `dbContext.SaveChangesAsync` so a
  read-then-write race is possible. The spike accepts that; a follow-up can use
  optimistic concurrency / a stored proc.
