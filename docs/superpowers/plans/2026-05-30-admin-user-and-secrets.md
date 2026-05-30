# Admin User Management + Secrets Overview — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the admin Users list consistent with Applications (Manage button → per-user page) and add a metadata-only admin Secrets Overview (paginated/sortable/filterable by last-accessed; details + who-has-access; never content).

**Architecture:** Cross-repo. Backend (`safeexchange`) adds a nullable `ObjectMetadata.LastAccessedAt` (set on content read) and three admin-only endpoints, following the existing `SafeExchangeAdminApplications` handler + `SafeAdminApplications` worker-wiring pattern. Frontend (`safeexchange.blazorpwa`) adds `ManageUser`, `Secrets`, and `SecretDetails` pages mirroring `Applications.razor` / `ManageS2SApp.razor`, plus `ApiClient` methods. Backend ships first.

**Tech Stack:** .NET 10 isolated Azure Functions, EF Core Cosmos, NUnit + Cosmos emulator (vnext-preview, `PROTOCOL=https`, conn string in user-secrets), Blazor WASM, Bootstrap.

**Branches:** `features/admin-user-and-secrets` in BOTH repos (already created in backend).

**Conventions to follow (read these first):**
- Backend handler: `SafeExchange.Core/Functions/Admin/SafeExchangeAdminApplications.cs` (admin gate `globalFilters.GetAdminFilterResultAsync`, `ActionResults.TryCatchAsync`, `PaginationHelper.Parse`, `PaginatedResult<T>`, `BaseResponseObject<T>`, `DateTimeProvider.UtcNow`, Cosmos client-side grouping).
- Worker wiring: `SafeExchange.Functions/AdminFunctions/SafeAdminApplications.cs` (`[Function]` + `[HttpTrigger(... Route=$"{Version}/...")]`, `request.FunctionContext.GetPrincipal()`).
- Frontend list: `SafeExchange.AdminPanel/Pages/Applications.razor`; manage page: `Pages/ManageS2SApp.razor`; client: `SafeExchange.Client.Common/ApiClient.cs` (`ListApplicationsAsync`, `ListUsersAsync`, `SetUserEnabledAsync`).
- Tests: `SafeExchange.Tests/Tests/GroupsTests.cs` (Cosmos-emulator integration shape, `CosmosTestOptions.UseGateway`).

**Cosmos emulator for tests:** `docker run -d --name saex-cosmos -p 8081:8081 -p 1234:1234 -e PROTOCOL=https mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview`; wait for `https://localhost:8081/` to return 200.

---

## Task 0: Resolve planning confirmations (read-only, no code)

- [ ] **Step 1:** Read `SafeExchange.Core/Model/Dto/Output/UserOverviewOutput.cs` (or wherever `ListUsersAsync` maps) and `SafeExchangeAdminUsers.cs`. Determine whether the user-list payload already returns `id`, `aadObjectId`, `aadTenantId`, `contactEmail`, `createdAt`, `modifiedAt`, `receiveExternalNotifications`, `consentRequired`. Record finding in the task notes. If all present, **skip Task 8's new endpoint** and have `ManageUser.razor` fetch via a per-UPN list query or a small detail endpoint; if absent, implement Task 8.
- [ ] **Step 2:** Read `SafeExchange.Core/Functions/SafeExchangeSecretStream.cs` to find the exact method/line where content bytes are read for **download** (not metadata). Record the insertion point for Task 2.
- [ ] **Step 3:** Confirm admin route prefix: existing admin app routes are `v2/admin/applications/...`? (the wiring file shows `v2/applications/{id}` for the combined handler — verify the admin-only secret routes should be `v2/admin/secret-list`, `v2/admin/secret/{name}`, `v2/admin/secret/{name}/access`). Record final routes.

---

## Backend — Feature 2 data model

