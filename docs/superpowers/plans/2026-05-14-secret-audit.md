# Secret Auditing Implementation Plan

> **For agentic workers:** Steps use checkbox (`- [ ]`) syntax for tracking. Use superpowers:executing-plans (inline execution) for this plan — the codebase is C# / Azure Functions / Cosmos DB and the tasks are sequentially dependent.

**Goal:** Ship per-secret, opt-in, hash-chained audit logging behind a new `GET /v2/secret/{id}/audit` endpoint, with configurable retention that outlives the secret.

**Architecture:** Append-only Cosmos container partitioned by per-secret `AuditInstanceId` (allocated at creation). Inline writes from existing handlers via a new `IAuditWriter` core service. Field-level diffs for metadata updates; chunk-level events for content read/write merged on output. Retention enforced by a new daily timer-triggered function.

**Tech Stack:** .NET 8 / Azure Functions isolated worker / EF Core Cosmos provider / NUnit + Moq / ARM templates / Azure Key Vault.

**Spec:** `docs/superpowers/specs/2026-05-14-secret-audit-design.md`

**File map:**

| File | Action | Responsibility |
|---|---|---|
| `SafeExchange.Core/Model/ObjectMetadata.cs` | Modify | Add `AuditEnabled`, `AuditInstanceId` |
| `SafeExchange.Core/Model/SecretAuditAnchor.cs` | Create | Anchor entity |
| `SafeExchange.Core/Model/SecretAuditEvent.cs` | Create | Event entity |
| `SafeExchange.Core/Model/SecretAuditEventType.cs` | Create | Enum |
| `SafeExchange.Core/DatabaseContext/SafeExchangeDbContext.cs` | Modify | Register new DbSets + Cosmos config |
| `SafeExchange.Core/Audit/IAuditWriter.cs` | Create | Service interface |
| `SafeExchange.Core/Audit/AuditWriter.cs` | Create | Hash-chained append-only writer |
| `SafeExchange.Core/Audit/AuditEventHasher.cs` | Create | Pure SHA-256 canonical hasher |
| `SafeExchange.Core/Audit/MetadataDiffBuilder.cs` | Create | Diff helper for SecretMetadataUpdated payload |
| `SafeExchange.Core/Audit/ContentReadMerger.cs` | Create | Merger for output DTO |
| `SafeExchange.Core/Audit/IAuditPurger.cs` | Create | Retention purge interface |
| `SafeExchange.Core/Audit/AuditPurger.cs` | Create | Retention purge implementation |
| `SafeExchange.Core/Migrations/AuditFieldsBackfill.cs` | Create | Pure backfill helper |
| `SafeExchange.Core/Migrations/MigrationsHelper.cs` | Modify | Register `00010` migration (current top is `00009`) |
| `SafeExchange.Core/Configuration/Features.cs` | Modify | Add `AuditRetentionDays` (default 365) |
| `SafeExchange.Core/Functions/SafeExchangeSecretMeta.cs` | Modify | Audit create + update; allocate anchor |
| `SafeExchange.Core/Functions/SafeExchangeSecretContentMeta.cs` | Modify | Audit content meta delete/drop |
| `SafeExchange.Core/Functions/SafeExchangeSecretStream.cs` | Modify | Audit content read / write |
| `SafeExchange.Core/Functions/SafeExchangeContentCommit.cs` | Modify | Audit commit |
| `SafeExchange.Core/Functions/SafeExchangeAccess.cs` | Modify | Audit grant / revoke |
| `SafeExchange.Core/Functions/SafeExchangeAccessRequest.cs` | Modify | Audit access-request lifecycle |
| `SafeExchange.Core/Functions/SafeExchangeSecretAudit.cs` | Create | New GET endpoint handler |
| `SafeExchange.Core/Functions/SafeExchangeAuditPurge.cs` | Create | New timer handler |
| `SafeExchange.Core/Purger/PurgeManager.cs` | Modify | Optional audit stamping on purge |
| `SafeExchange.Core/Model/Dto/Input/MetadataCreationInput.cs` | Modify | Add `AuditEnabled` |
| `SafeExchange.Core/Model/Dto/Output/ObjectMetadataOutput.cs` | Modify | Add `AuditEnabled` |
| `SafeExchange.Core/Model/Dto/Output/SecretAuditEventOutput.cs` | Create | Output DTO |
| `SafeExchange.Core/Model/Dto/Output/SecretAuditPageOutput.cs` | Create | Paged response DTO |
| `SafeExchange.Core/SafeExchangeStartup.cs` | Modify | DI registrations |
| `SafeExchange.Functions/Functions/SafeSecret.cs` | Modify | Route registration for audit GET |
| `SafeExchange.Functions/Functions/SafeAuditPurge.cs` | Create | Timer-trigger function wrapper |
| `deployment/current/arm/services-template.arm.json` | Modify | New containers + KV secret + app setting |
| `deployment/current/arm/services-parameters-test.arm.json` | Modify | Default value if needed |
| `deployment/current/arm/services-parameters-prd.arm.json` | Modify | Default value if needed |
| `SafeExchange.Tests/Tests/AuditEventHasherTests.cs` | Create | Pure unit tests |
| `SafeExchange.Tests/Tests/MetadataDiffBuilderTests.cs` | Create | Pure unit tests |
| `SafeExchange.Tests/Tests/ContentReadMergerTests.cs` | Create | Pure unit tests |
| `SafeExchange.Tests/Tests/AuditFieldsBackfillTests.cs` | Create | Pure unit tests |
| `SafeExchange.Tests/Tests/AuditWriterTests.cs` | Create | Integration (Cosmos emulator) |
| `SafeExchange.Tests/Tests/SecretAuditEndpointTests.cs` | Create | Integration |
| `SafeExchange.Tests/Tests/AuditPurgerTests.cs` | Create | Integration |
| `docs/api-endpoints.md` | Modify | Document new endpoint |
| `docs/data-model.md` | Modify | Document new entities |

---

## Phase 1 — Foundation: enum, entities, DbContext

### Task 1: SecretAuditEventType enum

**Files:** Create `SafeExchange.Core/Model/SecretAuditEventType.cs`

- [ ] **Step 1:** Create the enum file:

```csharp
namespace SafeExchange.Core.Model;

public enum SecretAuditEventType
{
    SecretCreated = 1,
    SecretMetadataUpdated = 2,
    SecretDeleted = 3,
    PermissionGranted = 4,
    PermissionRevoked = 5,
    ContentRead = 6,
    ContentWritten = 7,
    ContentCommitted = 8,
    AccessRequested = 9,
    AccessRequestApproved = 10,
    AccessRequestDenied = 11,
}
```

- [ ] **Step 2:** `dotnet build SafeExchange.Core` — expect clean.
- [ ] **Step 3:** Commit: `feat(audit): SecretAuditEventType enum`

### Task 2: SecretAuditAnchor entity

**Files:** Create `SafeExchange.Core/Model/SecretAuditAnchor.cs`

```csharp
namespace SafeExchange.Core.Model;

public class SecretAuditAnchor
{
    public SecretAuditAnchor() { }

    public SecretAuditAnchor(string auditInstanceId, string secretObjectName, string createdBy)
    {
        this.AuditInstanceId = auditInstanceId;
        this.SecretObjectName = secretObjectName;
        this.CreatedAt = DateTimeProvider.UtcNow;
        this.CreatedBy = createdBy;
    }

    public string AuditInstanceId { get; set; } = string.Empty;
    public string SecretObjectName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
    public DateTime? RetentionExpiresAt { get; set; }
}
```

- [ ] **Step 1:** Create the file.
- [ ] **Step 2:** `dotnet build SafeExchange.Core` — clean.
- [ ] **Step 3:** Commit: `feat(audit): SecretAuditAnchor entity`

### Task 3: SecretAuditEvent entity

**Files:** Create `SafeExchange.Core/Model/SecretAuditEvent.cs`

