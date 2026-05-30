# Rotating Telemetry ID — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Replace real user identifiers in App Insights telemetry with a per-user pseudonymous "telemetry ID" that rotates weekly (calendar-aligned, lazy), plus a feature-flagged email/UPN redaction safety net.

**Architecture:** Loosely-coupled units: a pure `TelemetryIdRotator` (no I/O), an `AsyncLocal` `TelemetryContext`, two `ITelemetryInitializer`s (stamp + redact), a `Features` flag, a wire-up in `TokenMiddlewareCore`/`SafeExchangeStartup`, a log-message sweep, and a backfill migration. Mirrors the existing `SessionCorrelationMiddleware`/`SessionCorrelationTelemetryInitializer` pattern exactly.

**Tech Stack:** .NET 10 isolated Functions, EF Core Cosmos, NUnit + Cosmos emulator (vnext-preview `PROTOCOL=https`), Microsoft.ApplicationInsights.

**Style:** Match the repo exactly — `this.`-qualified members, braces on every block, file-scoped-or-block namespaces as the neighbouring files use, XML doc comments, `DateTimeProvider.UtcNow` (never `DateTime.UtcNow`), one type per file.

**Branch:** `features/rotating-telemetry-id` (already created).
**Emulator for tests:** `docker run -d --name saex-cosmos -p 8081:8081 -p 1234:1234 -e PROTOCOL=https mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview` then wait for `https://localhost:8081/` → 200.
**Reference files to read first:** `SafeExchange.Core/Middleware/SessionCorrelationMiddleware.cs` (the initializer/AsyncLocal pattern), `SafeExchange.Core/Model/User.cs`, `SafeExchange.Core/Configuration/Features.cs`, `SafeExchange.Core/Middleware/TokenMiddlewareCore.cs`, `SafeExchange.Core/SafeExchangeStartup.cs:205-218`, `SafeExchange.Core/Migrations/Model/MigrationItem00010.cs` + `MigrationItem00007_User.cs` + `MigrationsHelper.cs`.

---

## Task 1: User telemetry fields

**Files:** Modify `SafeExchange.Core/Model/User.cs`; Test `SafeExchange.Tests/Tests/UserTelemetryFieldsTests.cs` (create)