> **REVISED 2026-05-30 during execution:** `ObjectMetadata.LastAccessedAt` ALREADY
> EXISTS as a non-nullable `DateTime`, set in the constructor AND updated on every
> content/metadata access (10 call sites incl. `SafeExchangeSecretStream`); its
> setter drives `ExpireIfUnusedAt`, which `PurgeManager` relies on. **Tasks 1 and 2
> are already satisfied by existing code — SKIP them. Do NOT make the field
> nullable** (that alters idle-expiry). The admin overview just surfaces the
> existing field. Sort by `LastAccessedAt` asc = stale-first (no null handling).
> **"Never accessed" is redefined as `LastAccessedAt <= CreatedAt`** (never read
> after creation). Apply this to Task 4a.

## Task 1 (SKIP — already implemented): Add `LastAccessedAt` to ObjectMetadata

**Files:**
- Modify: `SafeExchange.Core/Model/ObjectMetadata.cs`
- Modify (if explicit Cosmos config exists): `SafeExchange.Core/DatabaseContext/SafeExchangeDbContext.cs`
- Test: `SafeExchange.Tests/Tests/ObjectMetadataTests.cs` (create)

- [ ] **Step 1: Write the failing test**

```csharp
namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Model;

    [TestFixture]
    public class ObjectMetadataTests
    {
        [Test]
        public void LastAccessedAt_DefaultsToNull()
        {
            var meta = new ObjectMetadata();
            Assert.That(meta.LastAccessedAt, Is.Null);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails** — `dotnet test --filter LastAccessedAt_DefaultsToNull` → FAIL (no member `LastAccessedAt`).
- [ ] **Step 3: Implement** — add to `ObjectMetadata`:

```csharp
/// <summary>UTC time the secret's content was last read. Null = never accessed
/// since this field shipped (backward-compatible). Used by the admin overview
/// for the "stale secret" view; nulls sort as oldest.</summary>
public DateTime? LastAccessedAt { get; set; }
```

If `SafeExchangeDbContext.OnModelCreating` configures `ObjectMetadata` indexes explicitly, add `LastAccessedAt` to the indexed properties; if it uses default conventions, no change.

- [ ] **Step 4: Run test** → PASS.
- [ ] **Step 5: Commit** — `git commit -am "feat(model): add nullable ObjectMetadata.LastAccessedAt"`

## Task 2 (SKIP — already implemented): Set `LastAccessedAt` on content read

**Files:**
- Modify: `SafeExchange.Core/Functions/SafeExchangeSecretStream.cs` (download path from Task 0 Step 2)
- Test: `SafeExchange.Tests/Tests/SecretLastAccessedTests.cs` (create; model on `SecretStreamTests.cs`)

- [ ] **Step 1: Write failing integration test** — using the `SecretStreamTests` fixture setup (Cosmos emulator), create a secret with content, then invoke the content-download handler, then reload the `ObjectMetadata` and assert `LastAccessedAt` is set (within a few seconds of now). A second test: invoking the **metadata list/GET** handler does NOT set `LastAccessedAt` (stays null). Copy the fixture/DbContext bootstrap from `SecretStreamTests.cs`.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** — at the download read point, after authorization succeeds and before/after streaming content, set and persist:

```csharp
objectMetadata.LastAccessedAt = DateTimeProvider.UtcNow;
await this.dbContext.SaveChangesAsync();
```

(Use the existing loaded `ObjectMetadata` instance and `dbContext`; do not add a second write if one already happens on that path — fold into the existing SaveChanges if present.)

- [ ] **Step 4: Run** → PASS (both tests).
- [ ] **Step 5: Commit** — `git commit -am "feat(secret): stamp LastAccessedAt on content download"`

---

## Backend — Feature 2 DTOs + handler

## Task 3: Output DTOs

**Files (create, in `SafeExchange.Core/Model/Dto/Output/`):**
- `SecretAdminOverviewOutput.cs`, `SecretAdminDetailOutput.cs`, `SecretAccessItemOutput.cs`

- [ ] **Step 1: Write a serialization test** in `SafeExchange.Tests/Tests/SecretAdminDtoTests.cs` constructing each DTO and asserting key properties round-trip via `DefaultJsonSerializer`.
- [ ] **Step 2: Run** → FAIL (types missing).
- [ ] **Step 3: Implement** the DTOs:

```csharp
// SecretAdminOverviewOutput.cs
public class SecretAdminOverviewOutput
{
    public string ObjectName { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastAccessedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }      // from ExpirationMetadata (scheduled)
    public DateTime? IdleDeleteAt { get; set; }   // ExpireIfUnusedAt (default => null)
    public int AttachmentCount { get; set; }      // Content.Count(c => !c.IsMain)
    public List<string> Tags { get; set; } = new();
    public bool AuditEnabled { get; set; }
}
```
```csharp
// SecretAdminDetailOutput.cs  (overview + modified info; still NO content)
public class SecretAdminDetailOutput : SecretAdminOverviewOutput
{
    public DateTime ModifiedAt { get; set; }
    public string ModifiedBy { get; set; } = string.Empty;
    public bool KeepInStorage { get; set; }
}
```
```csharp
// SecretAccessItemOutput.cs
public class SecretAccessItemOutput
{
    public string SubjectName { get; set; } = string.Empty;
    public string SubjectType { get; set; } = string.Empty; // "User" | "Group"
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
    public bool CanGrantAccess { get; set; }
    public bool CanRevokeAccess { get; set; }
}
```

- [ ] **Step 4: Run** → PASS. **Step 5: Commit** — `git commit -am "feat(dto): admin secret overview/detail/access outputs"`

## Task 4a: `SafeExchangeAdminSecrets.RunList` (paged/sort/filter)

**Files:**
- Create: `SafeExchange.Core/Functions/Admin/SafeExchangeAdminSecrets.cs`
- Test: `SafeExchange.Tests/Tests/AdminSecretsListTests.cs`

- [ ] **Step 1: Write failing tests** (Cosmos emulator fixture). Seed ~4 secrets with varied `CreatedAt`, `LastAccessedAt` (incl. one **null**), tags, and content (one main + N attachments). Assert:
  - default list returns all, paged, with `AttachmentCount` = non-main content count;
  - `sortBy=lastAccessed&sortDir=asc` returns the **null-LastAccessedAt secret first**, then oldest→newest;
  - `neverAccessed=true` returns only the null one;
  - `accessedBefore=<date>` returns only those accessed strictly before it (nulls excluded from this filter);
  - `q=<substr>` matches name.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** `RunList` mirroring `SafeExchangeAdminApplications.RunList` (admin gate, `PaginationHelper`, `ActionResults.TryCatchAsync`). Read query params `q, sortBy, sortDir, accessedBefore, neverAccessed`. Because Cosmos EF can't always translate null-aware ordering, fetch the page deterministically:
  - Build `IQueryable<ObjectMetadata>` over `dbContext.Objects` (confirm DbSet name) with `q` filter and `neverAccessed`/`accessedBefore` filters translatable in Cosmos (`o.LastAccessedAt == null`, `o.LastAccessedAt < before`).
  - For `sortBy=lastAccessed asc`: order by a computed key so nulls are first — e.g. `OrderBy(o => o.LastAccessedAt == null ? 0 : 1).ThenBy(o => o.LastAccessedAt)`; for `desc`: `OrderByDescending(o => o.LastAccessedAt)` (nulls last). For `name`/`created` use direct `OrderBy`/`OrderByDescending`. If Cosmos rejects the conditional ordering, fall back to: query the page candidates and sort client-side after `Count`; keep pagination correct by ordering server-side on a translatable column then reordering — document whichever the emulator accepts (the tests are the gate).
  - Project to `SecretAdminOverviewOutput` (compute `AttachmentCount` client-side from loaded `Content`; map `ExpiresAt`/`IdleDeleteAt` from `ExpirationMetadata`/`ExpireIfUnusedAt`, treating default `DateTime` as null). Wrap in `PaginatedResult<...>` + `BaseResponseObject`.
- [ ] **Step 4: Run** → PASS. **Step 5: Commit** — `git commit -am "feat(admin): secrets overview list endpoint"`

## Task 4b: `RunDetail` + `RunAccess`

**Files:** Modify `SafeExchangeAdminSecrets.cs`; Test `SafeExchange.Tests/Tests/AdminSecretDetailTests.cs`

- [ ] **Step 1: Failing tests** — `RunDetail("name")` returns `SecretAdminDetailOutput` (no content/chunks) or 404; `RunAccess("name")` returns one `SecretAccessItemOutput` per `SubjectPermissions` row with correct flags + `SubjectType` string; both require admin (non-admin → filtered response).
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** `RunDetail` (load `ObjectMetadata` incl. `Content`, map detail DTO) and `RunAccess` (query `dbContext.Permissions`/`SubjectPermissions` where `SecretName == name`, map flags). Admin gate + `TryCatchAsync` as before.
- [ ] **Step 4: Run** → PASS. **Step 5: Commit** — `git commit -am "feat(admin): secret detail + access endpoints"`

## Task 5: Worker wiring `SafeAdminSecrets`

**Files:** Create `SafeExchange.Functions/AdminFunctions/SafeAdminSecrets.cs`; Test: covered by the e2e function tests if present, else manual.

- [ ] **Step 1:** Implement three `[Function]`s mirroring `SafeAdminApplications`, constructing `SafeExchangeAdminSecrets` and routing:
  - `GET {Version}/admin/secret-list` → `RunList`
  - `GET {Version}/admin/secret/{secretName}` → `RunDetail`
  - `GET {Version}/admin/secret/{secretName}/access` → `RunAccess`
  Use `request.FunctionContext.GetPrincipal()`; inject `SafeExchangeDbContext`, `GlobalFilters`, `IOptionsMonitor<Limits>`.
- [ ] **Step 2:** Build the Functions project → 0 errors. **Step 3: Commit** — `git commit -am "feat(admin): wire secret admin functions"`

## Task 6: User detail (only if Task 0 Step 1 found fields missing)

**Files:** Modify `SafeExchangeAdminUsers.cs` (+ `SafeAdminUsers` wiring if a new route); Test `AdminUserDetailTests.cs`.

- [ ] Steps: failing test for `GET {Version}/admin/users/{upn}` returning the user fields (excluding groups) or 404 → run FAIL → implement `RunDetail` mapping `User` → a `UserDetailOutput` DTO (omit `Groups`) → run PASS → commit. If fields already present in the list payload, skip and note it.

---

## Frontend — `safeexchange.blazorpwa` (branch `features/admin-user-and-secrets`)

> First: `cd safeexchange.blazorpwa && git checkout main && git checkout -b features/admin-user-and-secrets`.

## Task 7: ApiClient methods + DTOs

**Files:** Modify `SafeExchange.Client.Common/ApiClient.cs`; create DTOs in the client model folder (mirror `ApplicationAdminOverview`).

- [ ] Add `SecretAdminOverview`, `SecretAdminDetail`, `SecretAccessItem` client DTOs (match backend property names). Add methods mirroring `ListApplicationsAsync`:
  - `ListSecretsAsync(string q, int page, string sortBy, string sortDir, DateTime? accessedBefore, bool neverAccessed)` → `GET v2/admin/secret-list?...`
  - `GetSecretDetailAsync(string name)` → `GET v2/admin/secret/{name}`
  - `GetSecretAccessAsync(string name)` → `GET v2/admin/secret/{name}/access`
  - `GetUserDetailAsync(string upn)` only if Task 6 added an endpoint.
- [ ] Build the client project → 0 errors. Commit.

## Task 8: `Users.razor` → Manage button

**Files:** Modify `SafeExchange.AdminPanel/Pages/Users.razor`

- [ ] Replace the inline Enable/Disable `<button>` (lines ~26-30) with the Applications-style Manage button:
```razor
<button class="btn btn-sm btn-outline-primary" @onclick="() => this.NavManager.NavigateTo($\"users/{Uri.EscapeDataString(u.AadUpn)}\")" title="Manage user">
    <i class="bi bi-pencil-square"></i>&nbsp;Manage