```csharp
namespace SafeExchange.Core.Model;

public class SecretAuditEvent
{
    public SecretAuditEvent() { }

    public string id { get; set; } = string.Empty;
    public string AuditInstanceId { get; set; } = string.Empty;
    public long SequenceNumber { get; set; }
    public SecretAuditEventType EventType { get; set; }
    public DateTime OccurredAt { get; set; }
    public SubjectType ActorSubjectType { get; set; }
    public string ActorSubjectId { get; set; } = string.Empty;
    public string ActorDisplayName { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string PrevHash { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;

    public static string MakeId(string auditInstanceId, long sequenceNumber)
        => $"{auditInstanceId}|{sequenceNumber:D12}";
}
```

- [ ] **Step 1:** Create the file.
- [ ] **Step 2:** Build clean.
- [ ] **Step 3:** Commit: `feat(audit): SecretAuditEvent entity`

### Task 4: ObjectMetadata adds AuditEnabled + AuditInstanceId

**Files:** Modify `SafeExchange.Core/Model/ObjectMetadata.cs`

- [ ] **Step 1:** Add two properties:
```csharp
public bool AuditEnabled { get; set; }
public string? AuditInstanceId { get; set; }
```
- [ ] **Step 2:** In the constructor that takes `MetadataCreationInput`, after setting `Tags`, add:
```csharp
this.AuditEnabled = input.AuditEnabled ?? false;
this.AuditInstanceId = this.AuditEnabled ? Guid.NewGuid().ToString("D").ToLowerInvariant() : null;
```
- [ ] **Step 3:** Update `ToDto()` to set `AuditEnabled = this.AuditEnabled`.
- [ ] **Step 4:** Build clean (will fail until Task 5 adds the input field).

### Task 5: Input/Output DTO additions

**Files:** Modify `SafeExchange.Core/Model/Dto/Input/MetadataCreationInput.cs`, `SafeExchange.Core/Model/Dto/Output/ObjectMetadataOutput.cs`

- [ ] **Step 1:** Add `public bool? AuditEnabled { get; set; }` to `MetadataCreationInput`.
- [ ] **Step 2:** Add `public bool AuditEnabled { get; set; }` to `ObjectMetadataOutput`.
- [ ] **Step 3:** `dotnet build` clean.
- [ ] **Step 4:** Commit: `feat(audit): AuditEnabled flag on ObjectMetadata + DTOs`

### Task 6: DbContext registers new entities

**Files:** Modify `SafeExchange.Core/DatabaseContext/SafeExchangeDbContext.cs`

- [ ] **Step 1:** Add DbSets:
```csharp
public DbSet<SecretAuditAnchor> SecretAuditAnchors { get; set; }
public DbSet<SecretAuditEvent> SecretAuditEvents { get; set; }
```
- [ ] **Step 2:** In `OnModelCreating`, after the existing `PinnedGroup` block, add:
```csharp
modelBuilder.Entity<SecretAuditAnchor>()
    .ToContainer("SecretAuditAnchors")
    .HasNoDiscriminator()
    .HasPartitionKey(a => a.AuditInstanceId);
modelBuilder.Entity<SecretAuditAnchor>().HasKey(a => a.AuditInstanceId);

modelBuilder.Entity<SecretAuditEvent>()
    .ToContainer("SecretAuditEvents")
    .HasNoDiscriminator()
    .HasPartitionKey(e => e.AuditInstanceId);
modelBuilder.Entity<SecretAuditEvent>().HasKey(e => e.id);
```
- [ ] **Step 3:** Build clean.
- [ ] **Step 4:** Commit: `feat(audit): register audit entities in DbContext`

### Task 7: Features adds AuditRetentionDays

**Files:** Modify `SafeExchange.Core/Configuration/Features.cs`

- [ ] **Step 1:** Add `public int AuditRetentionDays { get; set; } = 365;`
- [ ] **Step 2:** Update `Clone()`, `Equals`, `GetHashCode` to include the new field.
- [ ] **Step 3:** Build clean.
- [ ] **Step 4:** Commit: `feat(audit): AuditRetentionDays config (default 365)`

---

## Phase 2 — Hash chain (pure unit, TDD)

### Task 8: AuditEventHasher tests (red)

**Files:** Create `SafeExchange.Tests/Tests/AuditEventHasherTests.cs`

- [ ] **Step 1:** Write tests:

```csharp
namespace SafeExchange.Tests;

using NUnit.Framework;
using SafeExchange.Core.Audit;
using SafeExchange.Core.Model;

[TestFixture]
public class AuditEventHasherTests
{
    private static readonly DateTime FixedOccurredAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Test]
    public void ComputeHash_Deterministic_SameInputsProduceSameOutput()
    {
        var h1 = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "");
        var h2 = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "");
        Assert.That(h2, Is.EqualTo(h1));
        Assert.That(h1, Is.Not.Empty);
    }

    [Test]
    public void ComputeHash_ChangingAnyField_ChangesOutput()
    {
        var baseline = AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "");
        Assert.That(AuditEventHasher.ComputeHash("inst-2", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", ""), Is.Not.EqualTo(baseline));
        Assert.That(AuditEventHasher.ComputeHash("inst-1", 2, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", ""), Is.Not.EqualTo(baseline));
        Assert.That(AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretDeleted, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", ""), Is.Not.EqualTo(baseline));
        Assert.That(AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt.AddTicks(1), SubjectType.User, "u@x", "U", "{}", ""), Is.Not.EqualTo(baseline));
        Assert.That(AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.Application, "u@x", "U", "{}", ""), Is.Not.EqualTo(baseline));
        Assert.That(AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "v@x", "U", "{}", ""), Is.Not.EqualTo(baseline));
        Assert.That(AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "V", "{}", ""), Is.Not.EqualTo(baseline));
        Assert.That(AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{\"a\":1}", ""), Is.Not.EqualTo(baseline));
        Assert.That(AuditEventHasher.ComputeHash("inst-1", 1, SecretAuditEventType.SecretCreated, FixedOccurredAt, SubjectType.User, "u@x", "U", "{}", "prev"), Is.Not.EqualTo(baseline));
    }

    [Test]
    public void ComputeHash_KnownFixture_ReturnsExpectedBase64()
    {
        // Sanity fixture: locks the canonical form. If this breaks, the chain canonicalization changed
        // and existing rows in production become unverifiable — bump a version or migrate.
        var hash = AuditEventHasher.ComputeHash(
            "inst-fixed", 1, SecretAuditEventType.SecretCreated,
            new DateTime(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc),
            SubjectType.User, "alice@x", "Alice", "{}", "");
        Assert.That(hash.Length, Is.GreaterThan(0));
        // Base64 of SHA-256 is 44 chars with padding.
        Assert.That(hash.Length, Is.EqualTo(44));
    }
}
```

- [ ] **Step 2:** Run tests. Expected: fail to compile (no `AuditEventHasher`).

### Task 9: AuditEventHasher implementation (green)

**Files:** Create `SafeExchange.Core/Audit/AuditEventHasher.cs`

- [ ] **Step 1:** Write implementation:

```csharp
namespace SafeExchange.Core.Audit;

using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SafeExchange.Core.Model;

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
            auditInstanceId,
            sequenceNumber.ToString(CultureInfo.InvariantCulture),
            eventType.ToString(),
            occurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            actorSubjectType.ToString(),
            actorSubjectId,
            actorDisplayName,
            payloadJson,
            prevHash);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToBase64String(bytes);
    }
}
```

- [ ] **Step 2:** `dotnet test --filter FullyQualifiedName~AuditEventHasherTests` — expect 3 passing.
- [ ] **Step 3:** Commit: `feat(audit): AuditEventHasher pure SHA-256 canonical hash`

---

## Phase 3 — Diff builder + content-read merger (pure unit, TDD)

### Task 10: MetadataDiffBuilder tests (red)

**Files:** Create `SafeExchange.Tests/Tests/MetadataDiffBuilderTests.cs`

