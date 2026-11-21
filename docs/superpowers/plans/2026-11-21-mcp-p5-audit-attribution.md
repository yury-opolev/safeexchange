# Phase 5: audit attribution (`Channel`, `ClientLabel`) — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Audit events produced through MCP record `Channel = "mcp"` and the MCP client's self-reported
`ClientLabel`; REST, system and all existing events keep empty values, unchanged hashes and byte-identical REST
audit responses.

**Architecture:** A new value object `AuditOrigin` (in `SafeExchange.Core\Audit`) holds the sanitised channel and
label; `AuditOrigin.FromCaller(caller)` is the single place that maps a `Caller` to an origin (MCP → set, anything
else → `AuditOrigin.None`). `IAuditWriter.AppendAsync` (and the explicit-delete `IPurger.PurgeAsync`) take an
optional `AuditOrigin`; `AuditWriter` persists it on `SecretAuditEvent` and passes it to `AuditEventHasher`, which
appends `|{channel}|{clientLabel}` to the canonical string only when one of them is non-empty. The audit output DTO
exposes both as nullable fields that are omitted from JSON when empty, and `ContentReadMerger` never merges reads
across different origins.

**Tech stack:** .NET 10, EF Core 10.0.10 (Cosmos provider in production, InMemory in tests), NUnit 4, Moq.

**Master plan:** `docs/superpowers/plans/2026-11-21-mcp-endpoint.md` (ground rules, style, commands).
**Spec:** `docs/superpowers/specs/2026-11-21-mcp-endpoint-design.md` (§Audit attribution, §Testing item 4,
§MCP endpoint → Transport and protocol for the label format).
**Previous phase:** `docs/superpowers/plans/2026-11-21-mcp-p4-services.md` (every audit call site now lives in a
service that has the `Caller`).

**Verified baseline:** this plan was applied task by task in a scratch worktree created from `wt/p1` at
**`7bbcfb2`** (phase 4 done), against a local Cosmos emulator (`azure-cosmos-emulator:vnext-EN20260907`), and the
result was then rebased onto **`527e8af`** (the phase 4 review fix to `ChunkService` / `HashingReadStream`; the
rebase was clean and every "replace" snippet below still matches that commit exactly). Results:
- every red step failed as quoted below and every green step passed with the quoted counts;
- `dotnet build SafeExchange.slnx --no-incremental` reports **0 errors and 704 warnings before and after** (no new
  warnings), on both base commits;
- the phase verification filter goes from **333 to 356** tests at `7bbcfb2` and from **335 to 358** at
  `527e8af` (23 new), all green; the 212 contract tests pass unchanged;
- the emulator-backed audit fixtures (`AuditAnchorWriteTests`, `AuditEventCosmosTests`, `SecretAccessAuditTests`)
  pass 17/17;
- the full suite passes **946/946** on `7bbcfb2` and **948/948** on `527e8af`;
- on that emulator build every Cosmos fixture's `[OneTimeTearDown]` (`EnsureDeletedAsync`) fails with
  `500 … PostgresError(SqlState(E58P01))`. That is pre-existing (it happens at `7bbcfb2` too) and is reported as
  `TearDown failed for test fixture …` plus a non-zero exit code while the summary line says
  `Passed! - Failed: 0`. Judge runs by the summary line.

---

## Ground rules for this phase

1. **Run every command from the repository root** of the worktree that holds the phase 4 commit (`7bbcfb2`, or
   `527e8af` with the phase 4 review fix — both verified; a later review fix on top is fine). Use PowerShell. This
   plan depends only on these members, which must exist with these shapes:
   `Caller(SubjectType, string SubjectId, string DisplayName, IReadOnlyList<string> GroupIds, string Channel, string? ClientLabel)`
   (a `sealed record` in `SafeExchange.Core.Callers`), `CallerChannels.Rest` / `CallerChannels.Mcp`,
   `IAuditWriter.AppendAsync(ObjectMetadata, SecretAuditEventType, SubjectType, string, object?, ILogger, CancellationToken = default)`,
   `IPurger.PurgeAsync(string, SafeExchangeDbContext, IAuditWriter, SubjectType, string, int)`,
   `ServiceTestBase` (`SafeExchange.Tests\Services\ServiceTestBase.cs`) with `Owner`, `OwnerUpn`, `ReaderUpn`,
   `CreationInput`, `CreateSecretService`, `CreateSecretAsync`, and the real `AuditWriter` over EF InMemory.
   If a phase 4 review moved an audit call site, Task 0 Step 3 finds it; the rule is simply "every
   `AppendAsync` in `SafeExchange.Core\Services` passes `AuditOrigin.FromCaller(caller)`".
2. **The phase 1 contract tests (`SafeExchange.Tests\Contract\*`) are frozen.** Do not edit them. They must pass
   unchanged after every task (REST audit responses stay byte-identical).
3. **Style** (master plan): file header `/// <summary>\n/// TypeName\n/// </summary>`, block namespace, `using`
   directives inside the namespace, `this.` prefix, 4-space indent, NUnit `Assert.That(..., Is....)`. Comments only
   where a line needs explaining. The test project has **no implicit usings** (every test file lists its usings);
   `SafeExchange.Core` has implicit usings enabled. Files use CRLF in the working tree and have no byte-order mark;
   keep it that way.
4. **One commit for the whole phase** (Task 7), message
   `feat(audit): record MCP channel and client label on audit events`, staged by exact paths (never
   `git add -A`), including this plan file.
5. **No new build warnings.** Task 0 records the count, Task 7 compares.
6. **File locks.** If a build fails with `MSB3027`/`MSB3021` ("file is locked by .NET Host"), a stale MSBuild node or
   `testhost` from an earlier run holds a `bin` DLL. Rebuild with `-nr:false` after it exits; do not kill processes
   you did not start.
7. **Cosmos fixtures** need the local emulator and the `ConnectionStrings:CosmosDb` user secret of
   `SafeExchange.Tests` (see the master plan). Only `AuditEventCosmosTests` (new) and the existing
   `AuditAnchorWriteTests` / `SecretAccessAuditTests` are emulator-backed in the phase filter; the filter's
   `FullyQualifiedName~Audit` term selects them.

## Decisions taken in this phase

### D1: `AuditOrigin` value object, one mapping from `Caller`

`SafeExchange.Core\Audit\AuditOrigin.cs`: a `sealed record` with a **private** constructor and get-only `Channel`
and `ClientLabel` (never null). Instances come only from:

- `AuditOrigin.None` — both empty;
- `AuditOrigin.Create(channel, clientLabel)` — sanitises both (D6); returns `None` when nothing is left;
- `AuditOrigin.FromCaller(caller)` — `Create(caller.Channel, caller.ClientLabel)` when
  `caller.Channel == CallerChannels.Mcp` (ordinal), otherwise `None`.

Why a value object instead of two `string` parameters: sanitisation happens by construction, so `AuditWriter`
cannot receive an unsanitised label from any caller (a `with` expression cannot set get-only properties); the REST →
empty rule lives in exactly one method; each call site passes one argument. REST callers carry
`Channel = "rest"` in their `Caller` (phase 3), but the spec says REST leaves the audit fields empty — so
`FromCaller` deliberately maps only `mcp` and ignores a REST caller's label.

### D2: `IAuditWriter.AppendAsync` gains `AuditOrigin? origin = null` before the token

```csharp
ValueTask AppendAsync(ObjectMetadata secret, SecretAuditEventType eventType, SubjectType actorType, string actorId,
    object? payload, ILogger log, AuditOrigin? origin = null, CancellationToken ct = default);
```

No call site in the repository passes `ct` (verified in Task 0 Step 3), so every existing caller compiles
unchanged and `CancellationToken` stays last (.NET convention). `null` means `AuditOrigin.None`. Callers that are
not service methods — `PurgeManager.PurgeIfNeededAsync` (system purger), `SafeExchangeAccessGiveUp` and
`SafeExchangeAccessRequest` (REST-only handlers that still write audit themselves) — keep calling without an origin
and so record nothing. The test doubles `NoOpAuditWriter` and `RecordingAuditWriter` implement the new parameter;
`RecordedAuditEntry` gains an `AuditOrigin Origin` member so later phases can assert on it.

Compatibility: source-compatible for every *caller*; an external *implementer* of `IAuditWriter` (a consumer's
own writer) must add the parameter. Phase 7's consumer migration note must say so.

### D3: hash includes the origin only when it is non-empty

`AuditEventHasher.ComputeHash` gains two optional trailing parameters `string? channel = null,
string? clientLabel = null`. When either is non-empty the canonical string becomes
`{old canonical}|{channel}|{clientLabel}`; otherwise it is exactly the old string. Every existing stored hash, and
every REST/system event written after this change, hashes as before, and a verifier recomputes any event from its
stored fields (`…, e.PrevHash, e.Channel, e.ClientLabel`) without caring which kind it is. Task 2 pins both
canonical forms with literal strings.

### D4: `SecretAuditEvent` normalises a missing property to empty

The entity gains `Channel` and `ClientLabel` (`string`, never null to readers). **EF Cosmos 10 materialises a
property that is absent from a stored document as `null`, overriding a `= string.Empty` initializer** — observed
against the emulator: with plain auto-properties `{ get; set; } = string.Empty`, an old document read back with
`Channel == null` (EF writes into the backing field, bypassing the initializer). So the properties use nullable
backing fields initialised to `""` and getters that return `field ?? string.Empty`. No model configuration, no
migration: old documents simply lack the two properties and read as empty; new documents store them (`""` for
REST/system events). Task 3 covers this twice: an EF-free unit test (`Channel = null!` reads as empty) and an
emulator test that strips the properties from a stored document, reads it back and continues the chain.

### D5: the explicit secret delete is attributed too

`SecretDeleted` for an explicit delete is appended inside `PurgeManager`, not in `SecretService`. So
`IPurger.PurgeAsync(secretId, dbContext, auditWriter, actorType, actorId, auditRetentionDays)` gains a trailing
`AuditOrigin? origin = null`, and `SecretService.DeleteAsync` passes `AuditOrigin.FromCaller(caller)`. The passive
purge (`PurgeIfNeededAsync`, actor `system:purger`) passes nothing. The two `Mock<IPurger>` in the tests never set
up this overload, so they compile unchanged. Without this, MCP `secret_delete` (phase 6) would produce the only
unattributed MCP event.

### D6: label sanitisation lives in `AuditOrigin.Sanitize`

Phase 6 decodes `Mcp-Session-Id` into `Caller.ClientLabel`; phase 5 makes the audit side safe on its own:
`AuditOrigin.Sanitize(string?)` keeps printable ASCII only (`0x20`–`0x7E`), trims, truncates to
`AuditOrigin.MaxLength` (128) and trims the end again. `Create` applies it to both channel and label. Phase 6
should reuse `AuditOrigin.Sanitize` when decoding the session id (mapping `""` to `null`), so `Caller.ClientLabel`
and the stored label always agree.

