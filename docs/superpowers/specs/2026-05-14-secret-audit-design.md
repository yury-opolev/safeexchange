# Secret Auditing — Design

**Status:** Approved
**Date:** 2026-05-14
**Author:** yury-opolev

## Goal

Make every meaningful action on a SafeExchange secret recordable and inspectable so that, later, a UI can answer "who did what to this secret, and when?" without exposing secret content.

## Scope

- Per-secret opt-in via an immutable `AuditEnabled` flag set at creation.
- Events recorded: lifecycle (create / metadata update / delete), permission grant / revoke, content read (per chunk), content write (per chunk) + commit, access-request created / approved / denied.
- New `GET /v2/secret/{secretId}/audit` endpoint, gated by `Read` permission on the secret.
- Append-only, hash-chained storage. No update or delete from application code (purge is the only deletion, run by a scheduled background job for records past retention).
- Audit survives secret deletion for a configurable window (`AuditRetentionDays`, default 365).
- Recycled names get an empty audit log — previous instance's events are not reachable via the recycled name.

## Non-goals

- Auditing of read access lists (`GET /v2/access/{id}`) and read of secret metadata (`GET /v2/secret/{id}`). Recording every read of permission lists or metadata adds noise without forensic value.
- Chain-verification endpoint. The hash chain makes verification possible; we will not ship an HTTP endpoint for it in v1.
- Forensic / admin endpoint to view audit of deleted-and-recycled instances. The data exists in Cosmos for the retention window and is reachable by direct DB query if compliance demands; not via the user-facing API.
- Tamper-proof storage at the Cosmos level (WORM/immutability policy). Considered, rejected because it fights the configurable retention model.

## Invariants

These hold by construction. Anyone extending the audit code must keep them.

1. **No content, ever.** An audit record must never contain secret content bytes, plaintext-derived hashes, or any field derived from plaintext. `ContentMetadata.Hash` is a plaintext-derived integrity hash and is therefore **not** allowed in audit payloads either. Audit may reference opaque identifiers (`contentId`, `chunkId`, `fileName`, `contentType`) but never anything keyed off the plaintext bytes.
2. **Safe to show to readers.** An audit record must be safe to show to any subject with `Read` on the secret without leaking more than they already have.
3. **Append-only.** Application code never updates or deletes a `SecretAuditEvent`. The only deletion path is the retention-purge background job, which deletes whole partitions of expired anchors.
4. **AuditEnabled is set once.** It is decided at creation and cannot be changed. PATCH explicitly ignores any `AuditEnabled` field in the body.
5. **Failure must not block user operations.** Audit-write failures are logged and surface no error to the caller. Auditing is not a denial-of-service vector against legitimate operations.

## Data model

### `ObjectMetadata` (modified)

Adds two fields:

| Field | Type | Notes |
|---|---|---|
| `AuditEnabled` | `bool` | Default `false`. Set only by the create handler. PATCH ignores it. |
| `AuditInstanceId` | `string?` | Lowercase GUID, allocated only when `AuditEnabled = true`. Immutable. Audit-event partition key. |

### `SecretAuditAnchor` (new entity, new Cosmos container `SecretAuditAnchors`)

One anchor per audit-enabled secret. Partition key = `AuditInstanceId`. Outlives `ObjectMetadata` so that audit events of deleted secrets remain reachable until retention expires.

| Field | Type | Notes |
|---|---|---|
| `AuditInstanceId` | `string` | Partition key. Same GUID as on `ObjectMetadata`. |
| `SecretObjectName` | `string` | The secret name at the time of audit-anchor creation. Snapshot; not kept in sync. |
| `CreatedAt` | `DateTime` | UTC. |
| `CreatedBy` | `string` | UPN / client id. |
| `DeletedAt` | `DateTime?` | Stamped on secret delete. |
| `DeletedBy` | `string?` | Subject who triggered the delete. |
| `RetentionExpiresAt` | `DateTime?` | `DeletedAt + AuditRetentionDays`. Purge job acts on this. |