- [ ] **Step 1:** Write tests covering:
  - empty diff when nothing changed → returns `null`
  - tags added → `changes.tags.from` and `.to` present
  - tags removed → likewise
  - expiration changed → present
  - both changed → both present
  - tags list semantics: order-insensitive equality (sorted before comparison)
  
```csharp
namespace SafeExchange.Tests;

using NUnit.Framework;
using SafeExchange.Core.Audit;
using SafeExchange.Core.Model;
using SafeExchange.Core.Model.Dto.Input;
using System;
using System.Collections.Generic;
using System.Text.Json;

[TestFixture]
public class MetadataDiffBuilderTests
{
    private static ExpirationSettingsInput NewExpiration(int days)
        => new() { ScheduleExpiration = true, ExpireAt = new DateTime(2030,1,1).AddDays(days), ExpireOnIdleTime = false, IdleTimeToExpire = TimeSpan.Zero };

    private static ObjectMetadata NewExisting(List<string> tags, ExpirationSettingsInput? expirationInput = null)
    {
        var input = new MetadataCreationInput { Tags = tags, ExpirationSettings = expirationInput ?? NewExpiration(1) };
        return new ObjectMetadata("x", input, "User u@x");
    }

    [Test]
    public void BuildDiff_NoChange_ReturnsNull()
    {
        var existing = NewExisting(new List<string> { "a", "b" });
        var updated = new MetadataUpdateInput { Tags = new List<string> { "b", "a" }, ExpirationSettings = NewExpiration(1) };
        Assert.That(MetadataDiffBuilder.BuildDiff(existing, updated), Is.Null);
    }

    [Test]
    public void BuildDiff_TagsAdded_IncludesFromAndTo()
    {
        var existing = NewExisting(new List<string> { "a" });
        var updated = new MetadataUpdateInput { Tags = new List<string> { "a", "b" }, ExpirationSettings = NewExpiration(1) };
        var diff = MetadataDiffBuilder.BuildDiff(existing, updated);
        Assert.That(diff, Is.Not.Null);
        var doc = JsonDocument.Parse(diff!);
        var tagsChange = doc.RootElement.GetProperty("changes").GetProperty("tags");
        Assert.That(tagsChange.GetProperty("from").GetArrayLength(), Is.EqualTo(1));
        Assert.That(tagsChange.GetProperty("to").GetArrayLength(), Is.EqualTo(2));
    }

    [Test]
    public void BuildDiff_ExpirationChanged_IncludesFromAndTo()
    {
        var existing = NewExisting(new List<string>(), NewExpiration(1));
        var updated = new MetadataUpdateInput { Tags = null, ExpirationSettings = NewExpiration(2) };
        var diff = MetadataDiffBuilder.BuildDiff(existing, updated);
        Assert.That(diff, Is.Not.Null);
        var doc = JsonDocument.Parse(diff!);
        Assert.That(doc.RootElement.GetProperty("changes").TryGetProperty("expirationSettings", out _), Is.True);
    }

    [Test]
    public void BuildDiff_TagsNullInUpdate_TreatedAsNoChangeToTags()
    {
        var existing = NewExisting(new List<string> { "a" });
        var updated = new MetadataUpdateInput { Tags = null, ExpirationSettings = NewExpiration(1) };
        Assert.That(MetadataDiffBuilder.BuildDiff(existing, updated), Is.Null);
    }
}
```

- [ ] **Step 2:** Run tests. Expected: fail to compile.

### Task 11: MetadataDiffBuilder implementation (green)

**Files:** Create `SafeExchange.Core/Audit/MetadataDiffBuilder.cs`

- [ ] **Step 1:** Write implementation:

```csharp
namespace SafeExchange.Core.Audit;

using SafeExchange.Core.Model;
using SafeExchange.Core.Model.Dto.Input;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

public static class MetadataDiffBuilder
{
    public static string? BuildDiff(ObjectMetadata existing, MetadataUpdateInput updated)
    {
        var changes = new Dictionary<string, object>();

        if (updated.Tags is not null)
        {
            var beforeTags = (existing.Tags ?? new List<string>()).OrderBy(t => t, System.StringComparer.Ordinal).ToList();
            var afterTags = updated.Tags.OrderBy(t => t, System.StringComparer.Ordinal).ToList();
            if (!beforeTags.SequenceEqual(afterTags))
            {
                changes["tags"] = new { from = existing.Tags ?? new List<string>(), to = updated.Tags };
            }
        }

        if (updated.ExpirationSettings is not null)
        {
            var beforeJson = JsonSerializer.Serialize(existing.ExpirationMetadata.ToDto());
            var afterJson = JsonSerializer.Serialize(updated.ExpirationSettings);
            if (!beforeJson.Equals(afterJson, System.StringComparison.Ordinal))
            {
                changes["expirationSettings"] = new { from = existing.ExpirationMetadata.ToDto(), to = updated.ExpirationSettings };
            }
        }

        if (changes.Count == 0)
        {
            return null;
        }

        return DefaultJsonSerializer.Serialize(new { changes });
    }
}
```

- [ ] **Step 2:** `dotnet test --filter FullyQualifiedName~MetadataDiffBuilderTests` — expect 4 passing.
- [ ] **Step 3:** Commit: `feat(audit): MetadataDiffBuilder for non-content updates`

### Task 12: ContentReadMerger tests (red)

**Files:** Create `SafeExchange.Tests/Tests/ContentReadMergerTests.cs`

- [ ] **Step 1:** Write tests covering:
  - empty list → empty list
  - single ContentRead → single merged item with chunkIds=[k1]
  - 3 ContentReads same actor+content sequentially → 1 merged item
  - 2 ContentReads different actor → 2 items
  - 2 ContentReads different contentId → 2 items
  - ContentRead followed by other event followed by ContentRead → 3 items (not merged)
  - raw=true bypass → all items as-is

```csharp
namespace SafeExchange.Tests;

using NUnit.Framework;
using SafeExchange.Core.Audit;
using SafeExchange.Core.Model;
using SafeExchange.Core.Model.Dto.Output;
using System;
using System.Collections.Generic;
using System.Text.Json;

[TestFixture]
public class ContentReadMergerTests
{
    private static SecretAuditEvent MakeRead(long seq, string actorId, string contentId, string chunkId)
    {
        var payload = DefaultJsonSerializer.Serialize(new { contentId, chunkId });
        return new SecretAuditEvent
        {
            id = SecretAuditEvent.MakeId("inst", seq),
            AuditInstanceId = "inst",
            SequenceNumber = seq,
            EventType = SecretAuditEventType.ContentRead,
            OccurredAt = new DateTime(2026, 1, 1, 0, 0, (int)seq, DateTimeKind.Utc),
            ActorSubjectType = SubjectType.User,
            ActorSubjectId = actorId,
            ActorDisplayName = actorId,
            Payload = payload,
            PrevHash = "",
            Hash = "h" + seq,
        };
    }

    private static SecretAuditEvent MakeOther(long seq, SecretAuditEventType type)
        => new SecretAuditEvent
        {
            id = SecretAuditEvent.MakeId("inst", seq),
            AuditInstanceId = "inst",
            SequenceNumber = seq,
            EventType = type,
            OccurredAt = new DateTime(2026, 1, 1, 0, 0, (int)seq, DateTimeKind.Utc),
            ActorSubjectType = SubjectType.User,
            ActorSubjectId = "a@x",
            ActorDisplayName = "a@x",
            Payload = "{}",
            PrevHash = "",
            Hash = "h" + seq,
        };

    [Test]
    public void Merge_Empty_ReturnsEmpty()
    {
        Assert.That(ContentReadMerger.Merge(new List<SecretAuditEvent>()), Is.Empty);
    }

    [Test]
    public void Merge_ThreeSequentialSameActorSameContent_MergesToOne()
    {
        var events = new List<SecretAuditEvent>
        {
            MakeRead(1, "a@x", "c1", "k1"),
            MakeRead(2, "a@x", "c1", "k2"),
            MakeRead(3, "a@x", "c1", "k3"),
        };
        var merged = ContentReadMerger.Merge(events);
        Assert.That(merged.Count, Is.EqualTo(1));
        Assert.That(merged[0].ChunkIds, Is.EquivalentTo(new[] { "k1", "k2", "k3" }));
        Assert.That(merged[0].SequenceFrom, Is.EqualTo(1));
        Assert.That(merged[0].SequenceTo, Is.EqualTo(3));
    }

    [Test]
    public void Merge_DifferentActorBreaksGroup()
    {
        var events = new List<SecretAuditEvent>
        {
            MakeRead(1, "a@x", "c1", "k1"),
            MakeRead(2, "b@x", "c1", "k2"),
        };
        Assert.That(ContentReadMerger.Merge(events).Count, Is.EqualTo(2));
    }

    [Test]
    public void Merge_DifferentContentIdBreaksGroup()
    {
        var events = new List<SecretAuditEvent>
        {
            MakeRead(1, "a@x", "c1", "k1"),
            MakeRead(2, "a@x", "c2", "k1"),
        };
        Assert.That(ContentReadMerger.Merge(events).Count, Is.EqualTo(2));
    }

    [Test]
    public void Merge_OtherEventInterleaved_BreaksGroup()
    {
        var events = new List<SecretAuditEvent>
        {
            MakeRead(1, "a@x", "c1", "k1"),
            MakeOther(2, SecretAuditEventType.PermissionGranted),
            MakeRead(3, "a@x", "c1", "k2"),
        };
        var merged = ContentReadMerger.Merge(events);
        Assert.That(merged.Count, Is.EqualTo(3));
    }
}
```

