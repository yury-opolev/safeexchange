# Admin Telemetry-ID Lookup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give admins bounded, auto-expiring reversibility of pseudonymous telemetry ids — store each *retired* id in a TTL'd Cosmos `TelemetryIdMap` container, show a user's current + recent ids on the admin user-detail page, and let an admin resolve any id (current or historical-within-window) back to its user.

**Architecture:** On weekly rotation, `TelemetryIdRotator.EnsureCurrent` now reports the retiring id + its active window; `TokenMiddlewareCore` writes one `TelemetryIdMapEntry` per retirement. A 28-day Cosmos container TTL purges entries automatically (no job). Two admin reads use it: the user-detail endpoint lists a user's retired ids (partition query by `UserId`), and a new `by-telemetry-id` endpoint resolves an id → user (current id first, then a cross-partition map lookup). The Blazor admin panel surfaces both.

**Tech Stack:** .NET 10 isolated Azure Functions, EF Core Cosmos, NUnit + Cosmos emulator (vnext-preview, `PROTOCOL=https`, port 8081), Blazor WASM admin panel (`safeexchange.blazorpwa`).

**Repos & branch:** backend `C:\Users\yurio\Documents\github\safeexchange`; admin panel `C:\Users\yurio\Documents\github\safeexchange.blazorpwa`. Branch `features/admin-telemetry-id-lookup` in **both**. Staging-only.

**Spec:** `docs/superpowers/specs/2026-05-30-admin-telemetry-id-lookup-design.md`.

---

## File Structure (decisions locked here)

Backend (`safeexchange`):
- `SafeExchange.Core/Model/User.cs` — add `TelemetryIdIssuedAt`.
- `SafeExchange.Core/Model/TelemetryIdMapEntry.cs` — **new** entity (one retired id).
- `SafeExchange.Core/Telemetry/TelemetryIdRotationResult.cs` — **new** result struct.
- `SafeExchange.Core/Telemetry/TelemetryIdRotator.cs` — `EnsureCurrent` returns the result, sets `IssuedAt`.
- `SafeExchange.Core/DatabaseContext/SafeExchangeDbContext.cs` — `DbSet` + model config for the map.
- `SafeExchange.Core/Middleware/TokenMiddlewareCore.cs` — write a map entry on rotation.
- `SafeExchange.Core/Model/Dto/Output/UserDetailOutput.cs` + new `TelemetryIdWindowOutput.cs` — detail DTO.
- `SafeExchange.Core/Functions/Admin/SafeExchangeAdminUsers.cs` — map the new fields; new `RunByTelemetryId`.
- `SafeExchange.Functions/Functions/SafeAdminUsers.cs` — wire the new `[Function]` route.
- `SafeExchange.Core/Migrations/Model/MigrationItem00012_User.cs` + `SafeExchange.Core/Migrations/IssuedAtBackfill.cs` + `SafeExchange.Core/Migrations/MigrationsHelper.cs` — backfill `IssuedAt`.
- `deployment/current/arm/services-template.arm.json` — `TelemetryIdMap` container.
- Tests: `SafeExchange.Tests/Tests/TelemetryIdRotatorTests.cs`, `UserTests.cs`, `AdminUserDetailTests.cs`, new `IssuedAtBackfillTests.cs`.

Admin panel (`safeexchange.blazorpwa`):
- `SafeExchange.Client.Common/Model/AdminModels.cs` — `UserDetail` fields + `TelemetryIdWindow`.
- `SafeExchange.Client.Common/ApiClient/ApiClient.Admin.cs` — `FindUserByTelemetryIdAsync`.
- `SafeExchange.AdminPanel/Pages/ManageUser.razor` — current id + recent-ids table.
- `SafeExchange.AdminPanel/Pages/Users.razor` — find-by-id field.

**Constants:** container name `TelemetryIdMap`; TTL `2419200` seconds (28 days); telemetry-id regex `^[0-9a-f]{32}$`.

---

### Task 1: `User.TelemetryIdIssuedAt` field

**Files:**
- Modify: `SafeExchange.Core/Model/User.cs`
- Test: `SafeExchange.Tests/Tests/UserTelemetryFieldsTests.cs`

- [ ] **Step 1: Write the failing test** — append to `UserTelemetryFieldsTests`:

```csharp
        [Test]
        public void TelemetryIdIssuedAt_DefaultsToMinValue()
        {
            var user = new User();
            Assert.That(user.TelemetryIdIssuedAt, Is.EqualTo(default(System.DateTime)));
        }
```

- [ ] **Step 2: Run it, verify it fails** — `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~UserTelemetryFieldsTests"` → FAIL (no `TelemetryIdIssuedAt`).

- [ ] **Step 3: Implement** — in `User.cs`, after `TelemetryIdExpiresAt`:

```csharp
        /// <summary>UTC instant the current TelemetryId was generated. Used as the
        /// validFrom of a retired id when it rotates into the TelemetryIdMap.</summary>
        public DateTime TelemetryIdIssuedAt { get; set; }
```

- [ ] **Step 4: Run it, verify it passes.**
- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat(model): add User.TelemetryIdIssuedAt"`.

---

### Task 2: `TelemetryIdMapEntry` entity

**Files:**
- Create: `SafeExchange.Core/Model/TelemetryIdMapEntry.cs`
- Test: `SafeExchange.Tests/Tests/TelemetryIdMapEntryTests.cs` (new)

- [ ] **Step 1: Write the failing test** (new file):

```csharp
namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Model;
    using System;

    [TestFixture]
    public class TelemetryIdMapEntryTests
    {
        [Test]
        public void Properties_RoundTrip()
        {
            var from = new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc);
            var to = new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc);
            var entry = new TelemetryIdMapEntry
            {
                id = "0123456789abcdef0123456789abcdef",
                UserId = "user-1",
                ValidFromUtc = from,
                ValidToUtc = to,
            };

            Assert.That(entry.id, Is.EqualTo("0123456789abcdef0123456789abcdef"));
            Assert.That(entry.UserId, Is.EqualTo("user-1"));
            Assert.That(entry.ValidFromUtc, Is.EqualTo(from));
            Assert.That(entry.ValidToUtc, Is.EqualTo(to));
        }
    }
}
```