### `SecretAuditEvent` (new entity, new Cosmos container `SecretAuditEvents`)

One row per recorded action. Partition key = `AuditInstanceId`.

| Field | Type | Notes |
|---|---|---|
| `id` | `string` | `{AuditInstanceId}|{SequenceNumber:D12}`. Ensures uniqueness within partition. |
| `AuditInstanceId` | `string` | Partition key. |
| `SequenceNumber` | `long` | Per-`AuditInstanceId` monotonic, starts at 1. |
| `EventType` | `enum` | See list below. |
| `OccurredAt` | `DateTime` | UTC, set via `DateTimeProvider.UtcNow`. |
| `ActorSubjectType` | `enum` | `User` / `Application` / `Group`. |
| `ActorSubjectId` | `string` | UPN / client id. |
| `ActorDisplayName` | `string` | Snapshot at event time (handles later renames). |
| `Payload` | `string` | Canonical JSON. Per-event-type schema (§ Payloads). |
| `PrevHash` | `string` | Base64 SHA-256 of previous record's `Hash`. Empty for `SequenceNumber = 1`. |
| `Hash` | `string` | Base64 SHA-256 over a canonical concatenation (§ Hash chain). |

`EventType` values (string enum, persisted as the name):
- `SecretCreated`
- `SecretMetadataUpdated`
- `SecretDeleted`
- `PermissionGranted`
- `PermissionRevoked`
- `ContentRead`
- `ContentWritten`
- `ContentCommitted`
- `AccessRequested`
- `AccessRequestApproved`
- `AccessRequestDenied`

## Payloads

All payloads are JSON. Strict invariant: no content bytes, no plaintext-derived hashes.

```jsonc
// SecretCreated
{ "tags": ["prod","db"],
  "expirationSettings": { /* shape of ExpirationSettingsOutput */ },
  "auditEnabled": true }

// SecretMetadataUpdated — field-level diff, non-content only
{ "changes": {
    "tags":              { "from": ["prod"], "to": ["prod","db"] },
    "expirationSettings":{ "from": { /* before */ }, "to": { /* after */ } }
  } }
// Emitted only when the diff is non-empty. PATCH with no effective change emits no event.

// SecretDeleted
{ }

// PermissionGranted / PermissionRevoked
{ "target":      { "subjectType":"User", "subjectId":"alice@…", "subjectName":"alice@…" },
  "permissions": { "from": { "canRead":false, "canWrite":false, "canGrantAccess":false, "canRevokeAccess":false },
                   "to":   { "canRead":true,  "canWrite":false, "canGrantAccess":false, "canRevokeAccess":false } } }

// ContentRead — one event per chunk download
{ "contentId":"c-abc", "chunkId":"k-1" }

// ContentWritten — chunk upload
{ "contentId":"c-abc", "chunkId":"k-1" }

// ContentCommitted
{ "contentId":"c-abc", "fileName":"creds.txt", "contentType":"text/plain" }

// AccessRequested / AccessRequestApproved / AccessRequestDenied
{ "accessRequestId":"ar-…",
  "requestedPermissions": { "canRead":true, "canWrite":false, "canGrantAccess":false, "canRevokeAccess":false },
  "requestor":            { "subjectType":"User", "subjectId":"bob@…", "subjectName":"bob@…" } }
```

Permission grant/revoke for **groups** records the group's `SubjectId` + `SubjectName` at action time. Group membership churn is not an audit concern of the secret.

## Hash chain

For each new event:

1. Load the tail record for the partition (`AuditInstanceId`) by `ORDER BY c.SequenceNumber DESC OFFSET 0 LIMIT 1`. Cache its `Hash` as the new record's `PrevHash`; if no tail exists, `PrevHash = ""`.
2. Compute `Hash` = base64(SHA-256(UTF-8 bytes of)):
   ```
   {AuditInstanceId}|{SequenceNumber}|{EventType}|{OccurredAt:O}|{ActorSubjectType}|{ActorSubjectId}|{ActorDisplayName}|{Payload}|{PrevHash}
   ```
   `Payload` is the exact JSON string that will be persisted (single canonical serialization: compact, no whitespace, `JsonSerializerOptions.Default` property name policy via `DefaultJsonSerializer.Serialize`). `OccurredAt` is formatted with the round-trip `O` specifier (UTC). All separator pipes are literal `|` characters.
3. Insert with Cosmos `IfNoneMatch:*`. On `PreconditionFailed` (a concurrent appender allocated the same sequence), retry: re-read the tail, recompute, re-insert. Bounded to 5 attempts; after that, the writer logs `AuditWriteFailed` and returns without throwing.

Verification (offline; no shipped endpoint) is a single linear scan: walk records in `SequenceNumber` order, recompute each hash, confirm `PrevHash` matches the previous record's stored `Hash`. Any break = tampering or missing record.

## Retention

`AuditRetentionDays` (int) is stored in Azure Key Vault as a secret and bound at runtime via the existing Key Vault configuration provider. Default value `365`. Surfaced on the `Features` config class.

On secret deletion (when `AuditEnabled` was true), the existing `IPurger` is updated to:

1. Write a final `SecretDeleted` audit event.
2. Stamp the anchor: `DeletedAt = UtcNow`, `RetentionExpiresAt = UtcNow + AuditRetentionDays`, `DeletedBy = actor`.
3. Hard-delete `ObjectMetadata`, permissions, content, chunks as today. Name is freed immediately.

For secrets where audit was never enabled, the purger's behaviour is unchanged — no anchor doc, no retention work.

A new timer-triggered function **`SafeExchange-AuditPurge`** (daily cadence, mirrors existing `SafePurge`) sweeps `SecretAuditAnchors WHERE RetentionExpiresAt <= now AND DeletedAt != null`. For each: bulk-delete the audit-events partition (all docs with `PartitionKey = AuditInstanceId`), then delete the anchor doc.

## Recycled-name semantics

`GET /v2/secret/{name}/audit` resolves `name → live ObjectMetadata → its AuditInstanceId → events under that partition`.

- A new secret with a recycled name starts with an empty audit log. Its `AuditInstanceId` is freshly minted; the previous instance's events are in a different partition the new secret doesn't reference.
- The previous instance's audit still exists in the DB until its retention window expires, anchored solely by `AuditInstanceId`. The per-secret read API has no path to it from the recycled name — `Read` permission on the new secret does not grant visibility into the deleted instance's history.

## API

### `GET /v2/secret/{secretId}/audit`

Returns the audit log for the live instance of a secret.

- **Auth:** caller must have `Read` on the secret. Reuses `IPermissionsManager.IsAuthorizedAsync(... PermissionType.Read)`.
- **404** if the secret doesn't exist.
- **200** with `{ status: "ok", result: { auditEnabled: false, events: [] } }` if the secret exists but audit was not enabled at creation.

Query params:

| Name | Type | Notes |
|---|---|---|
| `from` | ISO-8601 UTC | Optional inclusive lower bound on `OccurredAt`. |
| `to` | ISO-8601 UTC | Optional exclusive upper bound on `OccurredAt`. |
| `continuation` | opaque string | Server-issued continuation token (base64 of the Cosmos continuation). |
| `pageSize` | int, default 100, max 500 | Server-clamped. |
| `raw` | bool, default `false` | When `true`, return un-merged `ContentRead` events. |

### `ContentRead` merge rule (output only)

Walk events in `SequenceNumber` order. When you encounter a `ContentRead`, start a group keyed by `(ActorSubjectId, contentId)`. Keep extending the group as long as the **immediately next event in the chain** is another `ContentRead` from the same actor against the same `contentId`. The group closes on a different event type or a different `(actor, contentId)`.