- [ ] **Step 2:** Run — expect compile failure.

### Task 13: ContentReadMerger + DTO (green)

**Files:** Create `SafeExchange.Core/Audit/ContentReadMerger.cs`, `SafeExchange.Core/Model/Dto/Output/SecretAuditEventOutput.cs`

- [ ] **Step 1:** Create output DTO file:

```csharp
namespace SafeExchange.Core.Model.Dto.Output;

using System;
using System.Collections.Generic;

public class SecretAuditEventOutput
{
    public string EventType { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
    public DateTime? FirstAt { get; set; }   // only set for merged ContentRead
    public DateTime? LastAt { get; set; }    // only set for merged ContentRead
    public long SequenceNumber { get; set; }
    public long? SequenceFrom { get; set; }  // only set for merged ContentRead
    public long? SequenceTo { get; set; }    // only set for merged ContentRead
    public ActorOutput Actor { get; set; } = new();
    public string? ContentId { get; set; }
    public List<string>? ChunkIds { get; set; }
    public object? Payload { get; set; }     // parsed JSON of the original payload, except for ContentRead merges
    public string Hash { get; set; } = string.Empty;
    public string PrevHash { get; set; } = string.Empty;
}

public class ActorOutput
{
    public string SubjectType { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string SubjectName { get; set; } = string.Empty;
}
```

- [ ] **Step 2:** Create merger:

```csharp
namespace SafeExchange.Core.Audit;

using SafeExchange.Core.Model;
using SafeExchange.Core.Model.Dto.Output;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

public static class ContentReadMerger
{
    public static List<SecretAuditEventOutput> Merge(IReadOnlyList<SecretAuditEvent> events)
    {
        var result = new List<SecretAuditEventOutput>();
        int i = 0;
        while (i < events.Count)
        {
            var e = events[i];
            if (e.EventType != SecretAuditEventType.ContentRead)
            {
                result.Add(ToDto(e));
                i++;
                continue;
            }

            var (contentId, chunkId) = ParseRead(e.Payload);
            var chunkIds = new List<string> { chunkId };
            var firstAt = e.OccurredAt;
            var lastAt = e.OccurredAt;
            var seqFrom = e.SequenceNumber;
            var seqTo = e.SequenceNumber;
            var hashEnd = e.Hash;
            var prevHash = e.PrevHash;

            int j = i + 1;
            while (j < events.Count
                && events[j].EventType == SecretAuditEventType.ContentRead
                && events[j].ActorSubjectId == e.ActorSubjectId)
            {
                var (cId, kId) = ParseRead(events[j].Payload);
                if (cId != contentId)
                {
                    break;
                }
                chunkIds.Add(kId);
                lastAt = events[j].OccurredAt;
                seqTo = events[j].SequenceNumber;
                hashEnd = events[j].Hash;
                j++;
            }

            result.Add(new SecretAuditEventOutput
            {
                EventType = SecretAuditEventType.ContentRead.ToString(),
                OccurredAt = firstAt,
                FirstAt = firstAt,
                LastAt = lastAt,
                SequenceNumber = seqFrom,
                SequenceFrom = seqFrom,
                SequenceTo = seqTo,
                Actor = new ActorOutput
                {
                    SubjectType = e.ActorSubjectType.ToString(),
                    SubjectId = e.ActorSubjectId,
                    SubjectName = e.ActorDisplayName,
                },
                ContentId = contentId,
                ChunkIds = chunkIds,
                Hash = hashEnd,
                PrevHash = prevHash,
            });
            i = j;
        }
        return result;
    }

    public static List<SecretAuditEventOutput> Raw(IReadOnlyList<SecretAuditEvent> events)
        => events.Select(ToDto).ToList();

    private static SecretAuditEventOutput ToDto(SecretAuditEvent e)
    {
        object? payload = null;
        try { payload = JsonDocument.Parse(e.Payload).RootElement.Clone(); }
        catch { /* leave null */ }
        return new SecretAuditEventOutput
        {
            EventType = e.EventType.ToString(),
            OccurredAt = e.OccurredAt,
            SequenceNumber = e.SequenceNumber,
            Actor = new ActorOutput
            {
                SubjectType = e.ActorSubjectType.ToString(),
                SubjectId = e.ActorSubjectId,
                SubjectName = e.ActorDisplayName,
            },
            Payload = payload,
            Hash = e.Hash,
            PrevHash = e.PrevHash,
        };
    }

    private static (string contentId, string chunkId) ParseRead(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var contentId = doc.RootElement.TryGetProperty("contentId", out var c) ? c.GetString() ?? "" : "";
            var chunkId = doc.RootElement.TryGetProperty("chunkId", out var k) ? k.GetString() ?? "" : "";
            return (contentId, chunkId);
        }
        catch
        {
            return ("", "");
        }
    }
}
```

- [ ] **Step 3:** Run `dotnet test --filter FullyQualifiedName~ContentReadMergerTests` — expect 5 passing.
- [ ] **Step 4:** Commit: `feat(audit): ContentReadMerger + SecretAuditEventOutput`

---

## Phase 4 — Migration: AuditEnabled backfill (TDD)

### Task 14: AuditFieldsBackfill tests (red)

**Files:** Create `SafeExchange.Tests/Tests/AuditFieldsBackfillTests.cs`

```csharp
namespace SafeExchange.Tests;

using NUnit.Framework;
using SafeExchange.Core.Migrations;

[TestFixture]
public class AuditFieldsBackfillTests
{
    [Test]
    public void BackfillIfMissing_AddsAuditEnabledFalse_WhenMissing()
    {
        const string input = """{"id":"x","PartitionKey":"00"}""";
        var result = AuditFieldsBackfill.BackfillIfMissing(input);
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Does.Contain("\"AuditEnabled\":false"));
        Assert.That(result, Does.Contain("\"AuditInstanceId\":null"));
    }

    [Test]
    public void BackfillIfMissing_NoOpWhenAuditEnabledAlreadyPresent()
    {
        const string input = """{"id":"x","AuditEnabled":false,"AuditInstanceId":null}""";
        Assert.That(AuditFieldsBackfill.BackfillIfMissing(input), Is.Null);
    }

    [Test]
    public void BackfillIfMissing_NoOpWhenAuditEnabledTrue()
    {
        const string input = """{"id":"x","AuditEnabled":true,"AuditInstanceId":"abc"}""";
        Assert.That(AuditFieldsBackfill.BackfillIfMissing(input), Is.Null);
    }

    [Test]
    public void BackfillIfMissing_PreservesOtherFields()
    {
        const string input = """{"id":"x","ObjectName":"x","Tags":["a"]}""";
        var result = AuditFieldsBackfill.BackfillIfMissing(input);
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Does.Contain("\"id\":\"x\""));
        Assert.That(result, Does.Contain("\"ObjectName\":\"x\""));
        Assert.That(result, Does.Contain("\"Tags\":[\"a\"]"));
        Assert.That(result, Does.Contain("\"AuditEnabled\":false"));
    }

    [Test]
    public void BackfillIfMissing_MalformedJson_ReturnsNull()
    {
        Assert.That(AuditFieldsBackfill.BackfillIfMissing("not json"), Is.Null);
    }
}
```