- [ ] **Step 2: Run it, verify it fails** — FAIL (`TelemetryIdMapEntry` not found).

- [ ] **Step 3: Implement** (new file `SafeExchange.Core/Model/TelemetryIdMapEntry.cs`):

```csharp
/// <summary>
/// TelemetryIdMapEntry
/// </summary>

namespace SafeExchange.Core.Model
{
    using System;

    /// <summary>One retired telemetry id and the window it was active. The document id
    /// is the telemetry id itself; the partition key is the owning user's id so a
    /// future per-user erasure is a single-partition delete. Auto-purged by the
    /// TelemetryIdMap container's 28-day TTL.</summary>
    public class TelemetryIdMapEntry
    {
        public TelemetryIdMapEntry() { }

        /// <summary>The retired telemetry id (GUID "n") — also the Cosmos document id.</summary>
        public string id { get; set; } = string.Empty;

        /// <summary>Owning user's <see cref="User.Id"/>. Partition key.</summary>
        public string UserId { get; set; } = string.Empty;

        /// <summary>UTC instant the id became the user's current id.</summary>
        public DateTime ValidFromUtc { get; set; }

        /// <summary>UTC instant the id was retired (rotated out).</summary>
        public DateTime ValidToUtc { get; set; }
    }
}
```

- [ ] **Step 4: Run it, verify it passes.**
- [ ] **Step 5: Commit** — `git commit -m "feat(model): add TelemetryIdMapEntry entity"`.

---

### Task 3: `TelemetryIdRotator.EnsureCurrent` returns a rotation result

**Files:**
- Create: `SafeExchange.Core/Telemetry/TelemetryIdRotationResult.cs`
- Modify: `SafeExchange.Core/Telemetry/TelemetryIdRotator.cs`
- Test: `SafeExchange.Tests/Tests/TelemetryIdRotatorTests.cs`

**Note on existing callers:** `TokenMiddlewareCore` uses the bool in two places (Task 4 / shipped code). After this task the project will not compile until Task 4 updates them — that is expected; this task’s commit may leave `TokenMiddlewareCore` red, so **do Task 3 and Task 4 in one commit** if you prefer green-at-every-commit. The implementer should update `TokenMiddlewareCore` minimally to `.Rotated` here so the solution compiles and the rotator tests run.

- [ ] **Step 1: Write the result type** (new file `TelemetryIdRotationResult.cs`):

```csharp
/// <summary>
/// TelemetryIdRotationResult
/// </summary>

namespace SafeExchange.Core.Telemetry
{
    using System;

    /// <summary>Outcome of <see cref="TelemetryIdRotator.EnsureCurrent"/>. When a real
    /// rotation retired a previous id, <see cref="RetiredTelemetryId"/> is non-null and
    /// the validity window describes when that id was active.</summary>
    public readonly struct TelemetryIdRotationResult
    {
        public TelemetryIdRotationResult(bool rotated, string? retiredTelemetryId, DateTime retiredValidFromUtc, DateTime retiredValidToUtc)
        {
            this.Rotated = rotated;
            this.RetiredTelemetryId = retiredTelemetryId;
            this.RetiredValidFromUtc = retiredValidFromUtc;
            this.RetiredValidToUtc = retiredValidToUtc;
        }

        /// <summary>True when a new id was generated (caller must persist).</summary>
        public bool Rotated { get; }

        /// <summary>The id just retired, or null on first-ever creation / no-op.</summary>
        public string? RetiredTelemetryId { get; }

        public DateTime RetiredValidFromUtc { get; }

        public DateTime RetiredValidToUtc { get; }
    }
}
```

- [ ] **Step 2: Update the rotator tests** to the new return type and add the retire/IssuedAt assertions. Replace the three `EnsureCurrent_*` tests with:

```csharp
        [Test]
        public void EnsureCurrent_EmptyId_GeneratesSetsExpiryAndIssuedAt_NoRetiredEntry()
        {
            var rotator = new TelemetryIdRotator();
            var user = new User();
            var now = new DateTime(2026, 5, 27, 10, 0, 0, DateTimeKind.Utc);
            var result = rotator.EnsureCurrent(user, now);
            Assert.That(result.Rotated, Is.True);
            Assert.That(result.RetiredTelemetryId, Is.Null);
            Assert.That(user.TelemetryId, Is.Not.Empty);
            Assert.That(user.TelemetryIdIssuedAt, Is.EqualTo(now));
            Assert.That(user.TelemetryIdExpiresAt, Is.EqualTo(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
        }

        [Test]
        public void EnsureCurrent_NotExpired_NoChange()
        {
            var rotator = new TelemetryIdRotator();
            var user = new User { TelemetryId = "abc", TelemetryIdExpiresAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) };
            var result = rotator.EnsureCurrent(user, new DateTime(2026, 5, 31, 23, 0, 0, DateTimeKind.Utc));
            Assert.That(result.Rotated, Is.False);
            Assert.That(result.RetiredTelemetryId, Is.Null);
            Assert.That(user.TelemetryId, Is.EqualTo("abc"));
        }

        [Test]
        public void EnsureCurrent_Expired_RotatesAndReportsRetiredWindow()
        {
            var rotator = new TelemetryIdRotator();
            var issued = new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc);
            var user = new User
            {
                TelemetryId = "abc",
                TelemetryIdIssuedAt = issued,
                TelemetryIdExpiresAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            var now = new DateTime(2026, 6, 1, 0, 0, 1, DateTimeKind.Utc);
            var result = rotator.EnsureCurrent(user, now);
            Assert.That(result.Rotated, Is.True);
            Assert.That(result.RetiredTelemetryId, Is.EqualTo("abc"));
            Assert.That(result.RetiredValidFromUtc, Is.EqualTo(issued));
            Assert.That(result.RetiredValidToUtc, Is.EqualTo(now));
            Assert.That(user.TelemetryId, Is.Not.EqualTo("abc"));
            Assert.That(user.TelemetryIdIssuedAt, Is.EqualTo(now));
            Assert.That(user.TelemetryIdExpiresAt, Is.EqualTo(new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc)));
        }
```