Merged DTO item:

```jsonc
{ "eventType": "ContentRead",
  "actor": { "subjectType": "User", "subjectId": "alice@…", "subjectName": "alice@…" },
  "contentId": "c-abc",
  "chunkIds": ["k-1","k-2","k-3","k-4","k-5"],
  "firstAt": "...",
  "lastAt":  "...",
  "sequenceFrom": 42,
  "sequenceTo":   46 }
```

Phrasing "all chunks of content A" is a UI concern — the API returns the list; the UI compares to `ContentMetadata.Chunks.Count`.

### `POST /v2/secret/{secretId}` body change

`MetadataCreationInput` gains optional `bool? AuditEnabled` (default `false`). `MetadataUpdateInput` does **not** get it. `ObjectMetadataOutput` gains `bool AuditEnabled` so clients can render the badge.

## Handler integration

### `IAuditWriter` (new service)

```csharp
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
}
```

The implementation `AuditWriter` (sealed) owns the chain logic. Fast-path returns immediately when `secret.AuditEnabled == false`, so handlers can call `AppendAsync` unconditionally without paying for non-audited secrets. Registered scoped in `Program.cs`.

### Call-site additions

| Handler | New behaviour |
|---|---|
| `SafeExchangeSecretMeta.HandleSecretMetaCreation` | If `MetadataCreationInput.AuditEnabled == true`: allocate `AuditInstanceId`, insert anchor doc, emit `SecretCreated`. |
| `SafeExchangeSecretMeta.HandleSecretMetaUpdate` | Build diff; if non-empty, emit `SecretMetadataUpdated`. Never includes content fields. |
| `IPurger.PurgeAsync` (via deletion path) | If `AuditEnabled`: emit `SecretDeleted`, stamp anchor, then hard-delete `ObjectMetadata`. |
| `SafeExchangeAccess.GrantAccessAsync` | Emit one `PermissionGranted` per target with from/to flags. |
| `SafeExchangeAccess.RevokeAccessAsync` | Emit one `PermissionRevoked` per target. |
| `SafeExchangeSecretStream.Run` (chunk download), `RunContentDownload` (all-chunks) | After successful decrypt + response write: one `ContentRead` per chunk returned. |
| `SafeExchangeSecretStream.Run` (chunk upload) | After successful upload: one `ContentWritten` per chunk accepted. |
| `SafeExchangeContentCommit.Run` | After commit succeeds: `ContentCommitted`. |
| `SafeExchangeAccessRequest.Run` | `AccessRequested` on POST; `AccessRequestApproved` / `AccessRequestDenied` on resolution. |

Each call site catches `AuditWriter` failures via the writer's own try/catch — the user-facing operation always succeeds when the underlying op succeeded.

### New handler `SafeExchangeSecretAudit`

Lives in `SafeExchange.Core/Functions/`. Exposes `Run(HttpRequestData, secretId, principal, ILogger)` for `GET`. Function wrapper added to `SafeSecret` class in `SafeExchange.Functions/Functions/`:

```csharp
[Function("SafeExchange-SecretAudit")]
public Task<HttpResponseData> RunAudit(
    [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/secret/{{secretId}}/audit")]
    HttpRequestData request, string secretId) { ... }
```

### New scheduled function `SafeExchange-AuditPurge`

Lives in `SafeExchange.Functions/Functions/SafeAuditPurge.cs`. Timer trigger; daily cadence; calls a core service `IAuditPurger.PurgeExpiredAsync` that sweeps anchor docs with expired `RetentionExpiresAt`, removes their event partitions, then deletes the anchors.

## Migrations