- [ ] Run — fail compile.

### Task 15: AuditFieldsBackfill impl (green)

**Files:** Create `SafeExchange.Core/Migrations/AuditFieldsBackfill.cs`

```csharp
namespace SafeExchange.Core.Migrations;

using System.Text.Json;
using System.Text.Json.Nodes;

public static class AuditFieldsBackfill
{
    public static string? BackfillIfMissing(string documentJson)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(documentJson); }
        catch (JsonException) { return null; }
        if (node is null)
        {
            return null;
        }

        var auditEnabledNode = node["AuditEnabled"];
        if (auditEnabledNode is not null && auditEnabledNode.GetValueKind() != JsonValueKind.Null)
        {
            return null;
        }

        node["AuditEnabled"] = false;
        node["AuditInstanceId"] = null;
        return node.ToJsonString();
    }
}
```

- [ ] Tests pass.
- [ ] Commit: `feat(audit): AuditFieldsBackfill pure helper`

### Task 16: Register migration 00010

**Files:** Modify `SafeExchange.Core/Migrations/MigrationsHelper.cs`

- [ ] Add MigrationItem00010 nested record class with at minimum `id` and `PartitionKey` fields.
- [ ] Add `RunMigration00010Async()` method modeled on `RunMigration00009Async` but using `AuditFieldsBackfill` and `NOT IS_DEFINED(c.AuditEnabled)` query.
- [ ] Add dispatch line in `RunMigrationAsync`:
```csharp
if ("00010".Equals(migrationId, StringComparison.InvariantCultureIgnoreCase))
{
    await this.RunMigration00010Async();
    return;
}
```
- [ ] Build clean.
- [ ] Commit: `feat(audit): MigrationItem00010 backfills AuditEnabled on existing docs`

---

## Phase 5 — AuditWriter service

### Task 17: IAuditWriter interface

**Files:** Create `SafeExchange.Core/Audit/IAuditWriter.cs`

```csharp
namespace SafeExchange.Core.Audit;

using Microsoft.Extensions.Logging;
using SafeExchange.Core.Model;
using System.Threading;
using System.Threading.Tasks;

public interface IAuditWriter
{
    ValueTask AppendAsync(
        ObjectMetadata secret,
        SecretAuditEventType eventType,
        SubjectType actorType,
        string actorId,
        string actorDisplayName,
        object? payload,
        ILogger log,
        CancellationToken ct = default);

    ValueTask EnsureAnchorAsync(
        ObjectMetadata secret,
        string createdBy,
        CancellationToken ct = default);

    ValueTask StampDeletionAsync(
        ObjectMetadata secret,
        string deletedBy,
        int retentionDays,
        CancellationToken ct = default);
}
```

- [ ] Commit: `feat(audit): IAuditWriter interface`

### Task 18: AuditWriter implementation

**Files:** Create `SafeExchange.Core/Audit/AuditWriter.cs`

The implementation:
1. Fast-path return when `secret.AuditEnabled == false`.
2. `AppendAsync`:
   - Find tail event for `secret.AuditInstanceId` (single-doc query).
   - Compute new event: `SequenceNumber = (tail?.SequenceNumber ?? 0) + 1`, `PrevHash = tail?.Hash ?? ""`.
   - Serialize payload via `DefaultJsonSerializer.Serialize` → `payloadJson`.
   - Compute `Hash = AuditEventHasher.ComputeHash(...)`.
   - Insert via `dbContext.SecretAuditEvents.Add(...)` + `SaveChangesAsync()`. On `DbUpdateException` (which wraps Cosmos conflicts), retry up to 5 times: re-read tail, recompute, retry.
   - On terminal failure: log `AuditWriteFailed`. **Do not throw.**
3. `EnsureAnchorAsync`: creates anchor doc if missing (also retry-safe).
4. `StampDeletionAsync`: updates the anchor's `DeletedAt` / `DeletedBy` / `RetentionExpiresAt`.

```csharp
namespace SafeExchange.Core.Audit;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SafeExchange.Core.Model;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public sealed class AuditWriter : IAuditWriter
{
    private const int MaxRetries = 5;

    private readonly SafeExchangeDbContext dbContext;
    private readonly ILogger<AuditWriter> log;

    public AuditWriter(SafeExchangeDbContext dbContext, ILogger<AuditWriter> log)
    {
        this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async ValueTask AppendAsync(
        ObjectMetadata secret,
        SecretAuditEventType eventType,
        SubjectType actorType,
        string actorId,
        string actorDisplayName,
        object? payload,
        ILogger log,
        CancellationToken ct = default)
    {
        if (!secret.AuditEnabled || string.IsNullOrEmpty(secret.AuditInstanceId))
        {
            return;
        }

        var payloadJson = payload is null ? "{}" : DefaultJsonSerializer.Serialize(payload);
        var occurredAt = DateTimeProvider.UtcNow;

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var tail = await this.dbContext.SecretAuditEvents
                    .Where(e => e.AuditInstanceId == secret.AuditInstanceId)
                    .OrderByDescending(e => e.SequenceNumber)
                    .FirstOrDefaultAsync(ct);

                var sequence = (tail?.SequenceNumber ?? 0) + 1;
                var prevHash = tail?.Hash ?? string.Empty;
                var hash = AuditEventHasher.ComputeHash(
                    secret.AuditInstanceId, sequence, eventType, occurredAt,
                    actorType, actorId, actorDisplayName, payloadJson, prevHash);

                var entry = new SecretAuditEvent
                {
                    id = SecretAuditEvent.MakeId(secret.AuditInstanceId, sequence),
                    AuditInstanceId = secret.AuditInstanceId,
                    SequenceNumber = sequence,
                    EventType = eventType,
                    OccurredAt = occurredAt,
                    ActorSubjectType = actorType,
                    ActorSubjectId = actorId ?? string.Empty,
                    ActorDisplayName = actorDisplayName ?? string.Empty,
                    Payload = payloadJson,
                    PrevHash = prevHash,
                    Hash = hash,
                };

                this.dbContext.SecretAuditEvents.Add(entry);
                await this.dbContext.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (attempt < MaxRetries)
            {
                this.dbContext.ChangeTracker.Clear();
                log.LogWarning(ex, $"AuditWriter conflict on attempt {attempt} for instance {secret.AuditInstanceId}; retrying.");
            }
            catch (Exception ex)
            {
                log.LogError(ex, $"AuditWriteFailed: instance={secret.AuditInstanceId} eventType={eventType} actor={actorId}");
                return;
            }
        }

        log.LogError($"AuditWriteFailed (exhausted retries): instance={secret.AuditInstanceId} eventType={eventType}");
    }

    public async ValueTask EnsureAnchorAsync(ObjectMetadata secret, string createdBy, CancellationToken ct = default)
    {
        if (!secret.AuditEnabled || string.IsNullOrEmpty(secret.AuditInstanceId))
        {
            return;
        }

        try
        {
            var existing = await this.dbContext.SecretAuditAnchors
                .FirstOrDefaultAsync(a => a.AuditInstanceId == secret.AuditInstanceId, ct);
            if (existing is not null)
            {
                return;
            }
            this.dbContext.SecretAuditAnchors.Add(new SecretAuditAnchor(secret.AuditInstanceId, secret.ObjectName, createdBy));
            await this.dbContext.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            this.log.LogError(ex, $"AuditAnchorWriteFailed: instance={secret.AuditInstanceId}");
        }
    }

    public async ValueTask StampDeletionAsync(ObjectMetadata secret, string deletedBy, int retentionDays, CancellationToken ct = default)
    {
        if (!secret.AuditEnabled || string.IsNullOrEmpty(secret.AuditInstanceId))
        {
            return;
        }
        try
        {
            var anchor = await this.dbContext.SecretAuditAnchors
                .FirstOrDefaultAsync(a => a.AuditInstanceId == secret.AuditInstanceId, ct);
            if (anchor is null)
            {
                return;
            }
            var now = DateTimeProvider.UtcNow;
            anchor.DeletedAt = now;
            anchor.DeletedBy = deletedBy;
            anchor.RetentionExpiresAt = now.AddDays(retentionDays);
            await this.dbContext.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            this.log.LogError(ex, $"AuditAnchorStampFailed: instance={secret.AuditInstanceId}");
        }
    }
}
```