- [ ] **Step 3: Run them, verify they fail** — `--filter "FullyQualifiedName~TelemetryIdRotatorTests"` → FAIL (return type / members).

- [ ] **Step 4: Implement** — replace `EnsureCurrent` in `TelemetryIdRotator.cs`:

```csharp
        /// <summary>Ensures the user has a current telemetry id, regenerating it when
        /// empty or expired. Returns the rotation outcome; when a non-empty id was
        /// replaced, the result carries the retired id and its active window so the
        /// caller can record it in the TelemetryIdMap.</summary>
        public TelemetryIdRotationResult EnsureCurrent(User user, DateTime nowUtc)
        {
            if (user is null)
            {
                throw new ArgumentNullException(nameof(user));
            }

            if (!string.IsNullOrEmpty(user.TelemetryId) && nowUtc < user.TelemetryIdExpiresAt)
            {
                return new TelemetryIdRotationResult(false, null, default, default);
            }

            string? retiredId = null;
            DateTime retiredFrom = default;
            DateTime retiredTo = default;
            if (!string.IsNullOrEmpty(user.TelemetryId))
            {
                retiredId = user.TelemetryId;
                retiredFrom = user.TelemetryIdIssuedAt;
                retiredTo = nowUtc;
            }

            user.TelemetryId = Guid.NewGuid().ToString("n");
            user.TelemetryIdIssuedAt = nowUtc;
            user.TelemetryIdExpiresAt = NextWeekBoundaryUtc(nowUtc);
            return new TelemetryIdRotationResult(true, retiredId, retiredFrom, retiredTo);
        }
```

- [ ] **Step 5: Update `TokenMiddlewareCore` call sites to compile** — in `RunAsync`, change `var telemetryIdChanged = this.telemetryIdRotator.EnsureCurrent(...)` to `var rotation = this.telemetryIdRotator.EnsureCurrent(user, DateTimeProvider.UtcNow);` and use `rotation.Rotated` where `telemetryIdChanged` was used. (The map write is added in Task 4.) In `CreateUserAsync`, `this.telemetryIdRotator.EnsureCurrent(user, DateTimeProvider.UtcNow);` already ignores the return — leave as is.

- [ ] **Step 6: Build + run** `--filter "FullyQualifiedName~TelemetryIdRotatorTests"` → PASS.
- [ ] **Step 7: Commit** — `git commit -m "feat(telemetry): EnsureCurrent reports retired id + window, tracks IssuedAt"`.

---

### Task 4: Wire `TelemetryIdMap` into the DbContext + write an entry on rotation

**Files:**
- Modify: `SafeExchange.Core/DatabaseContext/SafeExchangeDbContext.cs`
- Modify: `SafeExchange.Core/Middleware/TokenMiddlewareCore.cs`
- Modify: `SafeExchange.Tests/Tests/UserTests.cs` (fixture: create + clean the container; new persistence test)

- [ ] **Step 1: Add the container to the test fixture.** In `UserTests.OneTimeSetup`, after the `Users` container `DefineContainer(...).CreateIfNotExistsAsync()` block, add:

```csharp
            await cosmosClient.GetDatabase(databaseName).CreateContainerIfNotExistsAsync(
                new Microsoft.Azure.Cosmos.ContainerProperties("TelemetryIdMap", "/UserId")
                {
                    DefaultTimeToLive = 2419200,
                });
```