- [ ] **Step 1 — failing test:**
```csharp
namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Model;

    [TestFixture]
    public class UserTelemetryFieldsTests
    {
        [Test]
        public void TelemetryId_DefaultsToEmpty()
        {
            var user = new User();
            Assert.That(user.TelemetryId, Is.EqualTo(string.Empty));
        }
    }
}
```
- [ ] **Step 2 — run, expect FAIL** (no member): `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj -c Release --filter "FullyQualifiedName~UserTelemetryFieldsTests"`
- [ ] **Step 3 — implement.** Add to `User` (match the file's existing property style):
```csharp
/// <summary>Current pseudonymous telemetry id (GUID "n"). Rotates weekly; stamped on
/// telemetry instead of real identifiers. Empty until first set (migration/lazy).</summary>
public string TelemetryId { get; set; } = string.Empty;

/// <summary>UTC instant at/after which TelemetryId must rotate (next week boundary).</summary>
public DateTime TelemetryIdExpiresAt { get; set; }
```
If `User` has a `ToDto()` that projects fields, do NOT add these (telemetry id must not leak to clients).
- [ ] **Step 4 — run, expect PASS.**
- [ ] **Step 5 — commit:** `git commit -am "feat(model): add User.TelemetryId + TelemetryIdExpiresAt"`

## Task 2: TelemetryIdRotator (pure logic)

**Files:** Create `SafeExchange.Core/Telemetry/TelemetryIdRotator.cs`; Test `SafeExchange.Tests/Tests/TelemetryIdRotatorTests.cs`

- [ ] **Step 1 — failing tests** (cover boundary + rotation):
```csharp
namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Telemetry;
    using System;

    [TestFixture]
    public class TelemetryIdRotatorTests
    {
        [Test]
        public void NextWeekBoundary_IsNextMonday0000Utc()
        {
            // Wed 2026-05-27 10:00 UTC -> Mon 2026-06-01 00:00 UTC
            var now = new DateTime(2026, 5, 27, 10, 0, 0, DateTimeKind.Utc);
            var b = TelemetryIdRotator.NextWeekBoundaryUtc(now);
            Assert.That(b, Is.EqualTo(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(b.Kind, Is.EqualTo(DateTimeKind.Utc));
        }

        [Test]
        public void NextWeekBoundary_OnMonday_GoesToFollowingMonday()
        {
            var monday = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
            Assert.That(TelemetryIdRotator.NextWeekBoundaryUtc(monday),
                Is.EqualTo(new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc)));
        }

        [Test]
        public void EnsureCurrent_EmptyId_GeneratesAndSetsExpiry()
        {
            var rotator = new TelemetryIdRotator();
            var user = new User();
            var now = new DateTime(2026, 5, 27, 10, 0, 0, DateTimeKind.Utc);
            var changed = rotator.EnsureCurrent(user, now);
            Assert.That(changed, Is.True);
            Assert.That(user.TelemetryId, Is.Not.Empty);
            Assert.That(user.TelemetryIdExpiresAt, Is.EqualTo(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
        }

        [Test]
        public void EnsureCurrent_NotExpired_NoChange()
        {
            var rotator = new TelemetryIdRotator();
            var user = new User { TelemetryId = "abc", TelemetryIdExpiresAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) };
            var changed = rotator.EnsureCurrent(user, new DateTime(2026, 5, 31, 23, 0, 0, DateTimeKind.Utc));
            Assert.That(changed, Is.False);
            Assert.That(user.TelemetryId, Is.EqualTo("abc"));
        }

        [Test]
        public void EnsureCurrent_Expired_RotatesToNewId()
        {
            var rotator = new TelemetryIdRotator();
            var user = new User { TelemetryId = "abc", TelemetryIdExpiresAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) };
            var changed = rotator.EnsureCurrent(user, new DateTime(2026, 6, 1, 0, 0, 1, DateTimeKind.Utc));
            Assert.That(changed, Is.True);
            Assert.That(user.TelemetryId, Is.Not.EqualTo("abc"));
            Assert.That(user.TelemetryIdExpiresAt, Is.EqualTo(new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc)));
        }
    }
}
```
- [ ] **Step 2 — run, expect FAIL** (type missing).
- [ ] **Step 3 — implement:**
```csharp
namespace SafeExchange.Core.Telemetry
{
    using SafeExchange.Core.Model;
    using System;

    /// <summary>Pure, dependency-free rotation logic for a user's telemetry id.
    /// Calendar-aligned: ids expire at the start of the next week boundary
    /// (Monday 00:00 UTC), so every user rotates at the same instant.</summary>
    public sealed class TelemetryIdRotator
    {
        private const DayOfWeek WeekBoundaryDay = DayOfWeek.Monday;

        /// <summary>Returns the start of the next <see cref="WeekBoundaryDay"/> (UTC),
        /// strictly after <paramref name="nowUtc"/>'s date.</summary>
        public static DateTime NextWeekBoundaryUtc(DateTime nowUtc)
        {
            var date = nowUtc.Date;
            int days = ((int)WeekBoundaryDay - (int)date.DayOfWeek + 7) % 7;
            if (days == 0)
            {
                days = 7;
            }

            return DateTime.SpecifyKind(date.AddDays(days), DateTimeKind.Utc);
        }

        /// <summary>Ensures the user has a current telemetry id, regenerating it when
        /// empty or expired. Returns true when the user was modified (caller must save).</summary>
        public bool EnsureCurrent(User user, DateTime nowUtc)
        {
            if (user is null)
            {
                throw new ArgumentNullException(nameof(user));
            }

            if (!string.IsNullOrEmpty(user.TelemetryId) && nowUtc < user.TelemetryIdExpiresAt)
            {
                return false;
            }

            user.TelemetryId = Guid.NewGuid().ToString("n");
            user.TelemetryIdExpiresAt = NextWeekBoundaryUtc(nowUtc);
            return true;
        }
    }
}
```
- [ ] **Step 4 — run, expect PASS (all 5).**
- [ ] **Step 5 — commit:** `git commit -am "feat(telemetry): TelemetryIdRotator (lazy, calendar-aligned weekly)"`

## Task 3: TelemetryContext + stamping initializer

**Files:** Create `SafeExchange.Core/Telemetry/TelemetryContext.cs`, `SafeExchange.Core/Telemetry/TelemetryIdTelemetryInitializer.cs`; Test `SafeExchange.Tests/Tests/TelemetryIdTelemetryInitializerTests.cs`

- [ ] **Step 1 — failing test:**
```csharp
namespace SafeExchange.Tests
{
    using Microsoft.ApplicationInsights.DataContracts;
    using NUnit.Framework;
    using SafeExchange.Core.Telemetry;

    [TestFixture]
    public class TelemetryIdTelemetryInitializerTests
    {
        [TearDown] public void Clear() => TelemetryContext.Current = null;

        [Test]
        public void Stamps_TelemetryId_WhenSet()
        {
            TelemetryContext.Current = "tid-123";
            var t = new TraceTelemetry("x");
            new TelemetryIdTelemetryInitializer().Initialize(t);
            Assert.That(t.Properties["saex.telemetryId"], Is.EqualTo("tid-123"));
        }

        [Test]
        public void NoOp_WhenEmpty()
        {
            TelemetryContext.Current = null;
            var t = new TraceTelemetry("x");
            new TelemetryIdTelemetryInitializer().Initialize(t);
            Assert.That(t.Properties.ContainsKey("saex.telemetryId"), Is.False);
        }
    }
}
```
- [ ] **Step 2 — run, expect FAIL.**
- [ ] **Step 3 — implement** `TelemetryContext.cs`:
```csharp
namespace SafeExchange.Core.Telemetry
{
    using System.Threading;

    /// <summary>Holds the current request's telemetry id for the duration of the
    /// async call chain (mirrors SessionCorrelationMiddleware.Current).</summary>
    public static class TelemetryContext
    {
        private static readonly AsyncLocal<string?> current = new();

        public static string? Current
        {
            get => current.Value;
            set => current.Value = value;
        }
    }
}
```
and `TelemetryIdTelemetryInitializer.cs` (mirror `SessionCorrelationTelemetryInitializer`):
```csharp
namespace SafeExchange.Core.Telemetry
{
    using Microsoft.ApplicationInsights.Channel;
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.ApplicationInsights.Extensibility;

    /// <summary>Stamps saex.telemetryId on every telemetry item from TelemetryContext.</summary>
    public class TelemetryIdTelemetryInitializer : ITelemetryInitializer
    {
        public const string PropertyName = "saex.telemetryId";

        public void Initialize(ITelemetry telemetry)
        {
            var telemetryId = TelemetryContext.Current;
            if (string.IsNullOrEmpty(telemetryId))
            {
                return;
            }

            if (telemetry is ISupportProperties supportsProperties
                && !supportsProperties.Properties.ContainsKey(PropertyName))
            {
                supportsProperties.Properties[PropertyName] = telemetryId;
            }
        }
    }
}
```
- [ ] **Step 4 — run, expect PASS.**
- [ ] **Step 5 — commit:** `git commit -am "feat(telemetry): TelemetryContext + telemetryId stamping initializer"`

## Task 4: Features.RedactTelemetryPii flag

**Files:** Modify `SafeExchange.Core/Configuration/Features.cs`; Test `SafeExchange.Tests/Tests/FeaturesTests.cs` (extend if exists, else create)

- [ ] **Step 1 — failing test:** assert `new Features().RedactTelemetryPii` is `false`, and that `Clone()` copies it (`new Features{RedactTelemetryPii=true}.Clone().RedactTelemetryPii == true`).
- [ ] **Step 2 — run, expect FAIL.**
- [ ] **Step 3 — implement:** add `public bool RedactTelemetryPii { get; set; } = false;` to `Features`, AND add it to `Clone()`, `Equals(object)`, and `GetHashCode()` (read the existing members and include the new field consistently — missing it there is a real bug).
- [ ] **Step 4 — run, expect PASS.**
- [ ] **Step 5 — commit:** `git commit -am "feat(config): add Features.RedactTelemetryPii flag"`

## Task 5: PII redaction initializer (flag-gated)

**Files:** Create `SafeExchange.Core/Telemetry/PiiRedactionTelemetryInitializer.cs`; Test `SafeExchange.Tests/Tests/PiiRedactionTelemetryInitializerTests.cs`

- [ ] **Step 1 — failing tests:**
```csharp
namespace SafeExchange.Tests
{
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.Extensions.Options;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Telemetry;

    [TestFixture]
    public class PiiRedactionTelemetryInitializerTests
    {
        private static PiiRedactionTelemetryInitializer Make(bool enabled)
        {
            var features = new Features { RedactTelemetryPii = enabled };
            var monitor = Mock.Of<IOptionsMonitor<Features>>(m => m.CurrentValue == features);
            return new PiiRedactionTelemetryInitializer(monitor);
        }

        [Test]
        public void Enabled_RedactsEmail()
        {
            var t = new TraceTelemetry("Principal alice@contoso.com is authenticated");
            Make(true).Initialize(t);
            Assert.That(t.Message, Does.Not.Contain("alice@contoso.com"));
            Assert.That(t.Message, Does.Contain("[redacted]"));
        }

        [Test]
        public void Enabled_LeavesCleanTextAndGuids()
        {
            var t = new TraceTelemetry("secret BLOB-20260529 id 8f3a2b1c-0000-0000-0000-000000000000 read");
            var original = t.Message;
            Make(true).Initialize(t);
            Assert.That(t.Message, Is.EqualTo(original)); // no '@', no redaction
        }

        [Test]
        public void Disabled_PassesThrough()
        {
            var t = new TraceTelemetry("Principal alice@contoso.com is authenticated");
            var original = t.Message;
            Make(false).Initialize(t);
            Assert.That(t.Message, Is.EqualTo(original));
        }
    }
}
```
- [ ] **Step 2 — run, expect FAIL.**
- [ ] **Step 3 — implement** (initializer, NOT processor — registers like the others; reads the flag live):
```csharp
namespace SafeExchange.Core.Telemetry
{
    using Microsoft.ApplicationInsights.Channel;
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.ApplicationInsights.Extensibility;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core.Configuration;
    using System;
    using System.Text.RegularExpressions;

    /// <summary>Safety net: when Features.RedactTelemetryPii is enabled, redacts
    /// email/UPN-shaped substrings from trace/exception message text. Deliberately
    /// does NOT touch GUIDs (oid/tenant/secret ids/telemetryId) or display names.
    /// Flag is read live via IOptionsMonitor so it can be toggled without redeploy.</summary>
    public class PiiRedactionTelemetryInitializer : ITelemetryInitializer
    {
        private const string Replacement = "[redacted]";

        // Linear, no nested quantifiers -> no catastrophic backtracking.
        private static readonly Regex EmailLike =
            new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);

        private readonly IOptionsMonitor<Features> features;

        public PiiRedactionTelemetryInitializer(IOptionsMonitor<Features> features)
        {
            this.features = features ?? throw new ArgumentNullException(nameof(features));
        }

        public void Initialize(ITelemetry telemetry)
        {
            if (!this.features.CurrentValue.RedactTelemetryPii)
            {
                return;
            }

            if (telemetry is TraceTelemetry trace)
            {
                trace.Message = Redact(trace.Message);
            }
            else if (telemetry is ExceptionTelemetry exception)
            {
                exception.Message = Redact(exception.Message);
            }
        }

        private static string Redact(string? message)
        {
            if (string.IsNullOrEmpty(message) || message.IndexOf('@') < 0)
            {
                return message ?? string.Empty;
            }

            return EmailLike.Replace(message, Replacement);
        }
    }
}
```
- [ ] **Step 4 — run, expect PASS.**
- [ ] **Step 5 — commit:** `git commit -am "feat(telemetry): flag-gated PII (email/UPN) redaction initializer"`

## Task 6: Wire-up (rotation in auth path + register initializers)

**Files:** Modify `SafeExchange.Core/Middleware/TokenMiddlewareCore.cs`, `SafeExchange.Core/SafeExchangeStartup.cs`

- [ ] **Step 1:** In `SafeExchangeStartup.ConfigureServices`, next to the existing `services.AddSingleton<ITelemetryInitializer, SessionCorrelationTelemetryInitializer>();` (line ~218) add:
```csharp
services.AddSingleton<ITelemetryInitializer, TelemetryIdTelemetryInitializer>();
services.AddSingleton<ITelemetryInitializer, PiiRedactionTelemetryInitializer>();
services.AddSingleton<TelemetryIdRotator>();
```
- [ ] **Step 2:** In `TokenMiddlewareCore`, where the authenticated `User` is resolved/created (the `dbContext.Users…FirstOrDefaultAsync` path, ~line 114 and the create path ~127), after the user object exists and BEFORE/at the same SaveChanges, call the rotator and stash the id:
```csharp
// (inject TelemetryIdRotator via ctor, or new it — match how the class takes deps)
var changed = this.telemetryIdRotator.EnsureCurrent(user, DateTimeProvider.UtcNow);
SafeExchange.Core.Telemetry.TelemetryContext.Current = user.TelemetryId;
if (changed)
{
    await this.dbContext.SaveChangesAsync();
}
```
Place it so a freshly-created user is saved with the id (fold into the existing create SaveChanges), and an existing user is saved only when `changed`. Set `TelemetryContext.Current` on **every** authenticated request (even when not changed) so the stamper has the value.
- [ ] **Step 3:** Build the solution `-c Release` → 0 errors.
- [ ] **Step 4 — commit:** `git commit -am "feat(telemetry): rotate + stash telemetry id per authenticated request"`

## Task 7: Source instrumentation sweep (remove identifiers from log messages)

**Files:** Modify the handlers/middleware that interpolate identifiers into log message text.

- [ ] **Step 1 — find them:** `grep -rnE "Log(Information|Debug|Warning|Error).*(\{subjectId\}|subjectId|AadUpn|AadObjectId|AadTenantId|\.Mail|ContactEmail|displayName|Principal)" --include=*.cs SafeExchange.Core` (excluding obj/bin). Also check `TokenMiddlewareCore` group-sync logs and the "Principal … authenticated" trace (find its source).
- [ ] **Step 2 — edit each:** remove the identifier from the message template; where a "who" reference is genuinely useful, replace with `TelemetryContext.Current` (the telemetry id). Examples:
  - `log.LogInformation($"… by {subjectType} {subjectId} …")` → `log.LogInformation($"… by {subjectType} (tid {TelemetryContext.Current}) …")` or drop the subject entirely if the dimension suffices.
  - `TokenMiddlewareCore`: `"Updating groups for user '{user.AadUpn}' ({user.AadTenantId}.{user.AadObjectId}), Id {user.Id}."` → log without UPN/oid/tenant (e.g. `"Updating groups for user (tid {user.TelemetryId})."`).
  - The "Principal '{email}' is authenticated …" trace → drop the email.
  Keep operational context (operation name, resource id, status). `subjectId`/UPN remains used for business logic — only logging changes.
- [ ] **Step 3:** Re-run the grep → no identifier interpolation remains in `Log*` message templates (display-name/email/upn/oid/tenant). Build `-c Release` → 0 errors.
- [ ] **Step 4 — commit:** `git commit -am "refactor(telemetry): stop emitting user identifiers in log messages"`
- [ ] **DONE_WITH_CONCERNS** if any log genuinely needs an identifier for non-telemetry reasons — report it.

## Task 8: Backfill migration

**Files:** Create `SafeExchange.Core/Migrations/Model/MigrationItem00011.cs` (+ `_User` suffix if the pattern uses one); Modify `SafeExchange.Core/Migrations/MigrationsHelper.cs`

- [ ] **Step 1:** Read `MigrationItem00010.cs`, `MigrationItem00007_User.cs`, and `MigrationsHelper.cs` to learn the exact pattern (the migration-item DTO shape, the `FeedIterator` pass, how a pass is registered/ordered, the per-pass `MigrationId`).
- [ ] **Step 2:** Add a migration that iterates the **Users** container and, for each user with an empty `TelemetryId`, sets `TelemetryId = Guid.NewGuid().ToString("n")` and `TelemetryIdExpiresAt = TelemetryIdRotator.NextWeekBoundaryUtc(DateTimeProvider.UtcNow)`, then upserts. **Idempotent** (skip users that already have a non-empty `TelemetryId`). Register it in `MigrationsHelper` exactly like `MigrationItem00010` is wired (next free MigrationId).
- [ ] **Step 3 — test** (emulator) `SafeExchange.Tests/Tests/TelemetryIdBackfillMigrationTests.cs`: seed 2 users (one with an existing TelemetryId, one without), run the migration pass, assert the empty one got a non-empty id + a future expiry and the existing one is unchanged. Mirror an existing migration test's fixture if present; else the AdminSecretsTests Cosmos fixture.
- [ ] **Step 4:** Run the new test → PASS. Build `-c Release` → 0 errors.
- [ ] **Step 5 — commit:** `git commit -am "feat(migration): backfill User telemetry id fields (idempotent)"`

## Task 9: Feature flag in deployment (staging on, prod off)

**Files:** Modify `deployment/current/arm/services-template.arm.json` (+ parameters if used) per the Key-Vault-settings invariant.

- [ ] **Step 1:** Add a Key Vault secret + Features wiring for `RedactTelemetryPii` the same way other `Features:*` flags are provisioned (find an existing flag e.g. `Features:UseAccessGiveUp` in the ARM/KV and mirror it). Value: **staging = true, prod = false** (per-env parameter or per-env parameters file `services-parameters-test.arm.json` = true, `…-prd.arm.json` = false).
- [ ] **Step 2:** Validate the ARM JSON parses. Commit: `git commit -am "chore(deploy): RedactTelemetryPii flag (staging on, prod off)"`
- [ ] **DONE_WITH_CONCERNS** if the flag plumbing for Features is non-obvious — report how other flags are set so the controller can confirm.

## Final verification & ship
- [ ] Start emulator; `dotnet test -c Release` → new tests green (flaky pre-existing `DeleteOnePinnedGroup_Sunshine` is fixed already; re-run if any unrelated ordering flake).
- [ ] Code review of the full diff (the code-review skill / a reviewer subagent).
- [ ] Merge `features/rotating-telemetry-id` → `main`, push.
- [ ] Deploy backend to **staging** (`func azure functionapp publish safeexchange-staging`); ensure the staging Key Vault has `RedactTelemetryPii=true`.
- [ ] Verify on staging: telemetry items carry `saex.telemetryId`; no UPN/email in message text; redaction active.

## Notes
- The redactor is an **ITelemetryInitializer** (not ITelemetryProcessor) so it registers via DI like the proven `SessionCorrelationTelemetryInitializer` (the isolated-worker host does not pick up ITelemetryProcessor from plain DI). It still only redacts message text.
- Never use `DateTime.UtcNow` — always `DateTimeProvider.UtcNow` (tests pin time via `DateTimeProvider.UseSpecifiedDateTime`).