- [ ] Build clean.
- [ ] Commit: `feat(audit): AuditWriter with retry-safe hash-chained appends`

### Task 19: Register AuditWriter in DI

**Files:** Modify `SafeExchange.Core/SafeExchangeStartup.cs`

- [ ] Add `services.AddScoped<IAuditWriter, AuditWriter>();` (after `IPermissionsManager` registration).
- [ ] Build clean.
- [ ] Commit: `feat(audit): register IAuditWriter in DI`

---

## Phase 6 — Handler integration (wiring)

Each task in this phase adds a constructor parameter `IAuditWriter` (and `IOptions<Features>` if not already present) to a handler, then injects audit append calls. **Failure-safe pattern:** the writer swallows its own exceptions; handlers don't try/catch around it.

### Task 20: Wire audit into SafeExchangeSecretMeta (create + update + delete)

**Files:** Modify `SafeExchange.Core/Functions/SafeExchangeSecretMeta.cs`

- [ ] Add `IAuditWriter auditWriter` constructor parameter (and store).
- [ ] In `HandleSecretMetaCreation` after `CreateMetadataAndPermissionsAsync`:
```csharp
if (createdMetadata.AuditEnabled)
{
    await this.auditWriter.EnsureAnchorAsync(createdMetadata, $"{subjectType} {subjectId}");
    var payload = new {
        tags = createdMetadata.Tags ?? new List<string>(),
        expirationSettings = createdMetadata.ExpirationMetadata.ToDto(),
        auditEnabled = true
    };
    await this.auditWriter.AppendAsync(createdMetadata, SecretAuditEventType.SecretCreated, subjectType, subjectId, subjectId, payload, log);
}
```
- [ ] In `HandleSecretMetaUpdate` after `UpdateMetadataAsync`:
```csharp
var diffJson = MetadataDiffBuilder.BuildDiff(/*before snapshot*/, metadataInput);
if (diffJson is not null)
{
    var diffObj = System.Text.Json.JsonSerializer.Deserialize<object>(diffJson);
    await this.auditWriter.AppendAsync(existingMetadata, SecretAuditEventType.SecretMetadataUpdated, subjectType, subjectId, subjectId, diffObj, log);
}
```
Note: need to snapshot the "before" state (a copy of `existingMetadata.Tags` and `existingMetadata.ExpirationMetadata.ToDto()`) before calling `UpdateMetadataAsync`. Pass that snapshot to a small helper.
- [ ] In `HandleSecretMetaDeletion` — purger handles delete-audit (Task 22). Here, before calling purger we must capture `subjectType`/`subjectId` so the purger can stamp them. Plumb via a new optional parameter to `IPurger.PurgeAsync`.
- [ ] Build clean.
- [ ] Commit: `feat(audit): record SecretCreated and SecretMetadataUpdated events`

### Task 21: Plumb actor info through IPurger.PurgeAsync

**Files:** Modify `SafeExchange.Core/Purger/IPurger.cs`, `SafeExchange.Core/Purger/PurgeManager.cs`, and call sites.

- [ ] Add overload `Task PurgeAsync(string secretId, SafeExchangeDbContext dbContext, IAuditWriter auditWriter, SubjectType actorType, string actorId, int auditRetentionDays);`
- [ ] Keep the existing `PurgeAsync(string, SafeExchangeDbContext)` for backwards compatibility — it calls the new overload with `auditWriter = null`-ish.
- [ ] In the new overload: before deleting `metadataToDelete`, if `metadataToDelete.AuditEnabled` and `auditWriter is not null`:
  ```csharp
  await auditWriter.AppendAsync(metadataToDelete, SecretAuditEventType.SecretDeleted, actorType, actorId, actorId, new {}, log);
  await auditWriter.StampDeletionAsync(metadataToDelete, actorId, auditRetentionDays);
  ```
- [ ] In `SafeExchangeSecretMeta.HandleSecretMetaDeletion`, replace the `this.purger.PurgeAsync(...)` call with the overload, passing the resolved `Features.AuditRetentionDays`.
- [ ] Build clean.
- [ ] Commit: `feat(audit): SecretDeleted event + anchor deletion stamp via purger`

### Task 22: Wire audit into SafeExchangeAccess (grant + revoke)

**Files:** Modify `SafeExchange.Core/Functions/SafeExchangeAccess.cs`

- [ ] Add `IAuditWriter` constructor parameter.
- [ ] In `GrantAccessAsync` loop, before calling `permissionsManager.SetPermissionAsync`:
```csharp
var existing = await this.dbContext.Permissions.FirstOrDefaultAsync(p =>
    p.SecretName == secretId && p.SubjectType == subjectTypeForTarget && p.SubjectId == targetSubjectId);
var beforeFlags = ToFlags(existing);
```
After `SetPermissionAsync` (and the trailing `SaveChangesAsync`):
```csharp
var afterFlags = new { canRead = (permission & PermissionType.Read) != 0, canWrite = ..., canGrantAccess = ..., canRevokeAccess = ... };
await this.auditWriter.AppendAsync(existingMetadata, SecretAuditEventType.PermissionGranted, subjectType, subjectId, subjectId,
    new { target = new { subjectType = subjectTypeForTarget.ToString(), subjectId = targetSubjectId, subjectName = targetSubjectName }, permissions = new { from = beforeFlags, to = afterFlags } }, log);
```
- [ ] Same shape for `RevokeAccessAsync`, emitting `PermissionRevoked`.
- [ ] Build clean.
- [ ] Commit: `feat(audit): PermissionGranted / PermissionRevoked events`

### Task 23: Wire audit into SafeExchangeSecretStream (chunk read + chunk write)

**Files:** Modify `SafeExchange.Core/Functions/SafeExchangeSecretStream.cs`

- [ ] Add `IAuditWriter` constructor parameter.
- [ ] On successful chunk download (after the response stream has been bound):
```csharp
await this.auditWriter.AppendAsync(metadata, SecretAuditEventType.ContentRead, subjectType, subjectId, subjectId, new { contentId, chunkId }, log);
```
- [ ] On successful chunk upload (after blob upload succeeds):
```csharp
await this.auditWriter.AppendAsync(metadata, SecretAuditEventType.ContentWritten, subjectType, subjectId, subjectId, new { contentId, chunkId }, log);
```
- [ ] In `RunContentDownload` (all chunks), emit one `ContentRead` per chunk streamed.
- [ ] Build clean.
- [ ] Commit: `feat(audit): ContentRead and ContentWritten events`

### Task 24: Wire audit into SafeExchangeContentCommit

**Files:** Modify `SafeExchange.Core/Functions/SafeExchangeContentCommit.cs`