**`MigrationItem00011`** — backfill `AuditEnabled = false` and `AuditInstanceId = null` on existing `ObjectMetadata` documents that pre-date the feature. Mirrors the `TagsBackfill` pattern: Cosmos-side filter on `NOT IS_DEFINED(c.AuditEnabled)`, idempotent re-runs are no-ops.

Migration registration goes in `MigrationsHelper.RunMigrationAsync`. Item-record type goes alongside the existing `MigrationItem` records.

## Deployment

### ARM template changes (`deployment/current/arm/services-template.arm.json`)

1. Add two Cosmos containers under the existing SQL database resource:
   - `SecretAuditAnchors`, partition key `/AuditInstanceId`.
   - `SecretAuditEvents`, partition key `/AuditInstanceId`.
2. Add one Key Vault secret resource:
   - `settings-features-AuditRetentionDays`, default value `"365"`.
3. Add a function-app application setting `Features__AuditRetentionDays` referencing the Key Vault secret via `@Microsoft.KeyVault(SecretUri=...)`.

### Deploy order

1. ARM template to staging (`./deployment/deploy.ps1 -Environment test`) — provisions containers + Key Vault secret.
2. Verify resources.
3. Function-app code publish (`func azure functionapp publish <staging-app-name>`).
4. Manual smoke test from operator's machine.

Production deploy is out of scope for the initial change.

## Testing

Tests in `SafeExchange.Tests/Tests/Audit/`. NUnit + Moq, matching existing conventions.

**Pure-unit (no Cosmos):**

- `HashChainTests` — building records produces stable hashes; tampering breaks verification at exactly the tampered record.
- `ContentReadMergerTests` — merge rule covers same-actor/same-content runs, event-type breaks, actor breaks, contentId breaks, `?raw=true` bypass.
- `DiffBuilderTests` — `MetadataUpdateInput → SecretMetadataUpdated` produces correct diffs and empty diff for unchanged fields; content fields cannot reach the diff helper (compile-time, by the helper's signature).
- `AuditEnabledImmutabilityTests` — PATCH cannot toggle the flag.

**Handler integration (in-memory `SafeExchangeDbContext` fixtures):**

- `SecretCreationAuditTests`
- `SecretMetadataUpdateAuditTests`
- `SecretDeletionAuditTests`
- `PermissionAuditTests`
- `ContentAuditTests`
- `AccessRequestAuditTests`

**Audit-read endpoint tests:**

- `AuditReadTests` — Read permission gating; `auditEnabled=false` path; time filters; continuation tokens; `?raw=true`.
- `RecycledNameTests` — create→delete→recreate same name; new audit is empty; old audit reachable only by direct partition query, not by name.

**Failure handling:**

- `AuditWriteFailureTests` — when `AuditWriter` throws (mocked), the user-facing operation still succeeds and returns 200; failure is logged.

**Retention purge:**

- `AuditPurgeTests` — expired anchors + their events deleted; un-expired left alone; live (non-deleted) anchors left alone.

**Migration:**

- `MigrationItem00011Tests` — backfills missing fields; idempotent; skipped on already-migrated docs.

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| Audit-write failure blocks user op. | Writer swallows its own exceptions and logs `AuditWriteFailed`. User op completes; gap is observable. |
| Concurrent appenders fight over `SequenceNumber`. | Cosmos `IfNoneMatch:*` on insert + bounded retry. Worst case: a record fails after 5 retries; gap is logged. |
| Hot-partition on a single very-active secret. | Per-secret read load is bounded by the human at the UI; per-secret write load is bounded by chunked uploads/downloads of the same secret. We accept the per-partition cap; if it bites, partition strategy can be revisited. |
| Audit data leaks more than the secret itself. | Invariants §1–§2 and the merge rule §API. DiffBuilder accepts only non-content inputs. Code review checks this in every PR that touches the writer. |
| Operator forgets to deploy ARM before code. | Spec §Deployment lists the order. Code that touches missing containers will surface a clear Cosmos error; we don't try to auto-create from the app. |