In `UserTests.Cleanup`, after the existing `RemoveRange` calls, add (so map rows don't leak between tests):

```csharp
            this.dbContext.Set<TelemetryIdMapEntry>().RemoveRange(this.dbContext.Set<TelemetryIdMapEntry>().ToList());
```

Add `using SafeExchange.Core.Model;` if not present.

- [ ] **Step 2: Write the failing persistence test** — append to `UserTests`:

```csharp
        [Test]
        public async Task Rotation_WritesTelemetryIdMapEntry_ForRetiredId()
        {
            // [GIVEN] A user that has already been created (id #1 active)
            var claimsPrincipal = new ClaimsPrincipal(this.firstIdentity);
            var request = TestFactory.CreateHttpRequestData("get");
            await this.tokenMiddleware.RunAsync(request, claimsPrincipal);

            var user = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals("first@test.test"));
            Assert.That(user, Is.Not.Null);
            var firstId = user!.TelemetryId;
            Assert.That(firstId, Is.Not.Empty);

            // [WHEN] Time crosses the week boundary and the user calls again -> rotation
            DateTimeProvider.SpecifiedDateTime = user.TelemetryIdExpiresAt.AddSeconds(1);
            await this.tokenMiddleware.RunAsync(request, claimsPrincipal);

            // [THEN] The retired (first) id is recorded in the map for this user
            var entry = await this.dbContext.Set<TelemetryIdMapEntry>()
                .FirstOrDefaultAsync(e => e.id == firstId);
            Assert.That(entry, Is.Not.Null);
            Assert.That(entry!.UserId, Is.EqualTo(user.Id));
            Assert.That(entry.ValidToUtc, Is.EqualTo(DateTimeProvider.SpecifiedDateTime));
        }
```

- [ ] **Step 3: Run it, verify it fails** — FAIL (no `DbSet`/no write).

- [ ] **Step 4: Wire the DbContext.** In `SafeExchangeDbContext.cs` add the property after `SecretAuditEvents`:

```csharp
        public DbSet<TelemetryIdMapEntry> TelemetryIdMap { get; set; }
```

and at the end of `OnModelCreating`:

```csharp
            modelBuilder.Entity<TelemetryIdMapEntry>()
                .ToContainer("TelemetryIdMap")
                .HasNoDiscriminator()
                .HasPartitionKey(e => e.UserId)
                .HasDefaultTimeToLive(2419200);
            modelBuilder.Entity<TelemetryIdMapEntry>().HasKey(e => e.id);
```

- [ ] **Step 5: Write the map entry on rotation.** In `TokenMiddlewareCore.RunAsync`, replace the rotation/persist block so it reads:

```csharp
            var rotation = this.telemetryIdRotator.EnsureCurrent(user, DateTimeProvider.UtcNow);
            if (rotation.Rotated)
            {
                if (rotation.RetiredTelemetryId is not null)
                {
                    await this.dbContext.Set<TelemetryIdMapEntry>().AddAsync(new TelemetryIdMapEntry
                    {
                        id = rotation.RetiredTelemetryId,
                        UserId = user.Id,
                        ValidFromUtc = rotation.RetiredValidFromUtc,
                        ValidToUtc = rotation.RetiredValidToUtc,
                    });
                }

                await this.dbContext.SaveChangesAsync();
            }

            // Stamp the telemetry id onto this request's frame so logs emitted within
            // RunAsync (e.g. the group-sync traces) carry the saex.telemetryId dimension,
            // and stash it on the invocation so TokenFilterMiddleware can re-establish it
            // within the frame that wraps next() — AsyncLocal mutations made here do not
            // reliably flow back up to the caller across the awaits in between.
            TelemetryContext.Current = user.TelemetryId;
            request.FunctionContext.Items[DefaultAuthenticationMiddleware.InvocationContextTelemetryIdKey] = user.TelemetryId;
            request.FunctionContext.Items[DefaultAuthenticationMiddleware.InvocationContextUserIdKey] = user.Id;
```

- [ ] **Step 6: Run** `--filter "FullyQualifiedName~UserTests"` → PASS (all UserTests incl. the new one).
- [ ] **Step 7: Commit** — `git commit -m "feat(telemetry): persist retired telemetry ids to TTL'd TelemetryIdMap"`.

---

### Task 5: `UserDetailOutput` telemetry fields + `TelemetryIdWindowOutput`

**Files:**
- Create: `SafeExchange.Core/Model/Dto/Output/TelemetryIdWindowOutput.cs`
- Modify: `SafeExchange.Core/Model/Dto/Output/UserDetailOutput.cs`

(No standalone test — exercised by Task 6’s endpoint tests. This task is a pure DTO shape change.)

- [ ] **Step 1: Create `TelemetryIdWindowOutput.cs`:**

```csharp
/// <summary>
/// TelemetryIdWindowOutput — a retired telemetry id and the window it was active.
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System;

    public class TelemetryIdWindowOutput
    {
        public string Id { get; set; } = string.Empty;

        public DateTime ValidFromUtc { get; set; }

        public DateTime ValidToUtc { get; set; }
    }
}
```

- [ ] **Step 2: Add fields to `UserDetailOutput.cs`** after `ConsentRequired`:

```csharp
        /// <summary>The user's current pseudonymous telemetry id (empty if never set).</summary>
        public string CurrentTelemetryId { get; set; } = string.Empty;

        /// <summary>UTC instant the current telemetry id was generated.</summary>
        public DateTime TelemetryIdActiveSinceUtc { get; set; }

        /// <summary>UTC instant the current telemetry id is due to rotate.</summary>
        public DateTime TelemetryIdRotatesAtUtc { get; set; }

        /// <summary>Recently retired telemetry ids still within the retention window,
        /// newest first.</summary>
        public List<TelemetryIdWindowOutput> RecentTelemetryIds { get; set; } = new();
```

Add `using System.Collections.Generic;` to the file.

- [ ] **Step 3: Build** the Core project → succeeds.
- [ ] **Step 4: Commit** — `git commit -m "feat(admin): add telemetry-id fields to UserDetailOutput"`.

---

### Task 6: `RunDetail` maps telemetry fields + lists the user's retired ids; shared builder

**Files:**
- Modify: `SafeExchange.Core/Functions/Admin/SafeExchangeAdminUsers.cs`
- Modify: `SafeExchange.Tests/Tests/AdminUserDetailTests.cs` (fixture container + new assertions)

- [ ] **Step 1: Add the container to the `AdminUserDetailTests` fixture.** In its `OneTimeSetup` (mirror Task 4 Step 1), after the `Users` container is ensured, add the `CreateContainerIfNotExistsAsync` for `TelemetryIdMap` (`/UserId`, `DefaultTimeToLive = 2419200`). In its teardown/cleanup, `RemoveRange` the `TelemetryIdMap` set (mirror Task 4 Step 1). Adapt to the fixture’s existing setup field names.

- [ ] **Step 2: Write failing tests** — append to `AdminUserDetailTests` (adapt handler construction to the fixture’s existing helper for building `SafeExchangeAdminUsers` + an admin principal + request):

```csharp
        [Test]
        public async Task RunDetail_ReturnsCurrentTelemetryIdAndWindow()
        {
            // [GIVEN] a user with a current telemetry id
            var user = await this.SeedUserAsync("detail-tid@test.test");
            user.TelemetryId = "0123456789abcdef0123456789abcdef";
            user.TelemetryIdIssuedAt = new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc);
            user.TelemetryIdExpiresAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            await this.dbContext.SaveChangesAsync();

            // [WHEN] admin fetches detail
            var detail = await this.GetDetailAsync(user.AadUpn);

            // [THEN] current id + window are returned
            Assert.That(detail.CurrentTelemetryId, Is.EqualTo("0123456789abcdef0123456789abcdef"));
            Assert.That(detail.TelemetryIdActiveSinceUtc, Is.EqualTo(user.TelemetryIdIssuedAt));
            Assert.That(detail.TelemetryIdRotatesAtUtc, Is.EqualTo(user.TelemetryIdExpiresAt));
        }

        [Test]
        public async Task RunDetail_ListsRetiredTelemetryIds_NewestFirst()
        {
            var user = await this.SeedUserAsync("detail-hist@test.test");
            await this.dbContext.Set<TelemetryIdMapEntry>().AddRangeAsync(
                new TelemetryIdMapEntry { id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", UserId = user.Id, ValidFromUtc = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc), ValidToUtc = new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc) },
                new TelemetryIdMapEntry { id = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", UserId = user.Id, ValidFromUtc = new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc), ValidToUtc = new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc) });
            await this.dbContext.SaveChangesAsync();

            var detail = await this.GetDetailAsync(user.AadUpn);

            Assert.That(detail.RecentTelemetryIds.Count, Is.EqualTo(2));
            Assert.That(detail.RecentTelemetryIds[0].Id, Is.EqualTo("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")); // newest ValidToUtc first
            Assert.That(detail.RecentTelemetryIds[1].Id, Is.EqualTo("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        }
```

If the fixture lacks `SeedUserAsync`/`GetDetailAsync` helpers, add small private helpers that create a `User` via the existing dbContext and invoke `handler.RunDetail(...)` then deserialize the `BaseResponseObject<UserDetailOutput>.Result` (mirror how other tests in the file read responses).

- [ ] **Step 3: Run, verify fail** — `--filter "FullyQualifiedName~AdminUserDetailTests"` → FAIL.

- [ ] **Step 4: Implement** in `SafeExchangeAdminUsers.cs`. Replace the body of `RunDetail`’s `detail` construction with a call to a shared builder, and add the builder. Inside `RunDetail`, after loading `user`:

```csharp
                var detail = await this.BuildUserDetailAsync(user);
                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<UserDetailOutput> { Status = "ok", Result = detail });
```

Add the private builder (queries the map by partition key `UserId`, newest first):

```csharp
        private async Task<UserDetailOutput> BuildUserDetailAsync(User user)
        {
            var recent = await this.dbContext.Set<TelemetryIdMapEntry>()
                .Where(e => e.UserId == user.Id)
                .OrderByDescending(e => e.ValidToUtc)
                .Select(e => new TelemetryIdWindowOutput
                {
                    Id = e.id,
                    ValidFromUtc = e.ValidFromUtc,
                    ValidToUtc = e.ValidToUtc,
                })
                .ToListAsync();

            return new UserDetailOutput
            {
                AadUpn = user.AadUpn,
                DisplayName = user.DisplayName,
                ContactEmail = user.ContactEmail,
                Enabled = user.Enabled,
                Id = user.Id,
                AadObjectId = user.AadObjectId,
                AadTenantId = user.AadTenantId,
                CreatedAt = user.CreatedAt,
                ModifiedAt = user.ModifiedAt,
                ReceiveExternalNotifications = user.ReceiveExternalNotifications,
                ConsentRequired = user.ConsentRequired,
                CurrentTelemetryId = user.TelemetryId,
                TelemetryIdActiveSinceUtc = user.TelemetryIdIssuedAt,
                TelemetryIdRotatesAtUtc = user.TelemetryIdExpiresAt,
                RecentTelemetryIds = recent,
            };
        }
```

Add `using SafeExchange.Core.Model;` (for `TelemetryIdMapEntry`) if not already imported. (The file already imports `Microsoft.EntityFrameworkCore` and `System.Linq`.)

- [ ] **Step 5: Run, verify pass.**
- [ ] **Step 6: Commit** — `git commit -m "feat(admin): user detail returns current + retired telemetry ids"`.

---

### Task 7: `GET v2/admin/users/by-telemetry-id/{telemetryId}` — resolve id → user

**Files:**
- Modify: `SafeExchange.Core/Functions/Admin/SafeExchangeAdminUsers.cs`
- Modify: `SafeExchange.Functions/Functions/SafeAdminUsers.cs`
- Modify: `SafeExchange.Tests/Tests/AdminUserDetailTests.cs`

- [ ] **Step 1: Write failing tests** — append to `AdminUserDetailTests` (adapt the invocation helper to call `handler.RunByTelemetryId(request, id, principal, log)`):

```csharp
        [Test]
        public async Task RunByTelemetryId_ResolvesCurrentId()
        {
            var user = await this.SeedUserAsync("byid-current@test.test");
            user.TelemetryId = "11111111111111111111111111111111";
            await this.dbContext.SaveChangesAsync();

            var detail = await this.GetByTelemetryIdAsync("11111111111111111111111111111111");
            Assert.That(detail.AadUpn, Is.EqualTo("byid-current@test.test"));
        }

        [Test]
        public async Task RunByTelemetryId_ResolvesRetiredIdViaMap()
        {
            var user = await this.SeedUserAsync("byid-hist@test.test");
            await this.dbContext.Set<TelemetryIdMapEntry>().AddAsync(new TelemetryIdMapEntry
            {
                id = "22222222222222222222222222222222",
                UserId = user.Id,
                ValidFromUtc = new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc),
                ValidToUtc = new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc),
            });
            await this.dbContext.SaveChangesAsync();

            var detail = await this.GetByTelemetryIdAsync("22222222222222222222222222222222");
            Assert.That(detail.AadUpn, Is.EqualTo("byid-hist@test.test"));
        }

        [Test]
        public async Task RunByTelemetryId_Unknown_Returns404()
        {
            var status = await this.GetByTelemetryIdStatusAsync("33333333333333333333333333333333");
            Assert.That(status, Is.EqualTo(System.Net.HttpStatusCode.NotFound));
        }

        [Test]
        public async Task RunByTelemetryId_Malformed_Returns400()
        {
            var status = await this.GetByTelemetryIdStatusAsync("not-a-valid-id");
            Assert.That(status, Is.EqualTo(System.Net.HttpStatusCode.BadRequest));
        }
```

Add private helpers `GetByTelemetryIdAsync` (returns `UserDetailOutput` from a 200) and `GetByTelemetryIdStatusAsync` (returns the `HttpResponseData.StatusCode`) mirroring the fixture’s response-reading helpers.

- [ ] **Step 2: Run, verify fail** — FAIL (`RunByTelemetryId` not found).

- [ ] **Step 3: Implement** in `SafeExchangeAdminUsers.cs`. Add a compiled regex field near the top of the class:

```csharp
        private static readonly System.Text.RegularExpressions.Regex TelemetryIdPattern =
            new("^[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.Compiled);
```

Add the handler:

```csharp
        public async Task<HttpResponseData> RunByTelemetryId(HttpRequestData request, string telemetryId, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetAdminFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var id = (telemetryId ?? string.Empty).Trim().ToLowerInvariant();
                if (!TelemetryIdPattern.IsMatch(id))
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Telemetry id must be 32 hex characters." });
                }

                // Current id first (lives on the user); then the retention map (cross-partition).
                var user = await this.dbContext.Users.FirstOrDefaultAsync(u => u.TelemetryId == id);
                if (user is null)
                {
                    var entry = await this.dbContext.Set<TelemetryIdMapEntry>().FirstOrDefaultAsync(e => e.id == id);
                    if (entry is not null)
                    {
                        user = await this.dbContext.Users.FirstOrDefaultAsync(u => u.Id == entry.UserId);
                    }
                }

                if (user is null)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = "No user resolves to that telemetry id (it may be older than the retention window)." });
                }

                var detail = await this.BuildUserDetailAsync(user);
                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<UserDetailOutput> { Status = "ok", Result = detail });
            }, nameof(RunByTelemetryId), log);
        }
```

- [ ] **Step 4: Wire the Function route** in `SafeAdminUsers.cs`, after `RunDetail`:

```csharp
        [Function("SafeExchange-Admin-Users-ByTelemetryId")]
        public async Task<HttpResponseData> RunByTelemetryId(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/admin/users/by-telemetry-id/{{telemetryId}}")]
            HttpRequestData request,
            string telemetryId)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunByTelemetryId(request, telemetryId, principal, this.log);
        }
```

- [ ] **Step 5: Run, verify pass** — `--filter "FullyQualifiedName~AdminUserDetailTests"` → PASS.
- [ ] **Step 6: Commit** — `git commit -m "feat(admin): resolve user by telemetry id (current + historical)"`.

---

### Task 8: Migration `00012` — backfill `TelemetryIdIssuedAt`

**Files:**
- Create: `SafeExchange.Core/Migrations/IssuedAtBackfill.cs`
- Create: `SafeExchange.Core/Migrations/Model/MigrationItem00012_User.cs`
- Modify: `SafeExchange.Core/Migrations/MigrationsHelper.cs`
- Test: `SafeExchange.Tests/Tests/IssuedAtBackfillTests.cs` (new, pure — no emulator)

- [ ] **Step 1: Write failing tests** (new file) for the pure rewriter:

```csharp
namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Migrations;
    using System;

    [TestFixture]
    public class IssuedAtBackfillTests
    {
        [Test]
        public void Backfill_SetsIssuedAt_WhenMissing()
        {
            var json = "{\"id\":\"u1\",\"TelemetryId\":\"abc\",\"TelemetryIdExpiresAt\":\"2026-06-01T00:00:00Z\"}";
            var expiresAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var result = IssuedAtBackfill.BackfillIfMissing(json, expiresAt);
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Does.Contain("\"TelemetryIdIssuedAt\""));
            Assert.That(result, Does.Contain("2026-05-25T00:00:00")); // expiresAt - 7d
        }

        [Test]
        public void Backfill_ReturnsNull_WhenAlreadySet()
        {
            var json = "{\"id\":\"u1\",\"TelemetryId\":\"abc\",\"TelemetryIdIssuedAt\":\"2026-05-20T00:00:00Z\",\"TelemetryIdExpiresAt\":\"2026-06-01T00:00:00Z\"}";
            var result = IssuedAtBackfill.BackfillIfMissing(json, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.That(result, Is.Null);
        }

        [Test]
        public void Backfill_ReturnsNull_WhenTelemetryIdEmpty()
        {
            var json = "{\"id\":\"u1\",\"TelemetryId\":\"\",\"TelemetryIdExpiresAt\":\"2026-06-01T00:00:00Z\"}";
            var result = IssuedAtBackfill.BackfillIfMissing(json, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.That(result, Is.Null);
        }
    }
}
```

- [ ] **Step 2: Run, verify fail** — FAIL (`IssuedAtBackfill` not found).

- [ ] **Step 3: Implement `IssuedAtBackfill.cs`** (mirrors `TelemetryIdBackfill`):

```csharp
/// <summary>
/// IssuedAtBackfill — pure helper for the TelemetryIdIssuedAt backfill migration (00012).
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Nodes;

    public static class IssuedAtBackfill
    {
        /// <summary>
        /// Returns the rewritten document JSON with <c>TelemetryIdIssuedAt</c> set to
        /// <c>telemetryIdExpiresAt - 7 days</c> (the start of the current calendar week),
        /// when the document has a non-empty <c>TelemetryId</c> and no usable
        /// <c>TelemetryIdIssuedAt</c>. Returns <c>null</c> when no change is needed
        /// (already set, no telemetry id) or the JSON is invalid.
        /// </summary>
        public static string? BackfillIfMissing(string documentJson, DateTime telemetryIdExpiresAt)
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(documentJson);
            }
            catch (JsonException)
            {
                return null;
            }

            if (node is null)
            {
                return null;
            }

            var telemetryId = node["TelemetryId"];
            if (telemetryId is null
                || telemetryId.GetValueKind() == JsonValueKind.Null
                || string.IsNullOrEmpty(telemetryId.GetValue<string>()))
            {
                return null;
            }

            var issued = node["TelemetryIdIssuedAt"];
            if (issued is not null
                && issued.GetValueKind() != JsonValueKind.Null
                && DateTime.TryParse(issued.GetValue<string>(), out var existing)
                && existing != default)
            {
                return null;
            }

            node["TelemetryIdIssuedAt"] = telemetryIdExpiresAt.AddDays(-7).ToString("o");
            return node.ToJsonString();
        }
    }
}
```

- [ ] **Step 4: Run, verify pass** — `--filter "FullyQualifiedName~IssuedAtBackfillTests"` → PASS.

- [ ] **Step 5: Add the migration DTO** `Model/MigrationItem00012_User.cs`:

```csharp
/// <summary>
/// MigrationItem00012_User
/// </summary>

namespace SafeExchange.Core.Migrations
{
    public class MigrationItem00012_User
    {
        public MigrationItem00012_User() { }

        public string PartitionKey { get; set; }

        public string id { get; set; }

        public string TelemetryId { get; set; }

        public string TelemetryIdIssuedAt { get; set; }
    }
}
```

- [ ] **Step 6: Wire the dispatch + pass** in `MigrationsHelper.cs`. After the `"00011"` block (≈line 106) add:

```csharp
                if ("00012".Equals(migrationId, StringComparison.InvariantCultureIgnoreCase))
                {
                    await this.RunMigration00012Async();
                    return;
                }
```

And add the method next to `RunMigration00011Async` (it reads each user’s `TelemetryIdExpiresAt` to compute the issued time; it streams + rewrites like 00011):

```csharp
        private async Task RunMigration00012Async()
        {
            using CosmosClient client = new CosmosClient(this.dbConfiguration.CosmosDbEndpoint, this.tokenCredential);
            var database = client.GetDatabase(this.dbConfiguration.DatabaseName);
            var container = database.GetContainer("Users");

            // Visit users that have a telemetry id but no issued-at yet; already-backfilled
            // users are filtered server-side so re-runs are cheap.
            var query = new QueryDefinition(
                "SELECT * FROM c WHERE IS_DEFINED(c.TelemetryId) AND NOT IS_NULL(c.TelemetryId) AND c.TelemetryId != \"\" AND (NOT IS_DEFINED(c.TelemetryIdIssuedAt) OR IS_NULL(c.TelemetryIdIssuedAt))");

            using FeedIterator<MigrationItem00012_User> feed =
                container.GetItemQueryIterator<MigrationItem00012_User>(queryDefinition: query);

            var backfilled = 0;
            while (feed.HasMoreResults)
            {
                FeedResponse<MigrationItem00012_User> response = await feed.ReadNextAsync();
                foreach (MigrationItem00012_User item in response)
                {
                    if (!string.IsNullOrEmpty(item.TelemetryIdIssuedAt))
                    {
                        continue;
                    }

                    var streamResp = await container.ReadItemStreamAsync(item.id, new PartitionKey(item.PartitionKey));
                    string original;
                    using (var reader = new StreamReader(streamResp.Content))
                    {
                        original = await reader.ReadToEndAsync();
                    }

                    var node = System.Text.Json.Nodes.JsonNode.Parse(original);
                    var expiresRaw = node?["TelemetryIdExpiresAt"]?.GetValue<string>();
                    if (!DateTime.TryParse(expiresRaw, out var expiresAt))
                    {
                        continue;
                    }

                    var rewritten = IssuedAtBackfill.BackfillIfMissing(original, expiresAt.ToUniversalTime());
                    if (rewritten is null)
                    {
                        continue;
                    }

                    using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rewritten));
                    await container.ReplaceItemStreamAsync(ms, item.id, new PartitionKey(item.PartitionKey));
                    backfilled++;
                    this.log.LogInformation($"Backfilled TelemetryIdIssuedAt on User '{item.id}'.");
                }
            }

            this.log.LogInformation($"Migration 00012 complete. Backfilled {backfilled} document(s).");
        }
```

- [ ] **Step 7: Build + run** the full migration-related tests → PASS.
- [ ] **Step 8: Commit** — `git commit -m "feat(migration): backfill TelemetryIdIssuedAt (00012)"`.

---

### Task 9: ARM — provision the `TelemetryIdMap` container

**Files:**
- Modify: `deployment/current/arm/services-template.arm.json`

- [ ] **Step 1: Add the container resource** in the `resources` array, immediately after the `Users` container object (after the block ending at the `Users` resource’s closing `}` — see the `"id": "Users"` block). Insert:

```json
        {
            "type": "Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers",
            "apiVersion": "2023-09-15",
            "name": "[concat(parameters('safeexchange_dbaccount_name'), '/', parameters('safeexchange_cosmosdb_database_name'), '/TelemetryIdMap')]",
            "dependsOn": [
                "[resourceId('Microsoft.DocumentDB/databaseAccounts/sqlDatabases', parameters('safeexchange_dbaccount_name'), parameters('safeexchange_cosmosdb_database_name'))]",
                "[resourceId('Microsoft.DocumentDB/databaseAccounts', parameters('safeexchange_dbaccount_name'))]"
            ],
            "properties": {
                "resource": {
                    "id": "TelemetryIdMap",
                    "defaultTtl": 2419200,
                    "indexingPolicy": {
                        "indexingMode": "consistent",
                        "automatic": true,
                        "includedPaths": [
                            {
                                "path": "/*"
                            }
                        ],
                        "excludedPaths": [
                            {
                                "path": "/\"_etag\"/?"
                            }
                        ]
                    },
                    "partitionKey": {
                        "paths": [
                            "/UserId"
                        ],
                        "kind": "Hash"
                    },
                    "conflictResolutionPolicy": {
                        "mode": "LastWriterWins",
                        "conflictResolutionPath": "/_ts"
                    }
                }
            }
        },
```

- [ ] **Step 2: Validate JSON** — run a JSON parse (e.g. `Get-Content services-template.arm.json -Raw | ConvertFrom-Json | Out-Null`) → no error.
- [ ] **Step 3: Commit** — `git commit -m "chore(deploy): provision TelemetryIdMap container (28-day TTL)"`.

---

### Task 10: Admin panel — client model + `FindUserByTelemetryIdAsync`

**Files (repo `safeexchange.blazorpwa`):**
- Modify: `SafeExchange.Client.Common/Model/AdminModels.cs`
- Modify: `SafeExchange.Client.Common/ApiClient/ApiClient.Admin.cs`

> First ensure the branch exists in this repo: `git checkout -b features/admin-telemetry-id-lookup` (from its default branch).

- [ ] **Step 1: Add the `TelemetryIdWindow` DTO + `UserDetail` fields** in `AdminModels.cs`. Add fields to `UserDetail` after `ConsentRequired`:

```csharp
        public string CurrentTelemetryId { get; set; } = string.Empty;
        public DateTime TelemetryIdActiveSinceUtc { get; set; }
        public DateTime TelemetryIdRotatesAtUtc { get; set; }
        public List<TelemetryIdWindow> RecentTelemetryIds { get; set; } = new();
```

And add the type (after `UserDetail`):

```csharp
    public class TelemetryIdWindow
    {
        public string Id { get; set; } = string.Empty;
        public DateTime ValidFromUtc { get; set; }
        public DateTime ValidToUtc { get; set; }
    }
```

- [ ] **Step 2: Add the client call** in `ApiClient.Admin.cs`, after `GetUserDetailAsync`:

```csharp
        public Task<BaseResponseObject<UserDetail>> FindUserByTelemetryIdAsync(string telemetryId)
            => GetAsync<UserDetail>($"{ApiVersion}/admin/users/by-telemetry-id/{Uri.EscapeDataString(telemetryId)}");
```

- [ ] **Step 3: Build** the client project → succeeds.
- [ ] **Step 4: Commit** — `git commit -m "feat(admin-panel): UserDetail telemetry fields + FindUserByTelemetryIdAsync"`.

---

### Task 11: Admin panel — show current id + recent-ids table on `ManageUser.razor`

**Files (repo `safeexchange.blazorpwa`):**
- Modify: `SafeExchange.AdminPanel/Pages/ManageUser.razor`

- [ ] **Step 1: Add the detail rows.** In the `<dl class="row mb-0">`, after the `Consent required` `<dd>`, add:

```razor
            <dt class="col-sm-3">Telemetry id (current)</dt>
            <dd class="col-sm-9">
                @if (string.IsNullOrEmpty(this.user.CurrentTelemetryId))
                {
                    <span class="text-muted">—</span>
                }
                else
                {
                    <code>@this.user.CurrentTelemetryId</code>
                    <div class="text-muted small">
                        active since @this.user.TelemetryIdActiveSinceUtc.ToString("u"), rotates after @this.user.TelemetryIdRotatesAtUtc.ToString("u")
                    </div>
                }
            </dd>

            <dt class="col-sm-3">Recent telemetry ids</dt>
            <dd class="col-sm-9">
                @if (this.user.RecentTelemetryIds.Count == 0)
                {
                    <span class="text-muted">none retained (within 28 days)</span>
                }
                else
                {
                    <table class="table table-sm mb-0">
                        <thead>
                            <tr><th>Telemetry id</th><th>Active from</th><th>Active to</th></tr>
                        </thead>
                        <tbody>
                            @foreach (var w in this.user.RecentTelemetryIds)
                            {
                                <tr>
                                    <td><code>@w.Id</code></td>
                                    <td class="text-muted">@w.ValidFromUtc.ToString("u")</td>
                                    <td class="text-muted">@w.ValidToUtc.ToString("u")</td>
                                </tr>
                            }
                        </tbody>
                    </table>
                }
            </dd>
```

- [ ] **Step 2: Build** the admin panel project → succeeds.
- [ ] **Step 3: Commit** — `git commit -m "feat(admin-panel): show current + recent telemetry ids on user detail"`.

---

### Task 12: Admin panel — "Find by telemetry id" field on `Users.razor`

**Files (repo `safeexchange.blazorpwa`):**
- Modify: `SafeExchange.AdminPanel/Pages/Users.razor`

- [ ] **Step 1: Add the dedicated field** after the existing search `input-group` (before the `@if (this.loading)` line):

```razor
<div class="input-group mb-3">
    <span class="input-group-text admin-search"><i class="bi bi-fingerprint"></i></span>
    <input class="form-control admin-search" placeholder="Find user by telemetry id (32 hex)…" @bind="this.tid" @bind:event="oninput" @onkeyup="this.OnTidKey" />
    <button class="btn btn-outline-light" @onclick="this.LookupByTelemetryIdAsync">Look up</button>
</div>
@if (!string.IsNullOrEmpty(this.tidError)) { <div class="alert alert-warning">@this.tidError</div> }
```

- [ ] **Step 2: Add the handler + state** in `@code`. Add fields:

```csharp
    private string tid = string.Empty;
    private string? tidError;
    private static readonly System.Text.RegularExpressions.Regex TidPattern =
        new("^[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.Compiled);
```

Add methods:

```csharp
    private async Task OnTidKey(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
        {
            await this.LookupByTelemetryIdAsync();
        }
    }

    private async Task LookupByTelemetryIdAsync()
    {
        this.tidError = null;
        var id = (this.tid ?? string.Empty).Trim().ToLowerInvariant();
        if (!TidPattern.IsMatch(id))
        {
            this.tidError = "Enter a 32-character hex telemetry id.";
            return;
        }

        var resp = await this.apiClient.FindUserByTelemetryIdAsync(id);
        if ("ok".Equals(resp.Status) && resp.Result is not null)
        {
            this.NavManager.NavigateTo($"users/{Uri.EscapeDataString(resp.Result.AadUpn)}");
            return;
        }

        this.tidError = "No active user has that telemetry id. Ids older than ~28 days are not retained, so they can't be resolved.";
    }
```

- [ ] **Step 3: Build** the admin panel project → succeeds.
- [ ] **Step 4: Commit** — `git commit -m "feat(admin-panel): find user by telemetry id"`.

---

## Final verification (after all tasks)

- [ ] Backend: `dotnet build` clean; `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --no-build` → all green (was 401; +new tests).
- [ ] Admin panel: `dotnet build` clean.
- [ ] Final code review of the whole branch (both repos).
- [ ] Merge `features/admin-telemetry-id-lookup` → `main` (both repos), push.
- [ ] Deploy staging: backend `func azure functionapp publish safeexchange-staging`; ARM `deployment/deploy.ps1 -Environment test` (provisions `TelemetryIdMap`); run migration `00012` via `POST v2/admops/run_dbmigration {"migrationId":"00012"}`; admin panel `deploy-pwa.ps1` staging.

## Self-review checklist (run before executing)

1. **Spec coverage:** User field (T1), map entity (T2), rotator result+IssuedAt (T3), DbContext+rotation write (T4), DTO (T5), RunDetail list (T6), by-id endpoint (T7), migration (T8), ARM (T9), client model+call (T10), detail UI (T11), search UI (T12) — all spec sections covered.
2. **Type consistency:** `TelemetryIdMapEntry.id` (lowercase, Cosmos doc id) used identically in T2/T4/T6/T7; `TelemetryIdRotationResult.Rotated/RetiredTelemetryId/RetiredValidFromUtc/RetiredValidToUtc` consistent T3↔T4; `UserDetailOutput.{CurrentTelemetryId,TelemetryIdActiveSinceUtc,TelemetryIdRotatesAtUtc,RecentTelemetryIds}` consistent T5↔T6↔T10↔T11; container name `TelemetryIdMap` and TTL `2419200` consistent T4/T9.
3. **No placeholders:** every code step has full code.
</content>
</invoke>