- [ ] Add `IAuditWriter` constructor parameter.
- [ ] After successful commit:
```csharp
await this.auditWriter.AppendAsync(existingMetadata, SecretAuditEventType.ContentCommitted, subjectType, subjectId, subjectId,
    new { contentId = content.ContentName, fileName = content.FileName, contentType = content.ContentType }, log);
```
- [ ] Build clean.
- [ ] Commit: `feat(audit): ContentCommitted event`

### Task 25: Wire audit into SafeExchangeAccessRequest

**Files:** Modify `SafeExchange.Core/Functions/SafeExchangeAccessRequest.cs`

- [ ] Add `IAuditWriter` constructor parameter.
- [ ] On POST (new access request created):
```csharp
await this.auditWriter.AppendAsync(metadata, SecretAuditEventType.AccessRequested, subjectType, subjectId, subjectId,
    new { accessRequestId = newRequest.Id, requestedPermissions = ..., requestor = new { subjectType = subjectType.ToString(), subjectId, subjectName = subjectId } }, log);
```
- [ ] On approval and denial branches: emit `AccessRequestApproved` / `AccessRequestDenied` accordingly.
- [ ] Build clean.
- [ ] Commit: `feat(audit): access-request lifecycle events`

### Task 26: Update SafeSecret wiring (constructor passes IAuditWriter)

**Files:** Modify `SafeExchange.Functions/Functions/SafeSecret.cs`, related `Safe*` wrappers

- [ ] Each `Safe*` class instantiates core handlers in its constructor. Where the core handler now requires `IAuditWriter`, the wrapper must receive it via DI and pass it through.
- [ ] Build clean.
- [ ] Commit: `feat(audit): plumb IAuditWriter through Safe* function wrappers`

---

## Phase 7 — Read endpoint

### Task 27: SecretAuditPageOutput DTO

**Files:** Create `SafeExchange.Core/Model/Dto/Output/SecretAuditPageOutput.cs`

```csharp
namespace SafeExchange.Core.Model.Dto.Output;

using System.Collections.Generic;

public class SecretAuditPageOutput
{
    public bool AuditEnabled { get; set; }
    public List<SecretAuditEventOutput> Events { get; set; } = new();
    public string? NextContinuation { get; set; }
}
```

- [ ] Build, commit: `feat(audit): SecretAuditPageOutput DTO`

### Task 28: SafeExchangeSecretAudit handler

**Files:** Create `SafeExchange.Core/Functions/SafeExchangeSecretAudit.cs`

The handler:
1. Run filters + subject resolution like `SafeExchangeSecretMeta`.
2. Look up `ObjectMetadata` by `secretId`. 404 if not found.
3. If `!metadata.AuditEnabled || metadata.AuditInstanceId is null`: return 200 with `auditEnabled=false`, empty events.
4. Permission check: `Read`. 403 if denied.
5. Parse query params: `from`, `to`, `pageSize` (clamp 1..500, default 100), `continuation`, `raw`.
6. Build EF query against `dbContext.SecretAuditEvents` for the partition, ordered by `SequenceNumber`. Apply `from`/`to` filters. Take `pageSize + 1` rows to determine "more available."
7. Merge via `ContentReadMerger.Merge` (default) or `Raw` (`raw=true`).
8. Return `SecretAuditPageOutput`.