### D7: output DTO fields are nullable and omitted when empty

`SecretAuditEventOutput` gains `string? Channel` and `string? ClientLabel` as its **last** two properties.
`ContentReadMerger` sets them to `null` when the stored value is empty. REST serialises with
`DefaultIgnoreCondition = WhenWritingNull` (`SafeExchangeStartup`, `DefaultJsonSerializer`), so REST and older
events produce exactly today's JSON (the contract tests prove it) and MCP events end with
`,"channel":"mcp","clientLabel":"…"}`.

### D8: `ContentReadMerger` merge key includes the origin

Runs of `ContentRead` events merge only when actor, `contentId`, `Channel` **and** `ClientLabel` match. An MCP read
and a REST read of the same content by the same actor stay two items, as do reads through two different MCP
clients; the merged item carries its run's origin. Histories without MCP events (all origins empty) merge exactly
as before.

## File map

| File | Change |
|---|---|
| `SafeExchange.Core\Audit\AuditOrigin.cs` | **new** — D1, D6 |
| `SafeExchange.Core\Audit\AuditEventHasher.cs` | optional `channel` / `clientLabel` (D3) |
| `SafeExchange.Core\Model\SecretAuditEvent.cs` | `Channel`, `ClientLabel` with null-normalising getters (D4) |
| `SafeExchange.Core\Audit\IAuditWriter.cs` | `AuditOrigin? origin = null` (D2) |
| `SafeExchange.Core\Audit\AuditWriter.cs` | persist + hash the origin |
| `SafeExchange.Core\Purger\IPurger.cs`, `PurgeManager.cs` | origin on the explicit-delete overload (D5) |
| `SafeExchange.Core\Services\SecretService.cs`, `ContentService.cs`, `ChunkService.cs`, `AccessService.cs` | pass `AuditOrigin.FromCaller(caller)` |
| `SafeExchange.Core\Model\Dto\Output\SecretAuditEventOutput.cs` | `string? Channel`, `string? ClientLabel` (D7) |
| `SafeExchange.Core\Audit\ContentReadMerger.cs` | map the origin; origin in the merge key (D8) |
| `SafeExchange.Tests\Utilities\NoOpAuditWriter.cs`, `RecordingAuditWriter.cs` | new parameter; `RecordedAuditEntry.Origin` |
| `SafeExchange.Tests\Tests\AuditOriginTests.cs` | **new** |
| `SafeExchange.Tests\Tests\AuditEventHasherTests.cs` | 3 tests |
| `SafeExchange.Tests\Tests\AuditWriterTests.cs` | 4 tests (EF InMemory) + shared chain check |
| `SafeExchange.Tests\Tests\AuditEventCosmosTests.cs` | **new**, emulator-backed |
| `SafeExchange.Tests\Services\AuditAttributionServiceTests.cs` | **new** |
| `SafeExchange.Tests\Services\AuditQueryServiceTests.cs` | 1 test |
| `SafeExchange.Tests\Tests\ContentReadMergerTests.cs` | 5 tests |
| `docs\data-model.md`, `docs\superpowers\specs\2026-05-14-secret-audit-design.md` | document the fields and the hash rule |

Not changed: `SafeExchange.Core.Functions` (the REST adapters and the two REST-only handlers that still call
`AppendAsync` compile unchanged), `SafeExchange.Functions`, `AuditQueryService`, the DbContext model, the
contract tests.

---

### Task 0: Record the baseline

**Files:** none.

- [ ] **Step 1: Build and record the warning count**

Run: `dotnet build SafeExchange.slnx --nologo -v q --no-incremental 2>&1 | Select-Object -Last 4`
Expected: `0 Error(s)` and a warning count — **704** at `7bbcfb2` and at `527e8af`. Write the number down; Task 7
compares.

- [ ] **Step 2: Record the phase filter counts**

Run:
```powershell
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchange.Tests.Contract|FullyQualifiedName~SafeExchange.Tests.Architecture|FullyQualifiedName~SafeExchange.Tests.Services|FullyQualifiedName~Audit|FullyQualifiedName~ContentReadMerger" 2>&1 | Select-String "Passed!|Failed!"
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchange.Tests.Services|FullyQualifiedName~SafeExchange.Tests.Contract" 2>&1 | Select-String "Passed!|Failed!"
```
Expected: `Passed!  - Failed:     0, Passed:   333, …` then `… Passed:   282, …` at `7bbcfb2`
(**335** and **284** at `527e8af`). Cosmos `TearDown failed` lines may appear (see the baseline note). Write both
numbers down; Task 4 expects the second **+ 2**, Task 7 expects the first **+ 23**.

- [ ] **Step 3: List the audit call sites**

Run: `git grep -n "AppendAsync(" -- "*.cs"`
Expected (16 lines at `7bbcfb2` and `527e8af`): the declaration and implementation in `SafeExchange.Core/Audit/IAuditWriter.cs`
and `AuditWriter.cs`; `SafeExchange.Core/Purger/PurgeManager.cs` (1); `SafeExchange.Core/Services/SecretService.cs`
(2), `ContentService.cs` (1), `ChunkService.cs` (2), `AccessService.cs` (1);
`SafeExchange.Core.Functions/Functions/SafeExchangeAccessGiveUp.cs` (1) and `SafeExchangeAccessRequest.cs` (2);
the test doubles and `AuditWriterTests.cs` (2). None passes a `CancellationToken`. If a phase 4 review added a
service call site, Task 4 must give it `AuditOrigin.FromCaller(caller)` too.

---

### Task 1: `AuditOrigin`

**Files:**
- Create: `SafeExchange.Core\Audit\AuditOrigin.cs`
- Test: `SafeExchange.Tests\Tests\AuditOriginTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `SafeExchange.Tests\Tests\AuditOriginTests.cs`:

```csharp
/// <summary>
/// AuditOriginTests
/// </summary>

namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Model;
    using System;

    [TestFixture]
    public class AuditOriginTests
    {
        [Test]
        public void FromCaller_RestCaller_IsNone()
        {
            var origin = AuditOrigin.FromCaller(MakeCaller(CallerChannels.Rest, "ignored/1.0"));

            Assert.That(origin, Is.EqualTo(AuditOrigin.None));
            Assert.That(origin.Channel, Is.Empty);
            Assert.That(origin.ClientLabel, Is.Empty);
            Assert.That(origin.IsEmpty, Is.True);
        }

        [Test]
        public void FromCaller_McpCaller_CarriesChannelAndLabel()
        {
            var origin = AuditOrigin.FromCaller(MakeCaller(CallerChannels.Mcp, "copilot-cli/1.0.114"));

            Assert.That(origin.Channel, Is.EqualTo("mcp"));
            Assert.That(origin.ClientLabel, Is.EqualTo("copilot-cli/1.0.114"));
            Assert.That(origin.IsEmpty, Is.False);
        }

        [Test]
        public void FromCaller_McpCallerWithoutLabel_HasAnEmptyLabel()
        {
            var origin = AuditOrigin.FromCaller(MakeCaller(CallerChannels.Mcp, null));

            Assert.That(origin.Channel, Is.EqualTo("mcp"));
            Assert.That(origin.ClientLabel, Is.Empty);
        }

        [Test]
        public void Create_StripsNonPrintableAndNonAsciiCharacters()
        {
            var origin = AuditOrigin.Create("mcp", "  agent\u0000/1.0\r\n\u00e9\u200b-x\t ");

            Assert.That(origin.ClientLabel, Is.EqualTo("agent/1.0-x"));
        }

        [Test]
        public void Create_TruncatesTheLabelTo128Characters()
        {
            var origin = AuditOrigin.Create("mcp", new string('a', 200));

            Assert.That(origin.ClientLabel, Has.Length.EqualTo(AuditOrigin.MaxLength));
            Assert.That(AuditOrigin.MaxLength, Is.EqualTo(128));
        }

        [Test]
        public void Create_NothingLeft_IsNone()
        {
            Assert.That(AuditOrigin.Create(null, null), Is.EqualTo(AuditOrigin.None));
            Assert.That(AuditOrigin.Create(" ", "\u0001\u00e9"), Is.EqualTo(AuditOrigin.None));
        }

        private static Caller MakeCaller(string channel, string? clientLabel)
            => new(SubjectType.User, "u@x", "U", Array.Empty<string>(), channel, clientLabel);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~AuditOriginTests" 2>&1 | Select-String "error CS" | Select-Object -First 1`
Expected: build fails with `AuditOriginTests.cs(19,26): error CS0103: The name 'AuditOrigin' does not exist in the current context`.

- [ ] **Step 3: Implement `AuditOrigin`**

Create `SafeExchange.Core\Audit\AuditOrigin.cs`:

```csharp
/// <summary>
/// AuditOrigin
/// </summary>

namespace SafeExchange.Core.Audit
{
    using SafeExchange.Core.Callers;
    using System;
    using System.Text;

    /// <summary>
    /// Where an audited action came from: the channel (<c>mcp</c>) and the client's self-reported label.
    /// REST and system actions use <see cref="None"/>, so their events and hashes are unchanged.
    /// The label carries no authority; the trusted actor is the event's ActorSubjectType/ActorSubjectId.
    /// </summary>
    public sealed record AuditOrigin
    {
        public const int MaxLength = 128;

        public static AuditOrigin None { get; } = new(string.Empty, string.Empty);

        private AuditOrigin(string channel, string clientLabel)
        {
            this.Channel = channel;
            this.ClientLabel = clientLabel;
        }

        public string Channel { get; }

        public string ClientLabel { get; }

        public bool IsEmpty => this.Channel.Length == 0 && this.ClientLabel.Length == 0;

        public static AuditOrigin Create(string? channel, string? clientLabel)
        {
            var sanitizedChannel = Sanitize(channel);
            var sanitizedLabel = Sanitize(clientLabel);
            return sanitizedChannel.Length == 0 && sanitizedLabel.Length == 0
                ? None
                : new AuditOrigin(sanitizedChannel, sanitizedLabel);
        }

        /// <summary>
        /// MCP callers get their channel and label recorded; every other channel (REST) records nothing.
        /// </summary>
        public static AuditOrigin FromCaller(Caller caller)
        {
            ArgumentNullException.ThrowIfNull(caller);
            return string.Equals(caller.Channel, CallerChannels.Mcp, StringComparison.Ordinal)
                ? Create(caller.Channel, caller.ClientLabel)
                : None;
        }

        /// <summary>
        /// Keeps printable ASCII (0x20-0x7E) only, trims, and truncates to <see cref="MaxLength"/> characters.
        /// </summary>
        public static string Sanitize(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (c >= 0x20 && c <= 0x7E)
                {
                    builder.Append(c);
                }
            }

            var printable = builder.ToString().Trim();
            return printable.Length > MaxLength ? printable[..MaxLength].TrimEnd() : printable;
        }
    }
}
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~AuditOriginTests" 2>&1 | Select-Object -Last 1`
Expected: `Passed!  - Failed:     0, Passed:     6, Skipped:     0, Total:     6, …`

---

### Task 2: `AuditEventHasher` hashes the origin only when it is set

**Files:**
- Modify: `SafeExchange.Core\Audit\AuditEventHasher.cs`
- Test: `SafeExchange.Tests\Tests\AuditEventHasherTests.cs`

- [ ] **Step 1: Write the failing tests**

In `SafeExchange.Tests\Tests\AuditEventHasherTests.cs` replace

```csharp
    using System;

    [TestFixture]
    public class AuditEventHasherTests
```

with

```csharp
    using System;
    using System.Security.Cryptography;
    using System.Text;

    [TestFixture]
    public class AuditEventHasherTests
```

and replace the end of the file

```csharp
            var bytes = Convert.FromBase64String(hash);
            Assert.That(bytes.Length, Is.EqualTo(32));
        }
    }
}
```

with

```csharp
            var bytes = Convert.FromBase64String(hash);
            Assert.That(bytes.Length, Is.EqualTo(32));
        }

        [Test]
        public void ComputeHash_WithoutOrigin_HashesTheOriginalCanonicalString()
        {
            var expected = Sha256Base64("inst-1|1|SecretCreated|2026-01-02T03:04:05.0000000Z|User|u@x|U|{}|prev");

            var legacyCall = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "prev");
            var emptyOrigin = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "prev", string.Empty, string.Empty);
            var nullOrigin = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "prev", null, null);

            Assert.That(legacyCall, Is.EqualTo(expected));
            Assert.That(emptyOrigin, Is.EqualTo(expected));
            Assert.That(nullOrigin, Is.EqualTo(expected));
        }

        [Test]
        public void ComputeHash_WithOrigin_AppendsChannelAndLabel()
        {
            var expected = Sha256Base64("inst-1|1|SecretCreated|2026-01-02T03:04:05.0000000Z|User|u@x|U|{}|prev|mcp|copilot-cli/1.0.114");

            var hash = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "prev", "mcp", "copilot-cli/1.0.114");

            Assert.That(hash, Is.EqualTo(expected));
        }

        [Test]
        public void ComputeHash_ChangingChannelOrLabel_ChangesOutput()
        {
            var baseline = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "prev", "mcp", "agent/1");
            var otherLabel = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "prev", "mcp", "agent/2");
            var noLabel = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "prev", "mcp", string.Empty);
            var noOrigin = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "prev");

            Assert.That(otherLabel, Is.Not.EqualTo(baseline));
            Assert.That(noLabel, Is.Not.EqualTo(baseline));
            Assert.That(noLabel, Is.Not.EqualTo(noOrigin));
            Assert.That(baseline, Is.Not.EqualTo(noOrigin));
        }

        private static string Sha256Base64(string canonical)
            => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
```

(`FixedOccurredAt` is the fixture's existing `new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)`.)

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~AuditEventHasherTests" 2>&1 | Select-String "error CS" | Select-Object -First 1`
Expected: `AuditEventHasherTests.cs(120,48): error CS1501: No overload for method 'ComputeHash' takes 11 arguments`.

- [ ] **Step 3: Implement**

In `SafeExchange.Core\Audit\AuditEventHasher.cs` replace the whole class body

```csharp
    public static class AuditEventHasher
    {
        public static string ComputeHash(
            string auditInstanceId,
            long sequenceNumber,
            SecretAuditEventType eventType,
            DateTime occurredAtUtc,
            SubjectType actorSubjectType,
            string actorSubjectId,
            string actorDisplayName,
            string payloadJson,
            string prevHash)
        {
            var canonical = string.Join('|',
                auditInstanceId ?? string.Empty,
                sequenceNumber.ToString(CultureInfo.InvariantCulture),
                eventType.ToString(),
                occurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                actorSubjectType.ToString(),
                actorSubjectId ?? string.Empty,
                actorDisplayName ?? string.Empty,
                payloadJson ?? string.Empty,
                prevHash ?? string.Empty);
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            return Convert.ToBase64String(bytes);
        }
    }
```

with

```csharp
    public static class AuditEventHasher
    {
        /// <summary>
        /// Hashes the canonical form of an event. <paramref name="channel"/> and <paramref name="clientLabel"/>
        /// are appended as <c>|{channel}|{clientLabel}</c> only when one of them is non-empty, so events
        /// without an origin (all REST and system events, and every event written before origins existed)
        /// hash exactly as before.
        /// </summary>
        public static string ComputeHash(
            string auditInstanceId,
            long sequenceNumber,
            SecretAuditEventType eventType,
            DateTime occurredAtUtc,
            SubjectType actorSubjectType,
            string actorSubjectId,
            string actorDisplayName,
            string payloadJson,
            string prevHash,
            string? channel = null,
            string? clientLabel = null)
        {
            var canonical = string.Join('|',
                auditInstanceId ?? string.Empty,
                sequenceNumber.ToString(CultureInfo.InvariantCulture),
                eventType.ToString(),
                occurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                actorSubjectType.ToString(),
                actorSubjectId ?? string.Empty,
                actorDisplayName ?? string.Empty,
                payloadJson ?? string.Empty,
                prevHash ?? string.Empty);
            if (!string.IsNullOrEmpty(channel) || !string.IsNullOrEmpty(clientLabel))
            {
                canonical = string.Join('|', canonical, channel ?? string.Empty, clientLabel ?? string.Empty);
            }

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            return Convert.ToBase64String(bytes);
        }
    }
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~AuditEventHasherTests" 2>&1 | Select-Object -Last 1`
Expected: `Passed!  - Failed:     0, Passed:    14, Skipped:     0, Total:    14, …` (11 existing + 3 new).

---

### Task 3: persist the origin (`SecretAuditEvent`, `IAuditWriter`, `AuditWriter`, test doubles)

**Files:**
- Modify: `SafeExchange.Core\Model\SecretAuditEvent.cs`, `SafeExchange.Core\Audit\IAuditWriter.cs`,
  `SafeExchange.Core\Audit\AuditWriter.cs`, `SafeExchange.Tests\Utilities\NoOpAuditWriter.cs`,
  `SafeExchange.Tests\Utilities\RecordingAuditWriter.cs`
- Test: `SafeExchange.Tests\Tests\AuditWriterTests.cs`, new `SafeExchange.Tests\Tests\AuditEventCosmosTests.cs`

- [ ] **Step 1: Write the failing EF InMemory tests**

In `SafeExchange.Tests\Tests\AuditWriterTests.cs` replace the top of the file

```csharp
/// AuditWriterTests — pure-unit coverage of the no-op fast-path. Cosmos-backed
/// retry/hash-chain behaviour lives in the integration test suite (not in scope
/// here without a Cosmos emulator).
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging.Abstractions;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using System;
    using System.Threading.Tasks;

    [TestFixture]
    public class AuditWriterTests
    {
        private sealed class ThrowingDbContextFactory
```

with

```csharp
/// AuditWriterTests — the no-op fast-path, plus EF InMemory coverage of origin
/// attribution (channel / client label) and the hash chain. Cosmos-specific
/// behaviour lives in AuditEventCosmosTests.
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging.Abstractions;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;

    [TestFixture]
    public class AuditWriterTests
    {
        private sealed class InMemoryDbContextFactory : IDbContextFactory<SafeExchangeDbContext>
        {
            private readonly DbContextOptions<SafeExchangeDbContext> options = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseInMemoryDatabase($"{nameof(AuditWriterTests)}-{Guid.NewGuid()}")
                .Options;

            public SafeExchangeDbContext CreateDbContext() => new(this.options);
        }

        private sealed class ThrowingDbContextFactory
```

Then replace the last test

```csharp
        [Test]
        public void Constructor_NullLogger_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new AuditWriter(new ThrowingDbContextFactory(), null!));
        }
```

with

```csharp
        [Test]
        public void Constructor_NullLogger_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new AuditWriter(new ThrowingDbContextFactory(), null!));
        }

        [Test]
        public async Task AppendAsync_WithoutOrigin_PersistsEmptyOriginAndTheOriginalHash()
        {
            var factory = new InMemoryDbContextFactory();
            var secret = MakeSecret(auditEnabled: true);
            var writer = new AuditWriter(factory, NullLogger<AuditWriter>.Instance);

            await writer.AppendAsync(secret, SecretAuditEventType.SecretCreated, SubjectType.Application, "app", payload: null, NullLogger.Instance);

            var stored = (await ReadEventsAsync(factory, secret)).Single();
            Assert.That(stored.Channel, Is.Empty);
            Assert.That(stored.ClientLabel, Is.Empty);
            Assert.That(stored.Hash, Is.EqualTo(AuditEventHasher.ComputeHash(
                stored.AuditInstanceId, stored.SequenceNumber, stored.EventType, stored.OccurredAt,
                stored.ActorSubjectType, stored.ActorSubjectId, stored.ActorDisplayName, stored.Payload, stored.PrevHash)));
        }

        [Test]
        public async Task AppendAsync_WithMcpOrigin_PersistsAndHashesChannelAndLabel()
        {
            var factory = new InMemoryDbContextFactory();
            var secret = MakeSecret(auditEnabled: true);
            var writer = new AuditWriter(factory, NullLogger<AuditWriter>.Instance);

            await writer.AppendAsync(
                secret, SecretAuditEventType.SecretCreated, SubjectType.Application, "app", payload: null, NullLogger.Instance,
                AuditOrigin.Create("mcp", "copilot-cli/1.0.114"));

            var stored = (await ReadEventsAsync(factory, secret)).Single();
            Assert.That(stored.Channel, Is.EqualTo("mcp"));
            Assert.That(stored.ClientLabel, Is.EqualTo("copilot-cli/1.0.114"));
            Assert.That(stored.Hash, Is.EqualTo(AuditEventHasher.ComputeHash(
                stored.AuditInstanceId, stored.SequenceNumber, stored.EventType, stored.OccurredAt,
                stored.ActorSubjectType, stored.ActorSubjectId, stored.ActorDisplayName, stored.Payload, stored.PrevHash,
                "mcp", "copilot-cli/1.0.114")));
            Assert.That(stored.Hash, Is.Not.EqualTo(AuditEventHasher.ComputeHash(
                stored.AuditInstanceId, stored.SequenceNumber, stored.EventType, stored.OccurredAt,
                stored.ActorSubjectType, stored.ActorSubjectId, stored.ActorDisplayName, stored.Payload, stored.PrevHash)));
        }

        [Test]
        public async Task AppendAsync_MixedOrigins_ChainVerifies()
        {
            var factory = new InMemoryDbContextFactory();
            var secret = MakeSecret(auditEnabled: true);
            var writer = new AuditWriter(factory, NullLogger<AuditWriter>.Instance);
            var read = new { contentId = "c", chunkId = "k" };

            await writer.AppendAsync(secret, SecretAuditEventType.SecretCreated, SubjectType.Application, "app", payload: null, NullLogger.Instance);
            await writer.AppendAsync(secret, SecretAuditEventType.ContentRead, SubjectType.Application, "app", read, NullLogger.Instance, AuditOrigin.Create("mcp", "agent/1"));
            await writer.AppendAsync(secret, SecretAuditEventType.ContentRead, SubjectType.Application, "app", read, NullLogger.Instance, AuditOrigin.None);

            var events = await ReadEventsAsync(factory, secret);
            Assert.That(events.Select(e => e.Channel), Is.EqualTo(new[] { string.Empty, "mcp", string.Empty }));
            AssertChainVerifies(events);
        }

        [Test]
        public void SecretAuditEvent_MissingOriginFields_ReadAsEmpty()
        {
            // EF Cosmos materialises a property that is absent from an old document as null.
            var legacy = new SecretAuditEvent { Channel = null!, ClientLabel = null! };

            Assert.That(legacy.Channel, Is.Empty);
            Assert.That(legacy.ClientLabel, Is.Empty);
            Assert.That(new SecretAuditEvent().Channel, Is.Empty);
        }

        internal static void AssertChainVerifies(IReadOnlyList<SecretAuditEvent> events)
        {
            var prevHash = string.Empty;
            foreach (var e in events.OrderBy(e => e.SequenceNumber))
            {
                Assert.That(e.PrevHash, Is.EqualTo(prevHash), $"PrevHash of event {e.SequenceNumber}");
                var recomputed = AuditEventHasher.ComputeHash(
                    e.AuditInstanceId, e.SequenceNumber, e.EventType, e.OccurredAt,
                    e.ActorSubjectType, e.ActorSubjectId, e.ActorDisplayName, e.Payload, e.PrevHash,
                    e.Channel, e.ClientLabel);
                Assert.That(e.Hash, Is.EqualTo(recomputed), $"Hash of event {e.SequenceNumber}");
                prevHash = e.Hash;
            }
        }

        private static async Task<List<SecretAuditEvent>> ReadEventsAsync(IDbContextFactory<SafeExchangeDbContext> factory, ObjectMetadata secret)
        {
            await using var db = await factory.CreateDbContextAsync();
            return await db.SecretAuditEvents.AsNoTracking()
                .Where(e => e.AuditInstanceId == secret.AuditInstanceId)
                .OrderBy(e => e.SequenceNumber)
                .ToListAsync();
        }
```

(`MakeSecret(bool auditEnabled)` is the fixture's existing helper; an audit-enabled secret gets a random
`AuditInstanceId`. `AssertChainVerifies` is `internal` because `AuditEventCosmosTests` reuses it — it is the
offline verifier of the audit spec, applied to stored fields.)

- [ ] **Step 2: Write the emulator test for documents without the new properties**

Create `SafeExchange.Tests\Tests\AuditEventCosmosTests.cs`:

```csharp
/// <summary>
/// AuditEventCosmosTests — emulator-backed checks that origin attribution
/// (Channel / ClientLabel) round-trips through Cosmos, and that documents
/// written before those properties existed still read and verify.
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.Azure.Cosmos;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging.Abstractions;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json.Nodes;
    using System.Threading.Tasks;

    [TestFixture]
    public class AuditEventCosmosTests
    {
        private const string DatabaseName = nameof(AuditEventCosmosTests) + "Database";

        private DbContextOptions<SafeExchangeDbContext> dbContextOptions = null!;

        private sealed class SimpleDbContextFactory : IDbContextFactory<SafeExchangeDbContext>
        {
            private readonly DbContextOptions<SafeExchangeDbContext> options;

            public SimpleDbContextFactory(DbContextOptions<SafeExchangeDbContext> options)
                => this.options = options;

            public SafeExchangeDbContext CreateDbContext()
                => new SafeExchangeDbContext(this.options);
        }

        [OneTimeSetUp]
        public async Task OneTimeSetup()
        {
            var secretConfiguration = new ConfigurationBuilder()
                .AddUserSecrets<AuditEventCosmosTests>()
                .Build();

            this.dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(
                    secretConfiguration.GetConnectionString("CosmosDb")!,
                    databaseName: DatabaseName,
                    CosmosTestOptions.UseGateway)
                .Options;

            await using var db = new SafeExchangeDbContext(this.dbContextOptions);
            await db.Database.EnsureCreatedAsync();
        }

        [OneTimeTearDown]
        public async Task OneTimeTeardown()
        {
            await using var db = new SafeExchangeDbContext(this.dbContextOptions);
            await db.Database.EnsureDeletedAsync();
        }

        [Test]
        public async Task AppendAsync_McpOrigin_RoundTripsAndVerifies()
        {
            var secret = MakeAuditEnabledSecret("cosmos-mcp-secret");
            var writer = new AuditWriter(new SimpleDbContextFactory(this.dbContextOptions), NullLogger<AuditWriter>.Instance);

            await writer.AppendAsync(secret, SecretAuditEventType.SecretCreated, SubjectType.Application, "app", payload: null, NullLogger.Instance);
            await writer.AppendAsync(secret, SecretAuditEventType.SecretMetadataUpdated, SubjectType.Application, "app", payload: null, NullLogger.Instance, AuditOrigin.Create("mcp", "copilot-cli/1.0.114"));

            var events = await this.ReadEventsAsync(secret);
            Assert.That(events.Select(e => (e.Channel, e.ClientLabel)), Is.EqualTo(new[] { (string.Empty, string.Empty), ("mcp", "copilot-cli/1.0.114") }));
            AuditWriterTests.AssertChainVerifies(events);
        }

        [Test]
        public async Task DocumentWithoutOriginProperties_ReadsAsEmpty_AndTheChainContinues()
        {
            var secret = MakeAuditEnabledSecret("cosmos-legacy-secret");
            var writer = new AuditWriter(new SimpleDbContextFactory(this.dbContextOptions), NullLogger<AuditWriter>.Instance);
            await writer.AppendAsync(secret, SecretAuditEventType.SecretCreated, SubjectType.Application, "app", payload: null, NullLogger.Instance);
            await this.RemoveOriginPropertiesAsync(SecretAuditEvent.MakeId(secret.AuditInstanceId!, 1), secret.AuditInstanceId!);

            await writer.AppendAsync(secret, SecretAuditEventType.SecretMetadataUpdated, SubjectType.Application, "app", payload: null, NullLogger.Instance, AuditOrigin.Create("mcp", "agent/1"));

            var events = await this.ReadEventsAsync(secret);
            Assert.That(events, Has.Count.EqualTo(2));
            Assert.That(events[0].Channel, Is.Empty);
            Assert.That(events[0].ClientLabel, Is.Empty);
            Assert.That(events[1].Channel, Is.EqualTo("mcp"));
            AuditWriterTests.AssertChainVerifies(events);
        }

        private static ObjectMetadata MakeAuditEnabledSecret(string objectName)
        {
            var input = new MetadataCreationInput
            {
                ExpirationSettings = new ExpirationSettingsInput
                {
                    ScheduleExpiration = false,
                    ExpireAt = DateTime.UtcNow.AddYears(1),
                    ExpireOnIdleTime = false,
                    IdleTimeToExpire = TimeSpan.Zero,
                },
                AuditEnabled = true,
            };
            return new ObjectMetadata(objectName, input, "creator@test.test");
        }

        private async Task<List<SecretAuditEvent>> ReadEventsAsync(ObjectMetadata secret)
        {
            await using var db = new SafeExchangeDbContext(this.dbContextOptions);
            return await db.SecretAuditEvents.AsNoTracking()
                .Where(e => e.AuditInstanceId == secret.AuditInstanceId)
                .OrderBy(e => e.SequenceNumber)
                .ToListAsync();
        }

        // Rewrites a stored document without Channel/ClientLabel, i.e. as it was written before this change.
        private async Task RemoveOriginPropertiesAsync(string id, string auditInstanceId)
        {
            await using var db = new SafeExchangeDbContext(this.dbContextOptions);
            var container = db.Database.GetCosmosClient().GetContainer(DatabaseName, "SecretAuditEvents");
            var partitionKey = new PartitionKey(auditInstanceId);

            using var read = await container.ReadItemStreamAsync(id, partitionKey);
            read.EnsureSuccessStatusCode();
            using var reader = new StreamReader(read.Content);
            var document = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
            Assert.That(document.ContainsKey("Channel"), Is.True, "Arrange: new documents store Channel.");
            document.Remove("Channel");
            document.Remove("ClientLabel");

            using var body = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString()));
            using var replaced = await container.ReplaceItemStreamAsync(body, id, partitionKey);
            replaced.EnsureSuccessStatusCode();
        }
    }
}
```

The stream API is used on purpose: it edits the stored JSON without going through EF or a serializer, so the
document ends up exactly like one written before this phase (EF stores the entity's property names as-is, e.g.
`"Channel":"","ClientLabel":""`).

- [ ] **Step 3: Run the tests to see them fail**

Run:
```powershell
dotnet build SafeExchange.Tests\SafeExchange.Tests.csproj --nologo -v q 2>&1 | Out-String -Width 500 | Select-String -Pattern "error CS[^\[]*" -AllMatches | ForEach-Object { $_.Matches.Value } | Sort-Object -Unique
```
Expected (build fails):
```
error CS0117: 'SecretAuditEvent' does not contain a definition for 'Channel'
error CS0117: 'SecretAuditEvent' does not contain a definition for 'ClientLabel'
error CS1061: 'SecretAuditEvent' does not contain a definition for 'Channel' and no accessible extension method 'Channel' …
error CS1061: 'SecretAuditEvent' does not contain a definition for 'ClientLabel' and no accessible extension method 'ClientLabel' …
error CS1503: Argument 7: cannot convert from 'SafeExchange.Core.Audit.AuditOrigin' to 'System.Threading.CancellationToken'
```

- [ ] **Step 4: Add the fields to `SecretAuditEvent`**

In `SafeExchange.Core\Model\SecretAuditEvent.cs` replace

```csharp
        public string Hash { get; set; } = string.Empty;
```

with

```csharp
        public string Hash { get; set; } = string.Empty;

        // Documents written before origin attribution have no Channel/ClientLabel
        // properties. EF Cosmos materialises a missing property as null into the
        // backing field, so the getters normalise null to empty.
        private string? channel = string.Empty;

        private string? clientLabel = string.Empty;

        /// <summary>
        /// "mcp" for events produced through the MCP endpoint; empty for REST, system and older events.
        /// </summary>
        public string Channel
        {
            get => this.channel ?? string.Empty;
            set => this.channel = value;
        }

        /// <summary>
        /// The MCP client's self-reported label (sanitised, at most 128 characters); carries no authority.
        /// </summary>
        public string ClientLabel
        {
            get => this.clientLabel ?? string.Empty;
            set => this.clientLabel = value;
        }
```

Do **not** simplify these to `public string Channel { get; set; } = string.Empty;`. That was tried while
verifying this plan: `SecretAuditEvent_MissingOriginFields_ReadAsEmpty` and
`DocumentWithoutOriginProperties_ReadsAsEmpty_AndTheChainContinues` then fail with `Expected: <empty>  But was:
null` (D4). The fields are initialised to `""` so EF InMemory's required-property check never sees `null` for an
entity built without them.

- [ ] **Step 5: Add the parameter to `IAuditWriter`**

In `SafeExchange.Core\Audit\IAuditWriter.cs` replace

```csharp
        /// <summary>
        /// Append an event. The actor's display name is resolved from server-stored
        /// records (User.DisplayName / Application.DisplayName) rather than trusted
        /// directly from a token claim — see OWASP review finding F1.
        /// </summary>
        ValueTask AppendAsync(
            ObjectMetadata secret,
            SecretAuditEventType eventType,
            SubjectType actorType,
            string actorId,
            object? payload,
            ILogger log,
            CancellationToken ct = default);
```

with

```csharp
        /// <summary>
        /// Append an event. The actor's display name is resolved from server-stored
        /// records (User.DisplayName / Application.DisplayName) rather than trusted
        /// directly from a token claim — see OWASP review finding F1.
        /// <paramref name="origin"/> (default <see cref="AuditOrigin.None"/>) records the
        /// channel and client label; it is persisted and hashed only when non-empty.
        /// </summary>
        ValueTask AppendAsync(
            ObjectMetadata secret,
            SecretAuditEventType eventType,
            SubjectType actorType,
            string actorId,
            object? payload,
            ILogger log,
            AuditOrigin? origin = null,
            CancellationToken ct = default);
```

- [ ] **Step 6: Persist and hash the origin in `AuditWriter`**

In `SafeExchange.Core\Audit\AuditWriter.cs` replace

```csharp
            object? payload,
            ILogger log,
            CancellationToken ct = default)
        {
            if (secret is null || !secret.AuditEnabled || string.IsNullOrEmpty(secret.AuditInstanceId))
            {
                return;
            }

            actorId ??= string.Empty;
```

with

```csharp
            object? payload,
            ILogger log,
            AuditOrigin? origin = null,
            CancellationToken ct = default)
        {
            if (secret is null || !secret.AuditEnabled || string.IsNullOrEmpty(secret.AuditInstanceId))
            {
                return;
            }

            origin ??= AuditOrigin.None;
            actorId ??= string.Empty;
```

and replace

```csharp
                    var hash = AuditEventHasher.ComputeHash(
                        secret.AuditInstanceId, sequence, eventType, occurredAt,
                        actorType, actorId, actorDisplayName,
                        payloadJson, prevHash);
```

with

```csharp
                    var hash = AuditEventHasher.ComputeHash(
                        secret.AuditInstanceId, sequence, eventType, occurredAt,
                        actorType, actorId, actorDisplayName,
                        payloadJson, prevHash,
                        origin.Channel, origin.ClientLabel);
```

and in the `new SecretAuditEvent { … }` initializer that follows replace

```csharp
                        PrevHash = prevHash,
                        Hash = hash,
                    };
```

with

```csharp
                        PrevHash = prevHash,
                        Hash = hash,
                        Channel = origin.Channel,
                        ClientLabel = origin.ClientLabel,
                    };
```

- [ ] **Step 7: Update the test doubles**

In `SafeExchange.Tests\Utilities\NoOpAuditWriter.cs` replace

```csharp
            object? payload,
            ILogger log,
            CancellationToken ct = default) => ValueTask.CompletedTask;
```

with

```csharp
            object? payload,
            ILogger log,
            AuditOrigin? origin = null,
            CancellationToken ct = default) => ValueTask.CompletedTask;
```

In `SafeExchange.Tests\Utilities\RecordingAuditWriter.cs` replace

```csharp
            object? payload,
            ILogger log,
            CancellationToken ct = default)
        {
            this.Entries.Add(new RecordedAuditEntry(secret.ObjectName, eventType, actorType, actorId, payload));
            return ValueTask.CompletedTask;
        }
```

with

```csharp
            object? payload,
            ILogger log,
            AuditOrigin? origin = null,
            CancellationToken ct = default)
        {
            this.Entries.Add(new RecordedAuditEntry(secret.ObjectName, eventType, actorType, actorId, payload, origin ?? AuditOrigin.None));
            return ValueTask.CompletedTask;
        }
```

and replace

```csharp
        object? Payload);
```

with

```csharp
        object? Payload,
        AuditOrigin Origin);
```

(Both files already have `using SafeExchange.Core.Audit;`. Nobody constructs or deconstructs
`RecordedAuditEntry` outside `RecordingAuditWriter`; `SecretAccessAuditTests` only reads its properties.)

- [ ] **Step 8: Run the InMemory tests to see them pass**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~AuditWriterTests" 2>&1 | Select-Object -Last 1`
Expected: `Passed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10, …` (6 existing + 4 new).

- [ ] **Step 9: Run the emulator test**

Run:
```powershell
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~AuditEventCosmosTests|FullyQualifiedName~AuditAnchorWriteTests" 2>&1 | Select-String "Passed!|Failed!"
```
Expected: `Passed!  - Failed:     0, Passed:     5, …` (2 new + 3 anchor tests). `TearDown failed for test fixture …`
lines from the emulator's database delete may appear (see the baseline note). Without an emulator these fixtures
fail at `[OneTimeSetUp]` like every other Cosmos fixture; run them where one is available (CI) before merging.

- [ ] **Step 10: Check the solution still builds**

Run: `dotnet build SafeExchange.slnx --nologo -v q 2>&1 | Select-Object -Last 3`
Expected: `0 Error(s)`. (`SafeExchange.Core.Functions` and `PurgeManager` call `AppendAsync` without an origin and
compile unchanged.)

---

### Task 4: services pass the caller's origin (incl. the explicit delete)

**Files:**
- Modify: `SafeExchange.Core\Purger\IPurger.cs`, `SafeExchange.Core\Purger\PurgeManager.cs`,
  `SafeExchange.Core\Services\SecretService.cs`, `SafeExchange.Core\Services\ContentService.cs`,
  `SafeExchange.Core\Services\ChunkService.cs`, `SafeExchange.Core\Services\AccessService.cs`
- Test: new `SafeExchange.Tests\Services\AuditAttributionServiceTests.cs`

- [ ] **Step 1: Write the failing service test**

Create `SafeExchange.Tests\Services\AuditAttributionServiceTests.cs`:

```csharp
/// <summary>
/// AuditAttributionServiceTests
/// </summary>

namespace SafeExchange.Tests.Services
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Groups;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Services;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Every service call site that writes an audit event records the caller's origin: MCP callers get
    /// channel "mcp" and their client label, REST callers leave both empty.
    /// </summary>
    [TestFixture]
    public class AuditAttributionServiceTests : ServiceTestBase
    {
        private const string Label = "copilot-cli/1.0.114";

        private static readonly byte[] Hello = Encoding.UTF8.GetBytes("hello");

        private static readonly SecretAuditEventType[] AllServiceEvents =
        {
            SecretAuditEventType.SecretCreated,
            SecretAuditEventType.SecretMetadataUpdated,
            SecretAuditEventType.PermissionGranted,
            SecretAuditEventType.ContentWritten,
            SecretAuditEventType.ContentCommitted,
            SecretAuditEventType.ContentRead,
            SecretAuditEventType.PermissionRevoked,
            SecretAuditEventType.SecretDeleted,
        };

        [Test]
        public async Task McpCaller_EveryEventCarriesChannelAndLabel()
        {
            var mcpOwner = new Caller(SubjectType.User, OwnerUpn, OwnerUpn, Array.Empty<string>(), CallerChannels.Mcp, Label);

            var events = await this.RunAuditedLifecycleAsync(mcpOwner);

            Assert.That(events.Select(e => e.EventType), Is.EqualTo(AllServiceEvents));
            Assert.That(events.Select(e => e.Channel), Is.All.EqualTo("mcp"));
            Assert.That(events.Select(e => e.ClientLabel), Is.All.EqualTo(Label));
        }

        [Test]
        public async Task RestCaller_EveryEventHasAnEmptyOrigin()
        {
            var restOwner = new Caller(SubjectType.User, OwnerUpn, OwnerUpn, Array.Empty<string>(), CallerChannels.Rest, "ignored/1.0");

            var events = await this.RunAuditedLifecycleAsync(restOwner);

            Assert.That(events.Select(e => e.EventType), Is.EqualTo(AllServiceEvents));
            Assert.That(events.Select(e => e.Channel), Is.All.Empty);
            Assert.That(events.Select(e => e.ClientLabel), Is.All.Empty);
        }

        // One audited secret through every service method that appends an event, all as the given caller.
        private async Task<List<SecretAuditEvent>> RunAuditedLifecycleAsync(Caller caller)
        {
            var secrets = this.CreateSecretService();
            var contents = new ContentService(this.Configuration, this.DbContext, this.Purger, this.PermissionsManager, this.AuditWriter, NullLogger<ContentService>.Instance);
            var chunks = new ChunkService(this.Configuration, this.DbContext, this.Purger, this.BlobHelper, this.PermissionsManager, this.AuditWriter, NullLogger<ChunkService>.Instance);
            var access = this.CreateAccessService();

            AssertOk((await secrets.CreateAsync(caller, "alpha", CreationInput(auditEnabled: true))).Outcome);
            var auditInstanceId = (await this.DbContext.Objects.AsNoTracking().SingleAsync(o => o.ObjectName == "alpha")).AuditInstanceId;
            AssertOk((await secrets.UpdateAsync(caller, "alpha", new MetadataUpdateInput { ExpirationSettings = CreationInput().ExpirationSettings, Tags = new() { "x" } })).Outcome);
            AssertOk(await access.GrantAsync(caller, "alpha", ReaderCanRead()));

            var content = await contents.CreateAsync(caller, "alpha", string.Empty, new ContentMetadataCreationInput { ContentType = "application/pdf", FileName = "report.pdf", IsImage = false });
            AssertOk(content.Outcome);
            var contentId = content.Value!.ContentName;
            var upload = await chunks.UploadChunkAsync(caller, "alpha", contentId, new MemoryStream(Hello), new ChunkUploadRequest
            {
                AccessTicket = string.Empty,
                ChunkHash = Sha256Hex(Hello),
                DeclaredLength = Hello.Length,
                ExpectedIndex = 0,
            });
            AssertOk(upload.Outcome);
            AssertOk((await contents.CommitAsync(caller, "alpha", contentId, upload.Value!.AccessTicket!, new ContentCommitInput(Sha256Hex(Hello)))).Outcome);
            var download = await chunks.DownloadChunkAsync(caller, "alpha", contentId, upload.Value!.ChunkName);
            AssertOk(download.Outcome);
            await download.Value!.WriteToAsync(new MemoryStream());

            AssertOk(await access.RevokeAsync(caller, "alpha", ReaderCanRead()));
            AssertOk(await secrets.DeleteAsync(caller, "alpha"));

            return await this.DbContext.SecretAuditEvents.AsNoTracking()
                .Where(e => e.AuditInstanceId == auditInstanceId)
                .OrderBy(e => e.SequenceNumber)
                .ToListAsync();
        }

        private AccessService CreateAccessService()
        {
            var features = new Features();
            var orphanedSecretConfiguration = new OrphanedSecretConfiguration();
            var orphanedSecretManager = new OrphanedSecretManager(
                Mock.Of<IOptionsMonitor<Features>>(x => x.CurrentValue == features),
                Mock.Of<IOptionsMonitor<OrphanedSecretConfiguration>>(x => x.CurrentValue == orphanedSecretConfiguration),
                NullLogger<OrphanedSecretManager>.Instance);
            return new AccessService(
                this.DbContext, new GroupsManager(this.DbContext, NullLogger<GroupsManager>.Instance), this.PermissionsManager,
                this.AuditWriter, orphanedSecretManager, NullLogger<AccessService>.Instance);
        }

        private static List<SubjectPermissionsInput> ReaderCanRead()
            => new() { new SubjectPermissionsInput { SubjectType = SubjectTypeInput.User, SubjectName = ReaderUpn, CanRead = true } };

        private static string Sha256Hex(byte[] data)
            => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        private static void AssertOk(ServiceResult outcome)
            => Assert.That(outcome.IsOk, Is.True, $"Arrange: {outcome.Status} {outcome.Error}");
    }
}
```

The lifecycle hits every service call site: `SecretService` create / update / delete (through the purger),
`AccessService` grant and revoke (`AppendPermissionEventAsync`; patch uses the same helper), `ChunkService`
`ContentWritten` (hashed upload; the legacy upload uses the same `EmitContentWrittenAsync`) and `ContentRead`
(emitted only when `WriteToAsync` runs), `ContentService` commit. The `AuditWriter` is the real one over EF
InMemory (`ServiceTestBase`), so this is end-to-end down to the stored row.

- [ ] **Step 2: Run it to see the MCP case fail**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~AuditAttributionServiceTests" 2>&1 | Select-String "Failed |Expected:|But was:|Passed!|Failed!"`
Expected:
```
  Failed McpCaller_EveryEventCarriesChannelAndLabel
  Expected: all items equal to "mcp"
  But was:  < <string.Empty>, <string.Empty>, <string.Empty>, <string.Empty>, <string.Empty>, <string.Empty>, <string.Empty>, <string.Empty> >
Failed!  - Failed:     1, Passed:     1, Skipped:     0, Total:     2, …
```
(The REST test already passes: empty is the default. The event-type list assertion passing in the MCP test proves
the lifecycle is complete.)

- [ ] **Step 3: Attribute the explicit delete in the purger**

In `SafeExchange.Core\Purger\IPurger.cs` replace

```csharp
        /// <summary>
        /// Delete a secret and stamp its audit anchor with the supplied actor and
        /// retention window. Emits a SecretDeleted audit event before the metadata
        /// row is removed.
        /// </summary>
        public Task PurgeAsync(string secretId, SafeExchangeDbContext dbContext, IAuditWriter auditWriter, SubjectType actorType, string actorId, int auditRetentionDays);
```

with

```csharp
        /// <summary>
        /// Delete a secret and stamp its audit anchor with the supplied actor and
        /// retention window. Emits a SecretDeleted audit event before the metadata
        /// row is removed, attributed to <paramref name="origin"/> (default: none).
        /// </summary>
        public Task PurgeAsync(string secretId, SafeExchangeDbContext dbContext, IAuditWriter auditWriter, SubjectType actorType, string actorId, int auditRetentionDays, AuditOrigin? origin = null);
```

In `SafeExchange.Core\Purger\PurgeManager.cs` replace

```csharp
        public Task PurgeAsync(string secretId, SafeExchangeDbContext dbContext, IAuditWriter auditWriter, SubjectType actorType, string actorId, int auditRetentionDays)
            => this.PurgeInternalAsync(secretId, dbContext, auditWriter, actorType, actorId, auditRetentionDays);

        private async Task PurgeInternalAsync(string secretId, SafeExchangeDbContext dbContext, IAuditWriter? auditWriter, SubjectType actorType, string actorId, int auditRetentionDays)
```

with

```csharp
        public Task PurgeAsync(string secretId, SafeExchangeDbContext dbContext, IAuditWriter auditWriter, SubjectType actorType, string actorId, int auditRetentionDays, AuditOrigin? origin = null)
            => this.PurgeInternalAsync(secretId, dbContext, auditWriter, actorType, actorId, auditRetentionDays, origin);

        private async Task PurgeInternalAsync(string secretId, SafeExchangeDbContext dbContext, IAuditWriter? auditWriter, SubjectType actorType, string actorId, int auditRetentionDays, AuditOrigin? origin = null)
```

and, in the same method, replace

```csharp
                    actorType, actorId, payload: new { }, this.log);
```

with

```csharp
                    actorType, actorId, payload: new { }, this.log, origin);
```

(The two `PurgeIfNeededAsync` paths and the audit-less `PurgeAsync(secretId, dbContext)` keep calling
`PurgeInternalAsync` without an origin. Both files already have `using SafeExchange.Core.Audit;`.)

- [ ] **Step 4: Pass the origin at every service call site**

All four service files already have `using SafeExchange.Core.Audit;`.

`SafeExchange.Core\Services\SecretService.cs` — in `CreateAsync` replace

```csharp
                        caller.SubjectType, caller.SubjectId, creationPayload, this.log);
```

with

```csharp
                        caller.SubjectType, caller.SubjectId, creationPayload, this.log, AuditOrigin.FromCaller(caller));
```

in `UpdateAsync` replace

```csharp
                        caller.SubjectType, caller.SubjectId, diffObj, this.log);
```

with

```csharp
                        caller.SubjectType, caller.SubjectId, diffObj, this.log, AuditOrigin.FromCaller(caller));
```

and in `DeleteAsync` replace

```csharp
                await this.purger.PurgeAsync(secretId, this.dbContext, this.auditWriter, caller.SubjectType, caller.SubjectId, this.features.AuditRetentionDays);
```

with

```csharp
                await this.purger.PurgeAsync(secretId, this.dbContext, this.auditWriter, caller.SubjectType, caller.SubjectId, this.features.AuditRetentionDays, AuditOrigin.FromCaller(caller));
```

`SafeExchange.Core\Services\ContentService.cs` — in `CommitAsync` replace

```csharp
                    new { contentId, fileName = existingContent.FileName, contentType = existingContent.ContentType }, this.log);
```

with

```csharp
                    new { contentId, fileName = existingContent.FileName, contentType = existingContent.ContentType }, this.log,
                    AuditOrigin.FromCaller(caller));
```

`SafeExchange.Core\Services\ChunkService.cs` — replace

```csharp
        private ValueTask EmitContentReadAsync(ObjectMetadata secret, string contentId, string chunkId, Caller caller)
            => this.auditWriter.AppendAsync(secret, SecretAuditEventType.ContentRead, caller.SubjectType, caller.SubjectId, new { contentId, chunkId }, this.log);

        private ValueTask EmitContentWrittenAsync(ObjectMetadata secret, string contentId, string chunkId, Caller caller)
            => this.auditWriter.AppendAsync(secret, SecretAuditEventType.ContentWritten, caller.SubjectType, caller.SubjectId, new { contentId, chunkId }, this.log);
```

with

```csharp
        private ValueTask EmitContentReadAsync(ObjectMetadata secret, string contentId, string chunkId, Caller caller)
            => this.auditWriter.AppendAsync(secret, SecretAuditEventType.ContentRead, caller.SubjectType, caller.SubjectId, new { contentId, chunkId }, this.log, AuditOrigin.FromCaller(caller));

        private ValueTask EmitContentWrittenAsync(ObjectMetadata secret, string contentId, string chunkId, Caller caller)
            => this.auditWriter.AppendAsync(secret, SecretAuditEventType.ContentWritten, caller.SubjectType, caller.SubjectId, new { contentId, chunkId }, this.log, AuditOrigin.FromCaller(caller));
```

`SafeExchange.Core\Services\AccessService.cs` — at the end of `AppendPermissionEventAsync` replace

```csharp
                    permissions = new
                    {
                        from = AuditPayloads.PermissionFlags(beforePerm),
                        to = AuditPayloads.PermissionFlags(afterPerm),
                    },
                }, this.log);
```

with

```csharp
                    permissions = new
                    {
                        from = AuditPayloads.PermissionFlags(beforePerm),
                        to = AuditPayloads.PermissionFlags(afterPerm),
                    },
                }, this.log, AuditOrigin.FromCaller(caller));
```

Then confirm no service call site was missed — the two counts must match file by file:

Run:
```powershell
git grep -c "auditWriter.AppendAsync(\|purger.PurgeAsync(" -- SafeExchange.Core/Services
git grep -c "AuditOrigin.FromCaller(caller)" -- SafeExchange.Core/Services
```
Expected (both commands, at `7bbcfb2` and `527e8af`):
```
SafeExchange.Core/Services/AccessService.cs:1
SafeExchange.Core/Services/ChunkService.cs:2
SafeExchange.Core/Services/ContentService.cs:1
SafeExchange.Core/Services/SecretService.cs:3
```

- [ ] **Step 5: Run the tests to see them pass**

Run:
```powershell
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~AuditAttributionServiceTests|FullyQualifiedName~SafeExchange.Tests.Services|FullyQualifiedName~SafeExchange.Tests.Contract" 2>&1 | Select-String "Passed!|Failed!"
```
Expected: `Passed!  - Failed:     0, Passed:   284, …` at `7bbcfb2`, `… Passed:   286, …` at `527e8af` (Task 0's
second count + 2).

---

### Task 5: expose the origin in audit responses (`SecretAuditEventOutput`, `ContentReadMerger`)

**Files:**
- Modify: `SafeExchange.Core\Model\Dto\Output\SecretAuditEventOutput.cs`, `SafeExchange.Core\Audit\ContentReadMerger.cs`
- Test: `SafeExchange.Tests\Tests\ContentReadMergerTests.cs`, `SafeExchange.Tests\Services\AuditQueryServiceTests.cs`

- [ ] **Step 1: Write the failing merger and JSON tests**

In `SafeExchange.Tests\Tests\ContentReadMergerTests.cs` replace

```csharp
    using System;
    using System.Collections.Generic;
```

with

```csharp
    using System;
    using System.Collections.Generic;
    using System.Linq;
```

replace the `MakeRead` signature

```csharp
        private static SecretAuditEvent MakeRead(long seq, string actorId, string contentId, string chunkId)
```

with

```csharp
        private static SecretAuditEvent MakeRead(long seq, string actorId, string contentId, string chunkId, string channel = "", string clientLabel = "")
```

and in `MakeRead`'s initializer (the first `Hash = "h" + seq,` in the file, inside `return new SecretAuditEvent`)
replace

```csharp
                Hash = "h" + seq,
            };
        }
```

with

```csharp
                Hash = "h" + seq,
                Channel = channel,
                ClientLabel = clientLabel,
            };
        }
```

Then insert these tests directly before `public void Merge_NonReadEventOnly_PassesThrough()`'s `[Test]` attribute:

```csharp
        [Test]
        public void Merge_McpAndRestReadsOfTheSameContent_AreNotMerged()
        {
            var events = new List<SecretAuditEvent>
            {
                MakeRead(1, "a@x", "c1", "k1"),
                MakeRead(2, "a@x", "c1", "k2", "mcp", "agent/1"),
            };
            var merged = ContentReadMerger.Merge(events);
            Assert.That(merged.Count, Is.EqualTo(2));
            Assert.That(merged.Select(m => m.Channel), Is.EqualTo(new[] { null, "mcp" }));
        }

        [Test]
        public void Merge_DifferentClientLabels_AreNotMerged()
        {
            var events = new List<SecretAuditEvent>
            {
                MakeRead(1, "a@x", "c1", "k1", "mcp", "agent/1"),
                MakeRead(2, "a@x", "c1", "k2", "mcp", "agent/2"),
            };
            Assert.That(ContentReadMerger.Merge(events).Count, Is.EqualTo(2));
        }

        [Test]
        public void Merge_SameOrigin_MergesAndKeepsTheOrigin()
        {
            var events = new List<SecretAuditEvent>
            {
                MakeRead(1, "a@x", "c1", "k1", "mcp", "agent/1"),
                MakeRead(2, "a@x", "c1", "k2", "mcp", "agent/1"),
            };
            var merged = ContentReadMerger.Merge(events);
            Assert.That(merged.Count, Is.EqualTo(1));
            Assert.That(merged[0].ChunkIds, Is.EqualTo(new[] { "k1", "k2" }));
            Assert.That(merged[0].Channel, Is.EqualTo("mcp"));
            Assert.That(merged[0].ClientLabel, Is.EqualTo("agent/1"));
        }

        [Test]
        public void Raw_ExposesTheOriginOnlyWhenSet()
        {
            var events = new List<SecretAuditEvent>
            {
                MakeOther(1, SecretAuditEventType.SecretCreated),
                MakeRead(2, "a@x", "c1", "k1", "mcp", "agent/1"),
            };
            var raw = ContentReadMerger.Raw(events);
            Assert.That(raw[0].Channel, Is.Null);
            Assert.That(raw[0].ClientLabel, Is.Null);
            Assert.That(raw[1].Channel, Is.EqualTo("mcp"));
            Assert.That(raw[1].ClientLabel, Is.EqualTo("agent/1"));
        }

        [Test]
        public void Json_OmitsTheOriginWhenEmpty_AndWritesItWhenSet()
        {
            var events = new List<SecretAuditEvent>
            {
                MakeOther(1, SecretAuditEventType.SecretCreated),
                MakeRead(2, "a@x", "c1", "k1", "mcp", "agent/1"),
            };
            var raw = ContentReadMerger.Raw(events);
            var restJson = DefaultJsonSerializer.Serialize(raw[0]);
            var mcpJson = DefaultJsonSerializer.Serialize(raw[1]);
            Assert.That(restJson, Does.Not.Contain("channel").And.Not.Contain("clientLabel"));
            Assert.That(mcpJson, Does.EndWith(",\"channel\":\"mcp\",\"clientLabel\":\"agent/1\"}"));
        }

```

(`DefaultJsonSerializer` — camelCase, `WhenWritingNull` — is in the already-imported `SafeExchange.Core`
namespace and matches the REST options. The byte-identity of real REST responses is proven by the frozen
contract tests.)

- [ ] **Step 2: Write the failing query-service test**

In `SafeExchange.Tests\Services\AuditQueryServiceTests.cs` replace

```csharp
    using NUnit.Framework;
    using SafeExchange.Core.Model;
```

with

```csharp
    using NUnit.Framework;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Model;
```

and insert before `public void Parse_ReadsTheRestQueryString()`'s `[Test]` attribute:

```csharp
        [Test]
        public async Task Read_ExposesTheOriginOfMcpEventsOnly()
        {
            await this.CreateSecretAsync("alpha", auditEnabled: true);
            var mcpOwner = this.Owner with { Channel = CallerChannels.Mcp, ClientLabel = "copilot-cli/1.0.114" };
            await this.CreateSecretService().UpdateAsync(mcpOwner, "alpha", new MetadataUpdateInput { ExpirationSettings = CreationInput().ExpirationSettings, Tags = new() { "x" } });

            var result = await this.service.ReadAsync(this.Owner, "alpha", new AuditPageQuery());

            Assert.That(result.Value!.Events.Select(e => (e.EventType, e.Channel, e.ClientLabel)), Is.EqualTo(new[]
            {
                (nameof(SecretAuditEventType.SecretCreated), (string?)null, (string?)null),
                (nameof(SecretAuditEventType.SecretMetadataUpdated), "mcp", "copilot-cli/1.0.114"),
            }));
        }

```

- [ ] **Step 3: Run them to see them fail**

Run:
```powershell
dotnet build SafeExchange.Tests\SafeExchange.Tests.csproj --nologo -v q 2>&1 | Out-String -Width 500 | Select-String -Pattern "error CS[^\[]*" -AllMatches | ForEach-Object { $_.Matches.Value } | Sort-Object -Unique
```
Expected (build fails):
```
error CS1061: 'SecretAuditEventOutput' does not contain a definition for 'Channel' and no accessible extension method 'Channel' …
error CS1061: 'SecretAuditEventOutput' does not contain a definition for 'ClientLabel' and no accessible extension method 'ClientLabel' …
```

- [ ] **Step 4: Add the DTO fields**

In `SafeExchange.Core\Model\Dto\Output\SecretAuditEventOutput.cs` replace the header

```csharp
/// SecretAuditEventOutput — DTO for a single audit event in API responses. Sequential
/// ContentRead events for the same (actor, contentId) are merged into a single output
/// item with a populated ChunkIds list; non-merged events use the scalar fields only.
```

with

```csharp
/// SecretAuditEventOutput — DTO for a single audit event in API responses. Sequential
/// ContentRead events for the same (actor, contentId, channel, clientLabel) are merged into a
/// single output item with a populated ChunkIds list; non-merged events use the scalar fields only.
```

and replace the end of `SecretAuditEventOutput`

```csharp
        public string PrevHash { get; set; } = string.Empty;
    }
```

with

```csharp
        public string PrevHash { get; set; } = string.Empty;

        /// <summary>
        /// "mcp" for events produced through MCP; null (omitted from JSON) for REST, system and older events.
        /// </summary>
        public string? Channel { get; set; }

        /// <summary>
        /// The MCP client's self-reported label; null (omitted from JSON) when none was recorded.
        /// </summary>
        public string? ClientLabel { get; set; }
    }
```

- [ ] **Step 5: Map the origin and add it to the merge key**

In `SafeExchange.Core\Audit\ContentReadMerger.cs` replace the header

```csharp
/// ContentReadMerger — collapses runs of ContentRead events for the same (actor, contentId)
/// into single output DTO items. Pure function; never mutates the input list and never reads
/// the secret's actual content (only the contentId/chunkId from the stored payload JSON).
```

with

```csharp
/// ContentReadMerger — collapses runs of ContentRead events for the same (actor, contentId, channel,
/// clientLabel) into single output DTO items, so reads through different channels or MCP clients stay
/// separate. Pure function; never mutates the input list and never reads the secret's actual content
/// (only the contentId/chunkId from the stored payload JSON).
```

replace

```csharp
                while (j < events.Count
                    && events[j].EventType == SecretAuditEventType.ContentRead
                    && events[j].ActorSubjectId == e.ActorSubjectId)
```

with

```csharp
                while (j < events.Count
                    && events[j].EventType == SecretAuditEventType.ContentRead
                    && events[j].ActorSubjectId == e.ActorSubjectId
                    && events[j].Channel == e.Channel
                    && events[j].ClientLabel == e.ClientLabel)
```

replace (in the merged item)

```csharp
                    ChunkIds = chunkIds,
                    Hash = hashEnd,
                    PrevHash = prevHashStart,
                });
```

with

```csharp
                    ChunkIds = chunkIds,
                    Hash = hashEnd,
                    PrevHash = prevHashStart,
                    Channel = NullIfEmpty(e.Channel),
                    ClientLabel = NullIfEmpty(e.ClientLabel),
                });
```

and replace (end of `ToDto`)

```csharp
                Payload = payload,
                Hash = e.Hash,
                PrevHash = e.PrevHash,
            };
        }
```

with

```csharp
                Payload = payload,
                Hash = e.Hash,
                PrevHash = e.PrevHash,
                Channel = NullIfEmpty(e.Channel),
                ClientLabel = NullIfEmpty(e.ClientLabel),
            };
        }

        private static string? NullIfEmpty(string value)
            => string.IsNullOrEmpty(value) ? null : value;
```

- [ ] **Step 6: Run the tests to see them pass**

Run:
```powershell
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~ContentReadMergerTests|FullyQualifiedName~AuditQueryServiceTests|FullyQualifiedName~SecretAuditDirectionTests" 2>&1 | Select-Object -Last 1
```
Expected: `Passed!  - Failed:     0, Passed:    21, Skipped:     0, Total:    21, …` (13 merger, 6 query service,
2 direction).

- [ ] **Step 7: Prove REST audit responses are unchanged**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchange.Tests.Contract" 2>&1 | Select-Object -Last 1`
Expected: `Passed!  - Failed:     0, Passed:   212, …` (includes `SecretAuditContractTests`, which compare the audit
JSON byte for byte).

---

### Task 6: documentation

**Files:**
- Modify: `docs\data-model.md`, `docs\superpowers\specs\2026-05-14-secret-audit-design.md`

- [ ] **Step 1: Document the fields in the data model**

In `docs\data-model.md`, section `### SecretAuditEvent`, replace

```markdown
| PrevHash / Hash | SHA-256 hash chain for tamper-evidence. |
```

with

```markdown
| PrevHash / Hash | SHA-256 hash chain for tamper-evidence. |
| Channel / ClientLabel | Origin of the action: `mcp` and the MCP client's self-reported label (printable ASCII, at most 128 characters) for events produced through the MCP endpoint; empty for REST, system and older events. Hashed only when non-empty. The label carries no authority. |
```

- [ ] **Step 2: Document the fields and the hash rule in the audit design**

In `docs\superpowers\specs\2026-05-14-secret-audit-design.md` replace

```markdown
| `Hash` | `string` | Base64 SHA-256 over a canonical concatenation (§ Hash chain). |
```

with

```markdown
| `Hash` | `string` | Base64 SHA-256 over a canonical concatenation (§ Hash chain). |
| `Channel` | `string` | `mcp` for events produced through the MCP endpoint; empty for REST and system events. Absent on documents written before it existed (read as empty). |
| `ClientLabel` | `string` | The MCP client's self-reported label (printable ASCII, at most 128 characters); empty otherwise. Carries no authority. |
```

and replace

```markdown
   `Payload` is the exact JSON string that will be persisted (single canonical serialization: compact, no whitespace, `JsonSerializerOptions.Default` property name policy via `DefaultJsonSerializer.Serialize`). `OccurredAt` is formatted with the round-trip `O` specifier (UTC). All separator pipes are literal `|` characters.
```

with

```markdown
   `Payload` is the exact JSON string that will be persisted (single canonical serialization: compact, no whitespace, `JsonSerializerOptions.Default` property name policy via `DefaultJsonSerializer.Serialize`). `OccurredAt` is formatted with the round-trip `O` specifier (UTC). All separator pipes are literal `|` characters.
   When `Channel` or `ClientLabel` is non-empty, `|{Channel}|{ClientLabel}` is appended to that string. When both are empty (REST and system events, and every event written before these fields existed) the string is exactly the one above, so those hashes are unchanged.
```

(`docs/api-endpoints.md` and `docs/mcp.md` are phase 7's; they should mention the two optional response fields.)

---

### Task 7: full verification and the phase commit

**Files:** none new; stages everything above plus this plan.

- [ ] **Step 1: Build without warnings drift**

Run: `dotnet build SafeExchange.slnx --nologo -v q --no-incremental 2>&1 | Select-Object -Last 4`
Expected: `0 Error(s)` and the **same warning count as Task 0** (704 at `7bbcfb2`).

- [ ] **Step 2: Run the phase filter**

Run:
```powershell
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchange.Tests.Contract|FullyQualifiedName~SafeExchange.Tests.Architecture|FullyQualifiedName~SafeExchange.Tests.Services|FullyQualifiedName~Audit|FullyQualifiedName~ContentReadMerger" 2>&1 | Select-String "Passed!|Failed!"
```
Expected: `Passed!  - Failed:     0, Passed:   356, …` at `7bbcfb2`, `… 358, …` at `527e8af` — Task 0's first count
+ 23 (6 origin, 3 hasher, 4 writer, 2 Cosmos, 2 attribution, 5 merger, 1 query). `TearDown failed …` lines from
Cosmos fixtures are the known emulator issue.

- [ ] **Step 3: Run the emulator-backed audit fixtures**

Run:
```powershell
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~AuditAnchorWriteTests|FullyQualifiedName~AuditEventCosmosTests|FullyQualifiedName~SecretAccessAuditTests" 2>&1 | Select-String "Passed!|Failed!"
```
Expected: `Passed!  - Failed:     0, Passed:    17, …` (`SecretAccessAuditTests` uses `RecordingAuditWriter` through
the REST handlers and stays green with the new `Origin` member).

- [ ] **Step 4 (optional, ~3 minutes): full suite**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build 2>&1 | Select-String "Passed!|Failed!"`
Expected: `Passed!  - Failed:     0, Passed:   946, …` on `7bbcfb2` + this phase, `… 948, …` on `527e8af` + this
phase (needs the emulator).

- [ ] **Step 5: Check the change set**

Run: `git status --short`
Expected exactly (order may differ):
```
 M SafeExchange.Core/Audit/AuditEventHasher.cs
 M SafeExchange.Core/Audit/AuditWriter.cs
 M SafeExchange.Core/Audit/ContentReadMerger.cs
 M SafeExchange.Core/Audit/IAuditWriter.cs
 M SafeExchange.Core/Model/Dto/Output/SecretAuditEventOutput.cs
 M SafeExchange.Core/Model/SecretAuditEvent.cs
 M SafeExchange.Core/Purger/IPurger.cs
 M SafeExchange.Core/Purger/PurgeManager.cs
 M SafeExchange.Core/Services/AccessService.cs
 M SafeExchange.Core/Services/ChunkService.cs
 M SafeExchange.Core/Services/ContentService.cs
 M SafeExchange.Core/Services/SecretService.cs
 M SafeExchange.Tests/Services/AuditQueryServiceTests.cs
 M SafeExchange.Tests/Tests/AuditEventHasherTests.cs
 M SafeExchange.Tests/Tests/AuditWriterTests.cs
 M SafeExchange.Tests/Tests/ContentReadMergerTests.cs
 M SafeExchange.Tests/Utilities/NoOpAuditWriter.cs
 M SafeExchange.Tests/Utilities/RecordingAuditWriter.cs
 M docs/data-model.md
 M docs/superpowers/specs/2026-05-14-secret-audit-design.md
?? SafeExchange.Core/Audit/AuditOrigin.cs
?? SafeExchange.Tests/Services/AuditAttributionServiceTests.cs
?? SafeExchange.Tests/Tests/AuditEventCosmosTests.cs
?? SafeExchange.Tests/Tests/AuditOriginTests.cs
?? docs/superpowers/plans/2026-11-21-mcp-p5-audit-attribution.md
```
Also run `git diff --check` — expected: no output.

- [ ] **Step 6: Commit**

```powershell
git add SafeExchange.Core/Audit/AuditOrigin.cs SafeExchange.Core/Audit/AuditEventHasher.cs SafeExchange.Core/Audit/AuditWriter.cs SafeExchange.Core/Audit/ContentReadMerger.cs SafeExchange.Core/Audit/IAuditWriter.cs SafeExchange.Core/Model/Dto/Output/SecretAuditEventOutput.cs SafeExchange.Core/Model/SecretAuditEvent.cs SafeExchange.Core/Purger/IPurger.cs SafeExchange.Core/Purger/PurgeManager.cs SafeExchange.Core/Services/AccessService.cs SafeExchange.Core/Services/ChunkService.cs SafeExchange.Core/Services/ContentService.cs SafeExchange.Core/Services/SecretService.cs SafeExchange.Tests/Services/AuditAttributionServiceTests.cs SafeExchange.Tests/Services/AuditQueryServiceTests.cs SafeExchange.Tests/Tests/AuditEventCosmosTests.cs SafeExchange.Tests/Tests/AuditEventHasherTests.cs SafeExchange.Tests/Tests/AuditOriginTests.cs SafeExchange.Tests/Tests/AuditWriterTests.cs SafeExchange.Tests/Tests/ContentReadMergerTests.cs SafeExchange.Tests/Utilities/NoOpAuditWriter.cs SafeExchange.Tests/Utilities/RecordingAuditWriter.cs docs/data-model.md docs/superpowers/specs/2026-05-14-secret-audit-design.md docs/superpowers/plans/2026-11-21-mcp-p5-audit-attribution.md
git commit -m "feat(audit): record MCP channel and client label on audit events" -m "SecretAuditEvent gains Channel and ClientLabel. AuditOrigin (Core/Audit) sanitises them (printable ASCII, max 128 chars) and FromCaller maps only MCP callers, so REST and system events stay empty. IAuditWriter.AppendAsync and the explicit-delete IPurger.PurgeAsync take an optional AuditOrigin; every service call site passes the caller's origin. AuditEventHasher appends |channel|clientLabel only when one is non-empty, so existing and REST hashes are unchanged. Audit responses expose channel/clientLabel only when set (omitted otherwise, REST byte-identical) and ContentRead runs are never merged across origins. Old Cosmos documents without the properties read as empty." -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

- [ ] **Step 7: Verify the commit**

Run: `git show --stat --format="%s" HEAD`
Expected: subject `feat(audit): record MCP channel and client label on audit events` and the 25 files from
Step 5 (5 added, 20 modified); `git status --short` prints nothing.

---

## Risks and notes for later phases

- **Phase 6 (MCP endpoint).** Build MCP callers with `CallerChannels.Mcp` and the decoded label; decode with
  `AuditOrigin.Sanitize` (empty → `null`) so `Caller.ClientLabel` equals what is stored. Every audited service
  call then records the origin automatically — no tool code touches the audit writer. `whoami` should report the
  sanitised label. MCP downloads must call `ContentDownload.WriteToAsync` (phase 4 note) or no `ContentRead`
  (and so no attribution) is written.
- **External `IAuditWriter` / `IPurger` implementers** (a downstream consumer's own writer or purger) must add
  the new optional parameter; callers are source-compatible. Phase 7's consumer migration note must say so.
- **New documents grow by two short properties** (`"Channel":"","ClientLabel":""` for REST events). No index or
  query uses them.
- **Canonical-string ambiguity.** The canonical form is `|`-joined and the label may contain `|` (it is printable
  ASCII), as could the payload before this phase. A row editor could in theory shift characters between `Channel`
  and `ClientLabel` without changing the hash (e.g. `mcp` + `a|b` vs `mcp|a` + `b`). The channel is server-set to
  `mcp`, so a verifier can reject any other channel value; the label has no authority. Accepted; if it ever
  matters, switch to length-prefixed fields in a versioned hash.
- **Old SPA/clients** ignore the two new optional response fields; they only appear on MCP-produced events.
- **Emulator teardown.** On `vnext-EN20260907` every Cosmos fixture's database delete fails with `500 …
  E58P01` and `dotnet test` exits non-zero while reporting `Failed: 0`. CI's emulator image should be checked in
  the final phase; nothing in this phase depends on it.