</button>
```
Inject `@inject NavigationManager NavManager`; remove the now-unused `toggling`/`ToggleAsync`. Build → 0 errors. Commit.

## Task 9: `ManageUser.razor`

**Files:** Create `SafeExchange.AdminPanel/Pages/ManageUser.razor` (mirror `ManageS2SApp.razor` structure + `ConfirmDialog`).

- [ ] `@page "/users/{Upn}"`. On init, load the user detail (via `GetUserDetailAsync` or the list lookup per Task 0). Render header (display name + enabled/disabled `admin-pill`) + Enable/Disable button (calls `SetUserEnabledAsync`, confirm via `ConfirmDialog`), then a key/value grid of: UPN, contact email, display name, user Id, Entra object id, Entra tenant id, created, last modified, external notifications, consent required. **No groups.** Build → 0 errors. Commit.

## Task 10: `Secrets.razor` (list + sort/filter)

**Files:** Create `SafeExchange.AdminPanel/Pages/Secrets.razor` (mirror `Applications.razor`).

- [ ] `@page "/secrets"`. Search box; a sort control (`<select>`: Last accessed ↑, Last accessed ↓, Name, Created) bound to `sortBy`/`sortDir`; a filter row: an `accessedBefore` `<input type="date">` and a "Never accessed" checkbox. Rows = `admin-card`: name (strong) + sub (`owner · created · N attachments`), a status `admin-pill` (active / `expires <date>` / `idle-delete in Nd`), and a Metadata button → `secrets/{name}`. `admin-pager`. Wire to `ListSecretsAsync(...)`. Build → 0 errors. Commit.

## Task 11: `SecretDetails.razor`

**Files:** Create `SafeExchange.AdminPanel/Pages/SecretDetails.razor`.

- [ ] `@page "/secrets/{Name}"`. Load `GetSecretDetailAsync(Name)` + `GetSecretAccessAsync(Name)`. Render metadata key/value grid (name, created+by, modified+by, last accessed, expires, idle deletion, **attachments: count only**, tags, audit) and a "Who has access" `<table>` (Subject, Type, Read, Write, Grant, Revoke). Include a static note "Secret content is never retrievable here." **Never** render content or attachment names/sizes. Build → 0 errors. Commit.

## Task 12: Admin nav + Index card

**Files:** Modify `SafeExchange.AdminPanel/Pages/Index.razor` and the admin nav/layout.

- [ ] Add a "Secrets" `admin-card` link (icon e.g. `bi-file-earmark-lock`) to `Index.razor` mirroring the Users/Applications/Audit cards, and a nav entry if the layout has one. Build → 0 errors. Commit.

---

## Final verification (whole feature)
- [ ] Backend: start emulator, `dotnet test -c Release` → all green (incl. new tests).
- [ ] Frontend: `dotnet build SafeExchange.PWA/SafeExchange.PWA.csproj -c Release` → 0 errors; run component tests if present.
- [ ] Code review of the full diff per repo (use the code-review skill) before deploy.
- [ ] Deploy backend → staging → verify → prod; then frontend `deploy-pwa.ps1 -Environment test` → verify → `prd`.

## Notes
- Confirm exact DbSet names (`dbContext.Objects`? `dbContext.Permissions`?) by reading `SafeExchangeDbContext.cs` in Task 0/Task 4.
- All new endpoints are admin-only via `globalFilters.GetAdminFilterResultAsync` — every handler test must include a non-admin-forbidden case.