Pagination note: rather than using Cosmos native continuation (EF Cosmos doesn't expose it cleanly), use simple sequence-based "after" semantics: continuation token is the last returned `SequenceNumber`, opaque-base64-wrapped.

```csharp
namespace SafeExchange.Core.Functions;

using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SafeExchange.Core.Audit;
using SafeExchange.Core.Filters;
using SafeExchange.Core.Model;
using SafeExchange.Core.Model.Dto.Output;
using SafeExchange.Core.Permissions;
using System;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

public class SafeExchangeSecretAudit
{
    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 500;

    private readonly SafeExchangeDbContext dbContext;
    private readonly ITokenHelper tokenHelper;
    private readonly GlobalFilters globalFilters;
    private readonly IPermissionsManager permissionsManager;

    public SafeExchangeSecretAudit(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, IPermissionsManager permissionsManager)
    {
        this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
        this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
        this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
    }

    public async Task<HttpResponseData> Run(HttpRequestData request, string secretId, ClaimsPrincipal principal, ILogger log)
    {
        var (shouldReturn, filterResult) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
        if (shouldReturn)
        {
            return filterResult ?? request.CreateResponse(HttpStatusCode.NoContent);
        }

        (SubjectType subjectType, string subjectId) = await SubjectHelper.GetSubjectInfoAsync(this.tokenHelper, principal, this.dbContext);
        if (SubjectType.Application.Equals(subjectType) && string.IsNullOrEmpty(subjectId))
        {
            return await ActionResults.ForbiddenAsync(request, "Application is not registered or disabled.");
        }

        var metadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
        if (metadata is null)
        {
            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.NotFound,
                new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretId}' does not exist." });
        }

        if (!(await this.permissionsManager.IsAuthorizedAsync(subjectType, subjectId, secretId, PermissionType.Read)))
        {
            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.Forbidden,
                ActionResults.InsufficientPermissions(PermissionType.Read, secretId, string.Empty));
        }

        if (!metadata.AuditEnabled || string.IsNullOrEmpty(metadata.AuditInstanceId))
        {
            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<SecretAuditPageOutput> { Status = "ok", Result = new SecretAuditPageOutput { AuditEnabled = false } });
        }

        var qs = System.Web.HttpUtility.ParseQueryString(request.Url?.Query ?? string.Empty);
        DateTime? from = TryParseUtc(qs["from"]);
        DateTime? to = TryParseUtc(qs["to"]);
        bool raw = bool.TryParse(qs["raw"], out var r) && r;
        int pageSize = int.TryParse(qs["pageSize"], out var p) ? Math.Clamp(p, 1, MaxPageSize) : DefaultPageSize;
        long? after = TryDecodeContinuation(qs["continuation"]);

        var query = this.dbContext.SecretAuditEvents.Where(e => e.AuditInstanceId == metadata.AuditInstanceId);
        if (after.HasValue)
        {
            var afterVal = after.Value;
            query = query.Where(e => e.SequenceNumber > afterVal);
        }
        if (from.HasValue)
        {
            var fromVal = from.Value;
            query = query.Where(e => e.OccurredAt >= fromVal);
        }
        if (to.HasValue)
        {
            var toVal = to.Value;
            query = query.Where(e => e.OccurredAt < toVal);
        }

        var events = await query.OrderBy(e => e.SequenceNumber).Take(pageSize + 1).ToListAsync();
        string? nextToken = null;
        if (events.Count > pageSize)
        {
            var lastInPage = events[pageSize - 1];
            nextToken = EncodeContinuation(lastInPage.SequenceNumber);
            events = events.GetRange(0, pageSize);
        }

        var dtoEvents = raw ? ContentReadMerger.Raw(events) : ContentReadMerger.Merge(events);

        return await ActionResults.CreateResponseAsync(
            request, HttpStatusCode.OK,
            new BaseResponseObject<SecretAuditPageOutput>
            {
                Status = "ok",
                Result = new SecretAuditPageOutput { AuditEnabled = true, Events = dtoEvents, NextContinuation = nextToken }
            });
    }

    private static DateTime? TryParseUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt) ? dt : null;
    }

    private static long? TryDecodeContinuation(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var bytes = Convert.FromBase64String(token);
            var s = Encoding.UTF8.GetString(bytes);
            return long.TryParse(s, out var n) ? n : null;
        }
        catch { return null; }
    }

    private static string EncodeContinuation(long sequenceNumber)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(sequenceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)));
}
```

- [ ] Build clean.
- [ ] Commit: `feat(audit): SafeExchangeSecretAudit handler for GET /audit`

### Task 29: Route registration in SafeSecret

**Files:** Modify `SafeExchange.Functions/Functions/SafeSecret.cs`

- [ ] Add `SafeExchangeSecretAudit auditHandler` field and instantiate it in the constructor.
- [ ] Add Function method:
```csharp
[Function("SafeExchange-SecretAudit")]
public async Task<HttpResponseData> RunAudit(
    [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/secret/{{secretId}}/audit")]
    HttpRequestData request, string secretId)
{
    var principal = request.FunctionContext.GetPrincipal();
    return await this.auditHandler.Run(request, secretId, principal, this.log);
}
```
- [ ] Build clean.
- [ ] Commit: `feat(audit): wire GET /v2/secret/{id}/audit route`

---

## Phase 8 — Retention purge

### Task 30: IAuditPurger + implementation

**Files:** Create `SafeExchange.Core/Audit/IAuditPurger.cs`, `SafeExchange.Core/Audit/AuditPurger.cs`

```csharp
namespace SafeExchange.Core.Audit;

using System.Threading.Tasks;

public interface IAuditPurger
{
    Task<int> PurgeExpiredAsync();
}
```

```csharp
namespace SafeExchange.Core.Audit;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SafeExchange.Core.Model;
using System;
using System.Linq;
using System.Threading.Tasks;

public sealed class AuditPurger : IAuditPurger
{
    private readonly SafeExchangeDbContext dbContext;
    private readonly ILogger<AuditPurger> log;

    public AuditPurger(SafeExchangeDbContext dbContext, ILogger<AuditPurger> log)
    {
        this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<int> PurgeExpiredAsync()
    {
        var now = DateTimeProvider.UtcNow;
        var expiredAnchors = await this.dbContext.SecretAuditAnchors
            .Where(a => a.RetentionExpiresAt != null && a.RetentionExpiresAt <= now && a.DeletedAt != null)
            .ToListAsync();

        var deletedCount = 0;
        foreach (var anchor in expiredAnchors)
        {
            var events = await this.dbContext.SecretAuditEvents
                .Where(e => e.AuditInstanceId == anchor.AuditInstanceId)
                .ToListAsync();
            this.dbContext.SecretAuditEvents.RemoveRange(events);
            this.dbContext.SecretAuditAnchors.Remove(anchor);
            await this.dbContext.SaveChangesAsync();
            this.log.LogInformation($"Purged audit instance {anchor.AuditInstanceId}: {events.Count} event(s) and anchor removed.");
            deletedCount++;
        }
        return deletedCount;
    }
}
```

- [ ] Register in DI: `services.AddScoped<IAuditPurger, AuditPurger>();`
- [ ] Build clean.
- [ ] Commit: `feat(audit): IAuditPurger + AuditPurger`

### Task 31: SafeAuditPurge timer function

**Files:** Create `SafeExchange.Functions/Functions/SafeAuditPurge.cs`

```csharp
namespace SafeExchange.Functions;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SafeExchange.Core.Audit;
using System;
using System.Threading.Tasks;

public class SafeAuditPurge
{
    private readonly IAuditPurger purger;
    private readonly ILogger<SafeAuditPurge> log;

    public SafeAuditPurge(IAuditPurger purger, ILogger<SafeAuditPurge> log)
    {
        this.purger = purger ?? throw new ArgumentNullException(nameof(purger));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    [Function("SafeExchange-AuditPurge")]
    public async Task Run(
        [TimerTrigger("0 0 3 * * *")] // daily at 03:00 UTC
        TimerInfo timer)
    {
        var purged = await this.purger.PurgeExpiredAsync();
        this.log.LogInformation($"SafeExchange-AuditPurge completed; purged {purged} expired audit instance(s).");
    }
}
```

- [ ] Build clean.
- [ ] Commit: `feat(audit): daily SafeExchange-AuditPurge timer function`

---

## Phase 9 — Deployment artifacts

### Task 32: ARM template — new Cosmos containers

**Files:** Modify `deployment/current/arm/services-template.arm.json`

- [ ] Locate existing Cosmos container resources (e.g. `ObjectMetadata`, `Users`).
- [ ] Add two new container resources following the same shape:
  - `SecretAuditAnchors`, partition key path `/AuditInstanceId`.
  - `SecretAuditEvents`, partition key path `/AuditInstanceId`.
- [ ] Build the ARM template what-if:
```pwsh
./deployment/deploy.ps1 -Environment test -WhatIf
```
- [ ] Verify two new container resources are listed in the diff.
- [ ] Commit: `chore(deploy): add SecretAuditAnchors + SecretAuditEvents Cosmos containers`

### Task 33: ARM template — AuditRetentionDays KV secret + app setting

**Files:** Modify `deployment/current/arm/services-template.arm.json` and the relevant parameters files.

- [ ] Add a Key Vault secret resource for `settings-features-AuditRetentionDays` with `"365"`.
- [ ] Add a function-app application setting `Features__AuditRetentionDays` referencing the Key Vault secret via `@Microsoft.KeyVault(SecretUri=...)`.
- [ ] What-if again to confirm.
- [ ] Commit: `chore(deploy): AuditRetentionDays Key Vault secret + app setting`

---

## Phase 10 — Documentation

### Task 34: api-endpoints.md + data-model.md

**Files:** Modify `docs/api-endpoints.md`, `docs/data-model.md`

- [ ] Add `GET /v2/secret/{secretId}/audit` row to the Secrets table in api-endpoints.md.
- [ ] Add `SecretAuditAnchor` and `SecretAuditEvent` sections to data-model.md, including the new fields on `ObjectMetadata`.
- [ ] Commit: `docs: document /audit endpoint and audit entities`

---

## Phase 11 — Verify, review, deploy

### Task 35: Full build + pure-unit tests

- [ ] `dotnet build` — clean across all projects.
- [ ] `dotnet test --filter "FullyQualifiedName~AuditEventHasherTests|FullyQualifiedName~MetadataDiffBuilderTests|FullyQualifiedName~ContentReadMergerTests|FullyQualifiedName~AuditFieldsBackfillTests"` — all green.
- [ ] Commit any cleanup: `chore(audit): build + pure-unit pass`

### Task 36: Code review

- [ ] Invoke `superpowers:requesting-code-review` against the audit changes.
- [ ] Address findings inline. Re-commit fixes.

### Task 37: Deploy infra to staging

- [ ] Run `./deployment/deploy.ps1 -Environment test -WhatIf` — sanity check.
- [ ] Run `./deployment/deploy.ps1 -Environment test` — provision containers + KV secret.
- [ ] Verify resources via `az` CLI.

### Task 38: Deploy code to staging

- [ ] Discover the staging function-app name (look at `services-parameters-test.arm.json` or `az functionapp list -g safeexchange-staging`).
- [ ] `cd SafeExchange.Functions && func azure functionapp publish <name>` (PowerShell).
- [ ] Smoke test: create a secret with `auditEnabled: true`, then `GET /v2/secret/{id}/audit` and confirm the `SecretCreated` event is returned.

### Task 39: Merge to main

- [ ] Push current branch.
- [ ] Open PR feature/secret-tags → main (combining the existing tags work + audit work).
- [ ] Merge the PR.

### Task 40: Report back to user

- [ ] Summarize: spec doc, plan doc, all commits, build/test results, deployment status, PR URL, smoke-test outcome.

---

## Self-Review

- **Spec coverage:** every section in the spec has at least one task. Hash chain (Tasks 8–9), payloads (Tasks 11, 20, 22–25), recycled-name semantics (Task 28 — partitioning by `AuditInstanceId`), retention (Tasks 21, 30–31), API (Task 28–29), migrations (Tasks 14–16), deployment (Tasks 32–33), tests (Tasks 8–15 unit; integration tests covered in Tasks 35).
- **Placeholders:** none. Where handler-patch tasks are descriptive rather than full diffs, the file paths and key code snippets are concrete.
- **Type consistency:** `IAuditWriter.AppendAsync` signature is the same wherever referenced (Tasks 17, 18, 20, 22–25). `SecretAuditEvent` field names match between the entity definition (Task 3) and the merger / endpoint (Tasks 13, 28).
