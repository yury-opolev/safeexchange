# Phase 4: host-agnostic services; the six handlers become HTTP adapters (implementation plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Move the business logic of the six MCP-relevant handlers (`SafeExchangeSecretMeta`,
`SafeExchangeSecretContentMeta`, `SafeExchangeSecretStream`, `SafeExchangeContentCommit`, `SafeExchangeAccess`,
`SafeExchangeSecretAudit`) into host-agnostic services in `SafeExchange.Core\Services` (`SecretService`,
`ContentService`, `ChunkService`, `AccessService`, `AuditQueryService`) that take a `Caller` and return a
`ServiceResult`. The handlers become thin HTTP adapters. REST stays byte-identical: the 212 contract tests pass
unchanged after every task.

**Architecture:** Each handler entry point now does: `ICallerResolver.ResolveAsync` (replacing the
`GlobalFilters` + `SubjectHelper` preamble; a denial is written with `CallerDenial.ToResponseAsync`), the same
trigger log line, request parsing (body, headers, query), one service call, and
`ServiceResult.ToResponseAsync` (or `ToDownloadResponseAsync`). Services own everything else in today's order:
purge-if-needed, lookups, `KeepInStorage`, permission checks, DB work and audit writes. Handlers keep their old
constructors (which build the service themselves) next to a new DI constructor; DI registers services and
handlers as scoped, and the two trigger classes get their handlers from DI.

**Tech stack:** .NET 10, Azure Functions isolated worker (`HttpRequestData`), EF Core (InMemory in tests), NUnit 4,
Moq, `Microsoft.Extensions.DependencyInjection`.

**Master plan:** `docs/superpowers/plans/2026-11-21-mcp-endpoint.md` (ground rules, style, commands).
**Spec:** `docs/superpowers/specs/2026-11-21-mcp-endpoint-design.md` (§Services, §REST compatibility contract,
§MCP endpoint → Tools, §Testing item 3).
**Previous phase:** `docs/superpowers/plans/2026-11-21-mcp-p3-caller.md` (`Caller`, `ICallerResolver`, D2: "phase 4
replaces each preamble").

**Verified baseline:** this plan was applied task by task in a scratch worktree created from `wt/p1` at
**`1a3bccc`** (phase 3 done). Results:
- every red step failed as quoted below and every green step passed;
- the solution builds with 0 errors after every task; a `--no-incremental` build reports **705** warnings
  before and **704** after. The unique-warning sets differ only by one pre-existing warning that disappears
  with the old code (`SafeExchangeAccess.cs(469,33): warning CS8602`); there are no new warnings;
- the verification set (contract + architecture + service tests + `SecC`/`SecE`/`SecF`/`EffectivePermissions`)
  goes from **268** to **338** tests (212 contract + 2 architecture + 54 handler regression + 70 new), all green;
- every task commit builds with 0 errors and passes the phase filter with exactly the count its task gives
  (297, 309, 318, 325, 331, 336, 338), and the `git diff --cached --name-status` lists below are the real ones;
- phase 3's DB-free regression sets still pass: 66/66 and 69/69 (Task 8, Step 4);
- the Cosmos-backed handler fixtures (Task 8, Step 5; 136 tests) gave no evidence on that machine: the local
  `vnext-preview` emulator fails every one of them identically at `1a3bccc` and after this phase
  (`Unknown JsonNodeType`, an SDK/emulator mismatch). Run them where the emulator works (CI) before merging.

---

## Ground rules for this phase

1. **Run every command from the repository root** (the worktree that holds the phase 3 commit, `1a3bccc` when
   this plan was verified). A phase 3 review fix on top is fine. This plan depends only on these phase 3 members,
   which must exist with these shapes (adapt the two call sites in `GlobalFiltersCallerResolver` /
   `GlobalFilters` if a review renamed one):
   `ICallerResolver.ResolveAsync(ClaimsPrincipal, string channel, string? clientLabel, CancellationToken = default)`,
   `CallerResolution.IsAllowed` / `.Caller` / `.Denial`, `CallerChannels.Rest`,
   `CallerDenial.Forbidden(string error, string subStatus = "")`, `CallerResolver.ApplicationNotRegisteredError`,
   `new GlobalAccessPolicy(GloballyAllowedGroupsConfiguration, ITokenHelper, ILogger).EvaluateAsync(ClaimsPrincipal, SafeExchangeDbContext, CancellationToken = default)`,
   `CallerDenialHttpExtensions.ToResponseAsync(this CallerDenial, HttpRequestData)`.
2. **The phase 1 contract tests (`SafeExchange.Tests\Contract\*`) are frozen.** Do not edit them. They must pass
   unchanged after every task. `ContractTestBase.BuildHandlers` keeps compiling because the handlers keep their
   old constructors (D5).
3. **Public API of the handlers stays:** class names, namespaces, every `Run*` signature, the public static
   header-name fields of `SafeExchangeSecretStream`, and the old constructors. New constructors and types are
   additive.
4. **Style** (master plan): file header `/// <summary>\n/// TypeName\n/// </summary>`, block namespace, `using`
   directives inside the namespace, `this.` prefix, constructor null-guards with
   `?? throw new ArgumentNullException(nameof(x))`, 4-space indent, NUnit `Assert.That(..., Is....)`. Comments
   only where a line needs explaining. The test project has no implicit usings (every test file lists its
   usings); `SafeExchange.Core` and `SafeExchange.Core.Functions` have implicit usings enabled.
5. **One commit per task (Tasks 1–7) and a final docs commit (Task 8).** Stage the exact paths listed in each
   task (never `git add -A`), so this plan file is committed only in Task 8.
6. **No new build warnings.** Task 0 records the count, Task 8 compares.
7. **Replacing a handler file** means replacing its whole content with the code block given (the handler files
   have no byte-order mark; keep it that way). Files that do have one (`ActionResults.cs`, `GlobalFilters.cs`,
   `SafeExchangeStartup.cs`, `SafeSecret.cs`) are only edited in place; keep their BOM.
8. **File locks.** If a build fails with `MSB3027`/`MSB3021` ("file is locked by .NET Host"), a stale MSBuild
   node or `testhost` from an earlier run holds a `bin` DLL. Rebuild with `-nr:false` (no node reuse) after it
   exits; do not kill processes you did not start.

## Decisions taken in this phase

### D1: `ServiceResult` shape

`SafeExchange.Core\Services\ServiceResult.cs` holds two sealed classes:

- `ServiceResult` (no value): `Status` (`ServiceStatus`), `StatusCode` (`System.Net.HttpStatusCode`), `Error`,
  `SubStatus` (default `""`), `Detail` (optional object written as `result` on failure), `IsOk`. Factories:
  `Ok()`, `Failure(status, statusCode, error, subStatus = "", detail = null)`, and the shortcuts `NotFound`
  (404), `Forbidden` (403), `InsufficientPermissions(permission, secretId, subStatus = "")` (403, today's exact
  message), `Unauthorized` (401), `Unprocessable` (422), `BadRequest` (400), `Conflict` (409).
- `ServiceResult<T>`: an `Outcome` (`ServiceResult`) plus `Value`; `Ok(T value)`; a failure converts
  implicitly (`return ServiceResult.NotFound(...)` in a method returning `ServiceResult<T>`); converting `Ok()`
  throws (an ok result needs a value).

`ServiceStatus` has one member per status string the six handlers emit: `Ok`, `NotFound`, `Forbidden`,
`Unauthorized`, `Unprocessable`, `BadRequest`, `Conflict`, `Error`, `ChunkHashMismatch`, `HashMismatch`,
`NoUploadState`, `InconsistentUploadMode`; `ServiceStatusNames.ToStatusString()` gives the wire string (MCP
reports the same string in phase 6).

Why the HTTP code travels with the result: the status string does not determine it. `error` is 400 for
invalid access input but 500 for internal errors; `unprocessable`, `chunk_hash_mismatch`, `hash_mismatch` and
`no_upload_state` are 422; `inconsistent_upload_mode` is 400. `HttpStatusCode` is a BCL type and phase 3's
`CallerDenial` already carries it, so Core stays free of Functions types (the architecture test still
passes). `Detail` exists because two failures carry a `result` today (`ChunkHashMismatch`).

Non-JSON results: a value-less success is always `200 {"status":"ok","subStatus":"","result":"ok"}` in these
six handlers (delete secret, delete content, grant, revoke, patch). Downloads return
`ServiceResult<ContentDownload>`: content type, length, optional `Content-Disposition` and a deferred
`WriteToAsync(Stream)` (D7). No member for `204`: the old `filterResult ?? request.CreateResponse(NoContent)`
branch was unreachable (phase 3, D2) and disappears with the preamble.

The adapter side is `SafeExchange.Core.Functions\Utilities\ServiceResultHttpExtensions.cs` (namespace
`SafeExchange.Core.Services`, like `CallerDenialHttpExtensions` sits in `SafeExchange.Core.Callers`). A failure
is written as `BaseResponseObject<object> { Status, SubStatus, Error, Result = Detail }` through
`ActionResults.CreateResponseAsync`. That is byte-identical to the old `BaseResponseObject<object>` and
`BaseResponseObject<ChunkHashMismatch>` bodies: same envelope class, same property order, `object`-typed
`Result` is serialized by runtime type, and `null` is omitted (pinned by `ServiceResultHttpExtensionsTests`
and by the contract tests).

### D2: Exceptions keep today's scopes

Today each handler body runs inside `ActionResults.TryCatchAsync` (500
`{"status":"error","subStatus":"internal_exception","error":"Internal error. Reference: <id>"}` + one
`LogError("Exception in {ActionName}, correlationId={CorrelationId}")`), but some steps run before it and
their exceptions propagate to the Functions host: the preamble (caller resolution), purge-if-needed, and in
`SafeExchangeAccess` the secret lookup plus the grant/list/revoke permission checks.

Chosen: **services throw, and mark which part threw.** `ServiceOperation.RunAsync(...)` wraps the part of a
service method that ran inside `TryCatchAsync` and turns any exception there into a
`ServiceOperationException` (inner = original). Steps that ran outside (purge-if-needed; the access lookup and
checks) run before `ServiceOperation.RunAsync` and throw unwrapped. Adapters call
`ActionResults.ServiceCallAsync(request, call, actionName, log)`: a `ServiceOperationException` becomes exactly
the old 500 response (same helper, now `InternalErrorAsync`, shared with `TryCatchAsync`; it logs the inner
exception, with today's action name, on the trigger's `ILogger`); any other exception propagates as before.
Action names are kept verbatim, including the copy-paste ones (`HandleSecretContentMetaDeletion` for content
update and drop, `HandleSecretStreamDownload` for `/all`). The deferred download writers are wrapped too, so a
storage failure while streaming is still the 500 envelope.

Rejected: (a) adapters wrap the whole service call in `TryCatchAsync` — simpler, but purge and access-lookup
failures would turn from host 500s into the JSON envelope; (b) services catching and returning an error result
— the action names and the trigger logger are REST concerns.

Residual, accepted: request bodies are read and parsed in the adapter before the service call, and the
response is written after it, outside the operation part. These cannot fail in practice (the worker buffers
the body; JSON errors are caught and become `null` / `ContentCommitInput.Malformed`; responses are plain DTOs).

### D3: Purge-if-needed belongs to the services

`purger.PurgeIfNeededAsync(...)` is security-relevant (an expired secret must not be readable through MCP
either), so it is the first step of every service method that purged before (`SecretService` get / create /
update / delete, all `ContentService` methods incl. commit, all `ChunkService` methods). It still runs after
caller resolution and the trigger log line, as today, because the adapter does those before calling the
service. The list endpoints, `AccessService` and `AuditQueryService` never purged and still do not.

Accepted change on an unreachable path: the "Request method not recognized" branch answers 400 directly, so it
no longer purges first (SecretMeta, ContentMeta, Stream) and `SafeExchangeAccess` no longer looks the secret up
first (a missing secret with an unknown method gave 404 before). Every `[HttpTrigger]` in `SafeSecret` and
`SafeAccess` lists only methods that its handler entry point handles (e.g. `SafeExchange-SecretMeta`:
`post, get, patch, delete`; `SafeExchange-Access`: `post, get, delete, patch`), so the host never routes another
method here. The contract tests that call `Run` with `put` on an existing secret still get the identical 400.

### D4: Caller resolution in the adapters

Each entry point starts with:

```csharp
var resolution = await this.callerResolver.ResolveAsync(principal, CallerChannels.Rest, null);
if (!resolution.IsAllowed)
{
    return await resolution.Denial.ToResponseAsync(request);
}

var caller = resolution.Caller;
log.LogInformation(/* today's trigger line, with caller.SubjectType */);
```

The order (global gate, subject, registered-app gate, then the log line) is today's. The denial bodies are
pinned by `GlobalFilterContractTests` on every entry point.

### D5: Constructors and DI (keep the old constructors)

Every handler gets a DI constructor `(ICallerResolver callerResolver, <X>Service service)` and **keeps** its old
public constructor, which now builds the service itself (with `NullLogger<TService>`) and a
`GlobalFiltersCallerResolver`. Reasons:

- About 40 handler constructions in 17 test fixtures, the frozen `ContractTestBase` and any consumer that builds handlers by hand compile and
  behave unchanged; the switch to DI happens only in the triggers.
- `GlobalFiltersCallerResolver` (internal, `SafeExchange.Core.Functions\Requests\Filters`) reproduces the old
  preamble exactly: the global gate through `GlobalFilters` (its options monitor, token helper and
  `ILogger<GlobalFilters>` category, via the new `GlobalFilters.GetGlobalAccessDenialAsync`), then
  `SubjectHelper` with the handler's own token helper, then the registered-app gate. The default
  `CallerResolver` could not: it takes one token helper and logs under its own category. Its `DisplayName` is
  the subject id, because the old preamble never called `GetDisplayName` and REST does not use it.

DI: `SafeExchangeStartup.ConfigureServices` registers the five services as scoped (they need the scoped
`SafeExchangeDbContext`). `SafeExchangeFunctionsStartup.ConfigureFunctionsServices` registers the six handlers
as scoped **through factories** that call the DI constructor: Microsoft DI picks the longest satisfiable
constructor, and in the app the old 7–8 parameter constructors are satisfiable too. `SafeSecret` and
`SafeAccess` take their handlers by constructor (the isolated worker activates function classes from the
invocation scope).

Rejected: dropping the old constructors (churn in about 40 test constructions and in the frozen contract base; consumers break
twice) and `[ActivatorUtilitiesConstructor]` (honoured by `ActivatorUtilities`, not by the container's own
constructor selection).

### D6: Logging

Service log lines keep their text but are written under the service's category
(`SafeExchange.Core.Services.<X>Service`) instead of the trigger's; the audit writer gets the service logger.
The trigger log line, the body-parse failure lines and the 500 `LogError` stay in the adapter on the trigger's
`ILogger`. Two effects, both outside the REST contract: an unreadable create/update body now logs the adapter's
"Could not parse input data …" line and the service's "… not provided." line; handlers built with the old
constructors log nothing from the service (`NullLogger`). Logs are not part of the REST contract (phase 3 risk
note).

### D7: Service APIs (what MCP gets)

- **Inputs are typed DTOs.** `null` means "missing or unreadable", which REST answers identically today (parse
  failure and `null` give the same error, at the same point in the check order). Commit is the exception: an
  unreadable body is answered with `"Body is not valid JSON."` after the lock and upload-state checks, so it is
  passed as `ContentCommitInput.Malformed`.
- **`SecretService`**: `ListAsync(caller, tags)` (v3 shape, `SecretListItemOutput` with effective
  permissions — what `secret_list` uses), `ListV2Async(caller, tags)`, `CreateAsync(caller, secretId,
  MetadataCreationInput?)`, `GetAsync(caller, secretId)` (`ObjectMetadataOutput` incl. content items and chunk
  hashes — what `secret_get` / `file_download_chunk` use for totals and hashes), `UpdateAsync(caller, secretId,
  MetadataUpdateInput?)`, `DeleteAsync(caller, secretId)`.
- **`ContentService`**: `CreateAsync(caller, secretId, contentId, input)` (`contentId` must be empty; REST
  passes its route value, MCP passes `""`), `UpdateAsync`, `DropAsync`, `DeleteAsync`, and
  **`CommitAsync(caller, secretId, contentId, accessTicket, ContentCommitInput)`**. Deviation from the spec
  table ("`ChunkService` (+ commit)"): commit is a content state change that needs no blob storage, and the old
  `SafeExchangeContentCommit` constructor has no `IBlobHelper`, so it can build a `ContentService` but not a
  `ChunkService`. Phase 6's `file_upload_complete` calls `ContentService.CommitAsync`.
- **`ChunkService`**: `UploadChunkAsync(caller, secretId, contentId, Stream data, ChunkUploadRequest)` with
  `ChunkId`, `AccessTicket`, `OperationType`, `ChunkHash`, `DeclaredLength` (REST: `Content-Length`, used as the
  hashed chunk's length) and `ExpectedIndex`; `DownloadChunkAsync(caller, secretId, contentId, chunkId)`;
  `DownloadContentAsync(caller, secretId, contentId)`. Chunk ids are `{contentName}-{index:00000000}`.
  `HashingReadStream`, `SerializableSha256`, `UploadModeResolver` and the HTML sanitizer policy move unchanged
  (the policy into `MainContentSanitizer`).
- **`ExpectedIndex`** (REST passes `null`, so REST is unchanged). Checked after the lock check and **before**
  the lock is taken, so a rejected call has no side effects: if a chunk is stored at that index and its hash
  equals `ChunkHash` (case-insensitive) → `ok` with that chunk (`ChunkName`, `Hash`, `Length`, the caller's
  `AccessTicket`), nothing written; otherwise, if the chunk count is not `ExpectedIndex` → `unprocessable` 422
  `"Expected chunk index {count}."` (the index the server expects next, so a client can resume).
- **Downloads are deferred.** `DownloadChunkAsync` / `DownloadContentAsync` run all checks and return the
  headers; the blob read, the copy, `LastAccessedAt` and the `ContentRead` audit events happen in
  `ContentDownload.WriteToAsync`, in today's order (the adapter writes the headers first, exactly as before).
  MCP writes into a `MemoryStream`.
- **`AccessService`**: `GrantAsync(caller, secretId, IReadOnlyList<SubjectPermissionsInput>?)`,
  `ListAsync(caller, secretId)` (v3 `AccessListOutput` — what `access_list` uses), `ListV2Async`,
  `RevokeAsync`, `PatchAsync(caller, secretId, AccessUpdateInput?)` (what `access_change` uses; same rules as
  REST `PATCH`, incl. the consent subStatus and the orphan rule).
- **`AuditQueryService`**: `ReadAsync(caller, secretId, AuditPageQuery)` and the static
  `BuildAuditPageAsync(dbContext, auditInstanceId, AuditPageQuery)`. `AuditPageQuery.Parse(queryString)` keeps
  the REST query parsing; MCP sets `From`/`To`/`PageSize`/`Continuation`/`Descending` directly.
  `SafeExchangeSecretAudit.BuildAuditPageAsync(dbContext, auditInstanceId, queryString)` stays as an internal
  forwarder, so `SafeExchangeAdminSecretAudit` is untouched.
- **Shared helpers:** `AccessTicketManager` (internal; the expiry-aware `TryGetAccessTicketAsync` that both
  `ContentService` and `ChunkService` duplicated, plus `NewTicket()`), `MainContentSanitizer` (internal).
- Every method takes `Caller` first. `ArgumentNullException` for a null caller/input object is thrown before
  the operation part.

### D8: Permission tests (spec, Testing item 3)

Each service has a fixture in `SafeExchange.Tests\Services` that calls every method that checks a permission
with a caller lacking it and asserts the exact result: `Forbidden`, 403,
`"Insufficient permissions to do '<Permission>' action on '<secretId>'"`, `SubStatus` `""` (or
`consent_required` for an access denial of a user who must consent). Methods without a per-secret permission
check, as today: `SecretService.CreateAsync` and the two `SecretService` list methods (they return only what
the caller can read, which is tested). Caller-level denials (disabled / unregistered app,
not in an allowed group) are `ICallerResolver`'s and already covered by phase 3 and the contract tests.

### D9: Test filter

All new tests live in `SafeExchange.Tests\Services` (namespace `SafeExchange.Tests.Services`), so the phase
filter is:

```powershell
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchange.Tests.Contract|FullyQualifiedName~SafeExchange.Tests.Architecture|FullyQualifiedName~SafeExchange.Tests.Services|FullyQualifiedName~SecC|FullyQualifiedName~SecE|FullyQualifiedName~SecF|FullyQualifiedName~EffectivePermissions"
```

(`SecC`/`SecE`/`SecF`/`EffectivePermissions` are existing EF InMemory fixtures that build the handlers with
the old constructors.) Below, "the phase filter" means this command.

## File map

Create (Core, namespace `SafeExchange.Core.Services`):
- `SafeExchange.Core\Services\ServiceStatus.cs`, `ServiceStatusNames.cs`, `ServiceResult.cs`,
  `ServiceOperation.cs`, `ServiceOperationException.cs`, `ContentDownload.cs` — result model (Task 1).
- `SafeExchange.Core\Services\SecretService.cs` (Task 2).
- `SafeExchange.Core\Services\AccessTicketManager.cs`, `ContentCommitInput.cs`, `ContentService.cs` (Task 3).
- `SafeExchange.Core\Services\ChunkUploadRequest.cs`, `MainContentSanitizer.cs`, `ChunkService.cs` (Task 4).
- `SafeExchange.Core\Services\AccessService.cs` (Task 5).
- `SafeExchange.Core\Services\AuditPageQuery.cs`, `AuditQueryService.cs` (Task 6).

Create (Functions library):
- `SafeExchange.Core.Functions\Utilities\ServiceResultHttpExtensions.cs` (Task 1).
- `SafeExchange.Core.Functions\Requests\Filters\GlobalFiltersCallerResolver.cs` (Task 2).

Create (tests, namespace `SafeExchange.Tests.Services`):
- `ServiceResultTests.cs`, `ServiceResultHttpExtensionsTests.cs` (Task 1); `ServiceTestBase.cs`,
  `SecretServiceTests.cs` (Task 2); `ContentServiceTests.cs` (Task 3); `ChunkServiceTests.cs` (Task 4);
  `AccessServiceTests.cs` (Task 5); `AuditQueryServiceTests.cs` (Task 6); `ServiceRegistrationTests.cs`
  (Task 7) — all under `SafeExchange.Tests\Services\`.

Modify:
- `SafeExchange.Core.Functions\Utilities\ActionResults.cs` — `ServiceCallAsync`, shared `InternalErrorAsync` (Task 1).
- `SafeExchange.Core.Functions\Requests\Filters\GlobalFilters.cs` — `GetGlobalAccessDenialAsync` (Task 2).
- `SafeExchange.Core.Functions\Functions\SafeExchangeSecretMeta.cs` (Task 2),
  `SafeExchangeSecretContentMeta.cs` + `SafeExchangeContentCommit.cs` (Task 3), `SafeExchangeSecretStream.cs`
  (Task 4), `SafeExchangeAccess.cs` (Task 5), `SafeExchangeSecretAudit.cs` (Task 6) — replaced by adapters.
- `SafeExchange.Core\SafeExchangeStartup.cs`, `SafeExchange.Core.Functions\SafeExchangeFunctionsStartup.cs`,
  `SafeExchange.Functions\Functions\SafeSecret.cs`, `SafeExchange.Functions\Functions\SafeAccess.cs`,
  `SafeExchange.Tests\Tests\SafeExchangeFunctionsStartupTests.cs` (Task 7).
- `docs\architecture.md` (Task 8).

---

### Task 0: Record the baseline

**Files:** none.

- [ ] **Step 1: Confirm the starting commit and a clean tree**

Run: `git --no-pager log --oneline -1; git status --short`
Expected: `1a3bccc feat(core): Caller abstraction and host-agnostic caller resolution` or a later phase 3 review
commit, and no status lines other than this plan file
(`?? docs/superpowers/plans/2026-11-21-mcp-p4-services.md`).

- [ ] **Step 2: Full rebuild and note the warning count**

Run: `dotnet build SafeExchange.slnx --nologo -v q --no-incremental`
Expected: the last lines are `705 Warning(s)` and `0 Error(s)`. Write the number down (if a review fix changed
it, use the new number; Task 8 expects it minus one).

- [ ] **Step 3: Run the phase filter (D9)**

Expected: `Passed!  - Failed:     0, Passed:   268, Skipped:     0, Total:   268` (212 contract + 2 architecture
+ 54 handler regression tests). If a review fix added tests, use the new number as the base wherever this
plan gives a count.

- [ ] **Step 4 (optional, needs the Cosmos emulator): record the Cosmos handler fixtures**

Run the command of Task 8, Step 5 and write down the result. Task 8 must give the same result. (On the
machine this plan was verified on, the local `vnext-preview` emulator made every Cosmos fixture fail already at
`1a3bccc` with `System.ArgumentOutOfRangeException : ... (Parameter 'Unknown JsonNodeType: Unknown.')` from the
Cosmos SDK's binary JSON reader, so this check could not add evidence there; in CI they pass.)

---

### Task 1: `ServiceResult`, `ServiceOperation` and the REST mapping

**Files:**
- Create: `SafeExchange.Tests\Services\ServiceResultTests.cs`
- Create: `SafeExchange.Tests\Services\ServiceResultHttpExtensionsTests.cs`
- Create: `SafeExchange.Core\Services\ServiceStatus.cs`, `ServiceStatusNames.cs`, `ServiceResult.cs`,
  `ServiceOperationException.cs`, `ServiceOperation.cs`, `ContentDownload.cs`
- Create: `SafeExchange.Core.Functions\Utilities\ServiceResultHttpExtensions.cs`
- Modify: `SafeExchange.Core.Functions\Utilities\ActionResults.cs`

- [ ] **Step 1: Write the failing tests**

Create the folder `SafeExchange.Tests\Services` and `SafeExchange.Tests\Services\ServiceResultTests.cs`:

```csharp
/// <summary>
/// ServiceResultTests
/// </summary>

namespace SafeExchange.Tests.Services
{
    using NUnit.Framework;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Services;
    using System;
    using System.Net;
    using System.Threading.Tasks;

    [TestFixture]
    public class ServiceResultTests
    {
        [TestCase(ServiceStatus.Ok, "ok")]
        [TestCase(ServiceStatus.NotFound, "not_found")]
        [TestCase(ServiceStatus.Forbidden, "forbidden")]
        [TestCase(ServiceStatus.Unauthorized, "unauthorized")]
        [TestCase(ServiceStatus.Unprocessable, "unprocessable")]
        [TestCase(ServiceStatus.BadRequest, "bad_request")]
        [TestCase(ServiceStatus.Conflict, "conflict")]
        [TestCase(ServiceStatus.Error, "error")]
        [TestCase(ServiceStatus.ChunkHashMismatch, "chunk_hash_mismatch")]
        [TestCase(ServiceStatus.HashMismatch, "hash_mismatch")]
        [TestCase(ServiceStatus.NoUploadState, "no_upload_state")]
        [TestCase(ServiceStatus.InconsistentUploadMode, "inconsistent_upload_mode")]
        public void ToStatusString_IsTheRestStatusString(ServiceStatus status, string expected)
        {
            Assert.That(status.ToStatusString(), Is.EqualTo(expected));
        }

        [Test]
        public void Shortcuts_CarryTodaysStatusCodes()
        {
            AssertFailure(ServiceResult.NotFound("n"), ServiceStatus.NotFound, HttpStatusCode.NotFound, "n", string.Empty);
            AssertFailure(ServiceResult.Forbidden("f", "consent_required"), ServiceStatus.Forbidden, HttpStatusCode.Forbidden, "f", "consent_required");
            AssertFailure(ServiceResult.Unauthorized("u"), ServiceStatus.Unauthorized, HttpStatusCode.Unauthorized, "u", string.Empty);
            AssertFailure(ServiceResult.Unprocessable("p"), ServiceStatus.Unprocessable, HttpStatusCode.UnprocessableEntity, "p", string.Empty);
            AssertFailure(ServiceResult.BadRequest("b"), ServiceStatus.BadRequest, HttpStatusCode.BadRequest, "b", string.Empty);
            AssertFailure(ServiceResult.Conflict("c"), ServiceStatus.Conflict, HttpStatusCode.Conflict, "c", string.Empty);
        }

        [Test]
        public void InsufficientPermissions_UsesTodaysMessage()
        {
            var result = ServiceResult.InsufficientPermissions(PermissionType.GrantAccess, "alpha", "consent_required");

            AssertFailure(result, ServiceStatus.Forbidden, HttpStatusCode.Forbidden, "Insufficient permissions to do 'GrantAccess' action on 'alpha'", "consent_required");
        }

        [Test]
        public void Failure_KeepsDetail_AndRejectsOk()
        {
            var detail = new ChunkHashMismatch { Expected = "a", Actual = "b" };

            var result = ServiceResult.Failure(ServiceStatus.ChunkHashMismatch, HttpStatusCode.UnprocessableEntity, "m", detail: detail);

            Assert.That(result.Detail, Is.SameAs(detail));
            Assert.Throws<ArgumentException>(() => ServiceResult.Failure(ServiceStatus.Ok, HttpStatusCode.OK, "x"));
        }

        [Test]
        public void GenericOk_CarriesValue()
        {
            var result = ServiceResult<string>.Ok("value");

            Assert.That(result.IsOk, Is.True);
            Assert.That(result.Value, Is.EqualTo("value"));
            Assert.That(result.Status, Is.EqualTo(ServiceStatus.Ok));
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public void Failure_ConvertsToGenericResult_ButOkDoesNot()
        {
            ServiceResult<string> converted = ServiceResult.NotFound("missing");

            Assert.That(converted.IsOk, Is.False);
            Assert.That(converted.Value, Is.Null);
            Assert.That(converted.Error, Is.EqualTo("missing"));
            Assert.Throws<InvalidOperationException>(() => { ServiceResult<string> _ = ServiceResult.Ok(); });
        }

        [Test]
        public void ServiceOperation_WrapsExceptions_Once()
        {
            var inner = new InvalidOperationException("boom");

            var wrapped = Assert.ThrowsAsync<ServiceOperationException>(() => ServiceOperation.RunAsync<int>(() => throw inner));
            Assert.That(wrapped!.InnerException, Is.SameAs(inner));

            var rethrown = Assert.ThrowsAsync<ServiceOperationException>(() => ServiceOperation.RunAsync(() => throw wrapped));
            Assert.That(rethrown, Is.SameAs(wrapped));
        }

        [Test]
        public async Task ServiceOperation_ReturnsTheOperationResult()
        {
            Assert.That(await ServiceOperation.RunAsync(() => Task.FromResult(42)), Is.EqualTo(42));
        }

        private static void AssertFailure(ServiceResult result, ServiceStatus status, HttpStatusCode statusCode, string error, string subStatus)
        {
            Assert.That(result.IsOk, Is.False);
            Assert.That(result.Status, Is.EqualTo(status));
            Assert.That(result.StatusCode, Is.EqualTo(statusCode));
            Assert.That(result.Error, Is.EqualTo(error));
            Assert.That(result.SubStatus, Is.EqualTo(subStatus));
            Assert.That(result.Detail, Is.Null);
        }
    }
}
```

Create `SafeExchange.Tests\Services\ServiceResultHttpExtensionsTests.cs`. It derives from `ContractTestBase` for
the production serializer settings and compares bytes with what `ActionResults` writes:

```csharp
/// <summary>
/// ServiceResultHttpExtensionsTests
/// </summary>

namespace SafeExchange.Tests.Services
{
    using Microsoft.Azure.Functions.Worker.Http;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Services;
    using SafeExchange.Tests.Contract;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Uses the contract fixture plumbing (production serializer settings) so the bytes compare with what
    /// the handlers wrote before they became adapters.
    /// </summary>
    [TestFixture]
    public class ServiceResultHttpExtensionsTests : ContractTestBase
    {
        [Test]
        public async Task Failure_IsByteIdenticalToTheHandlersJson()
        {
            var expected = await ActionResults.CreateResponseAsync(
                Request("get"), HttpStatusCode.NotFound,
                new BaseResponseObject<object> { Status = "not_found", Error = "Secret 'alpha' does not exist." });

            var actual = await ServiceResult.NotFound("Secret 'alpha' does not exist.").ToResponseAsync(Request("get"));

            AssertSameResponse(actual, expected);
        }

        [Test]
        public async Task InsufficientPermissions_IsByteIdenticalToActionResults()
        {
            var expected = await ActionResults.CreateResponseAsync(
                Request("post"), HttpStatusCode.Forbidden,
                ActionResults.InsufficientPermissions(PermissionType.GrantAccess, "alpha", "consent_required"));

            ServiceResult<string> failure = ServiceResult.InsufficientPermissions(PermissionType.GrantAccess, "alpha", "consent_required");
            var actual = await failure.ToResponseAsync(Request("post"));

            AssertSameResponse(actual, expected);
        }

        [Test]
        public async Task FailureWithDetail_IsByteIdenticalToTheTypedResultObject()
        {
            var mismatch = new ChunkHashMismatch { Expected = "aa", Actual = "bb" };
            var expected = await ActionResults.CreateResponseAsync(
                Request("post"), HttpStatusCode.UnprocessableEntity,
                new BaseResponseObject<ChunkHashMismatch> { Status = "chunk_hash_mismatch", Error = "Received chunk bytes do not match the client-asserted hash.", Result = mismatch });

            var actual = await ServiceResult.Failure(
                ServiceStatus.ChunkHashMismatch, HttpStatusCode.UnprocessableEntity,
                "Received chunk bytes do not match the client-asserted hash.", detail: mismatch).ToResponseAsync(Request("post"));

            AssertSameResponse(actual, expected);
        }

        [Test]
        public async Task Ok_WithoutValue_WritesResultOk()
        {
            var response = await ServiceResult.Ok().ToResponseAsync(Request("delete"));

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":"ok"}
                """);
        }

        [Test]
        public async Task Ok_WithValue_IsByteIdenticalToTheTypedResultObject()
        {
            var value = new List<string> { "a", "b" };
            var expected = await ActionResults.CreateResponseAsync(
                Request("get"), HttpStatusCode.OK, new BaseResponseObject<List<string>> { Status = "ok", Result = value });

            var actual = await ServiceResult<List<string>>.Ok(value).ToResponseAsync(Request("get"));

            AssertSameResponse(actual, expected);
        }

        [Test]
        public async Task Download_WritesHeadersInOrderThenTheBytes()
        {
            var bytes = Encoding.UTF8.GetBytes("hello");
            var download = new ContentDownload("text/plain", bytes.Length, "attachment; filename=a.txt", (stream, ct) => stream.WriteAsync(bytes, 0, bytes.Length, ct));

            var response = await ServiceResult<ContentDownload>.Ok(download).ToDownloadResponseAsync(Request("get"));

            ContractAssert.Bytes(response, HttpStatusCode.OK, bytes,
                "Content-Type: text/plain", "Content-Length: 5", "Content-Disposition: attachment; filename=a.txt");
        }

        [Test]
        public async Task Download_WithoutDisposition_HasNoDispositionHeader()
        {
            var download = new ContentDownload("text/plain", 0, null, (stream, ct) => Task.CompletedTask);

            var response = await ServiceResult<ContentDownload>.Ok(download).ToDownloadResponseAsync(Request("get"));

            ContractAssert.Bytes(response, HttpStatusCode.OK, Array.Empty<byte>(), "Content-Type: text/plain", "Content-Length: 0");
        }

        [Test]
        public async Task Download_Failure_IsWrittenAsJson()
        {
            ServiceResult<ContentDownload> failure = ServiceResult.Unprocessable("Content 'c' is not ready.");

            var response = await failure.ToDownloadResponseAsync(Request("get"));

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Content 'c' is not ready."}
                """);
        }

        [Test]
        public async Task ServiceCallAsync_TurnsOperationFailuresIntoTheInternalErrorResponse()
        {
            var response = await ActionResults.ServiceCallAsync(
                Request("get"),
                () => throw new ServiceOperationException(new InvalidOperationException("boom")),
                "HandleSomething",
                this.Logger);

            ContractAssert.Json(response, HttpStatusCode.InternalServerError, """
                {"status":"error","subStatus":"internal_exception","error":"Internal error. Reference: <correlationId>"}
                """);
        }

        [Test]
        public void ServiceCallAsync_LetsOtherExceptionsPropagate()
        {
            var thrown = new InvalidOperationException("outside the operation");

            var actual = Assert.ThrowsAsync<InvalidOperationException>(() => ActionResults.ServiceCallAsync(
                Request("get"), () => throw thrown, "HandleSomething", this.Logger));

            Assert.That(actual, Is.SameAs(thrown));
        }

        private static void AssertSameResponse(HttpResponseData actual, HttpResponseData expected)
        {
            Assert.That(actual.StatusCode, Is.EqualTo(expected.StatusCode));
            Assert.That(ContractAssert.HeaderLines(actual), Is.EqualTo(ContractAssert.HeaderLines(expected)));
            Assert.That(ContractAssert.ReadBodyBytes(actual), Is.EqualTo(ContractAssert.ReadBodyBytes(expected)));
        }
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet build SafeExchange.slnx --nologo -v q`
Expected: FAIL, e.g. `error CS0234: The type or namespace name 'Services' does not exist in the namespace
'SafeExchange.Core' (are you missing an assembly reference?)` in both new test files.

- [ ] **Step 3: Add the result model to Core**

Create `SafeExchange.Core\Services\ServiceStatus.cs`:

```csharp
/// <summary>
/// ServiceStatus
/// </summary>

namespace SafeExchange.Core.Services
{
    /// <summary>
    /// Outcome of a service call: one member per <c>status</c> string the REST handlers emit.
    /// <see cref="ServiceStatusNames.ToStatusString"/> gives the wire string.
    /// </summary>
    public enum ServiceStatus
    {
        Ok,
        NotFound,
        Forbidden,
        Unauthorized,
        Unprocessable,
        BadRequest,
        Conflict,
        Error,
        ChunkHashMismatch,
        HashMismatch,
        NoUploadState,
        InconsistentUploadMode,
    }
}
```

Create `SafeExchange.Core\Services\ServiceStatusNames.cs`:

```csharp
/// <summary>
/// ServiceStatusNames
/// </summary>

namespace SafeExchange.Core.Services
{
    using System;

    public static class ServiceStatusNames
    {
        /// <summary>
        /// The <c>status</c> string REST writes (and MCP reports) for <paramref name="status"/>.
        /// </summary>
        public static string ToStatusString(this ServiceStatus status) => status switch
        {
            ServiceStatus.Ok => "ok",
            ServiceStatus.NotFound => "not_found",
            ServiceStatus.Forbidden => "forbidden",
            ServiceStatus.Unauthorized => "unauthorized",
            ServiceStatus.Unprocessable => "unprocessable",
            ServiceStatus.BadRequest => "bad_request",
            ServiceStatus.Conflict => "conflict",
            ServiceStatus.Error => "error",
            ServiceStatus.ChunkHashMismatch => "chunk_hash_mismatch",
            ServiceStatus.HashMismatch => "hash_mismatch",
            ServiceStatus.NoUploadState => "no_upload_state",
            ServiceStatus.InconsistentUploadMode => "inconsistent_upload_mode",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown service status."),
        };
    }
}
```

Create `SafeExchange.Core\Services\ServiceResult.cs`:

```csharp
/// <summary>
/// ServiceResult
/// </summary>

namespace SafeExchange.Core.Services
{
    using SafeExchange.Core.Permissions;
    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.Net;

    /// <summary>
    /// Result of a service call without a value. A failure carries everything REST writes today: the
    /// HTTP status code (the same <see cref="ServiceStatus"/> maps to different codes in places, e.g.
    /// <c>error</c> is 400 for invalid access input), the <c>status</c>, <c>subStatus</c> and <c>error</c>
    /// strings, and an optional <see cref="Detail"/> written as <c>result</c> (e.g. a hash mismatch).
    /// </summary>
    public sealed class ServiceResult
    {
        private static readonly ServiceResult OkResult = new(ServiceStatus.Ok, HttpStatusCode.OK, null, string.Empty, null);

        private ServiceResult(ServiceStatus status, HttpStatusCode statusCode, string? error, string subStatus, object? detail)
        {
            this.Status = status;
            this.StatusCode = statusCode;
            this.Error = error;
            this.SubStatus = subStatus;
            this.Detail = detail;
        }

        public ServiceStatus Status { get; }

        public HttpStatusCode StatusCode { get; }

        public string? Error { get; }

        public string SubStatus { get; }

        public object? Detail { get; }

        [MemberNotNullWhen(false, nameof(Error))]
        public bool IsOk => this.Status == ServiceStatus.Ok;

        public static ServiceResult Ok() => OkResult;

        public static ServiceResult Failure(ServiceStatus status, HttpStatusCode statusCode, string error, string subStatus = "", object? detail = null)
        {
            if (status == ServiceStatus.Ok)
            {
                throw new ArgumentException("A failure cannot have the Ok status.", nameof(status));
            }

            return new ServiceResult(
                status,
                statusCode,
                error ?? throw new ArgumentNullException(nameof(error)),
                subStatus ?? throw new ArgumentNullException(nameof(subStatus)),
                detail);
        }

        public static ServiceResult NotFound(string error) => Failure(ServiceStatus.NotFound, HttpStatusCode.NotFound, error);

        public static ServiceResult Forbidden(string error, string subStatus = "") => Failure(ServiceStatus.Forbidden, HttpStatusCode.Forbidden, error, subStatus);

        public static ServiceResult InsufficientPermissions(PermissionType permission, string secretId, string subStatus = "")
            => Forbidden($"Insufficient permissions to do '{permission}' action on '{secretId}'", subStatus);

        public static ServiceResult Unauthorized(string error) => Failure(ServiceStatus.Unauthorized, HttpStatusCode.Unauthorized, error);

        public static ServiceResult Unprocessable(string error) => Failure(ServiceStatus.Unprocessable, HttpStatusCode.UnprocessableEntity, error);

        public static ServiceResult BadRequest(string error) => Failure(ServiceStatus.BadRequest, HttpStatusCode.BadRequest, error);

        public static ServiceResult Conflict(string error) => Failure(ServiceStatus.Conflict, HttpStatusCode.Conflict, error);
    }

    /// <summary>
    /// Result of a service call that returns a <typeparamref name="T"/> on success. Failures are written as
    /// <see cref="ServiceResult"/> and convert implicitly, so a method returning <c>ServiceResult&lt;T&gt;</c>
    /// can <c>return ServiceResult.NotFound(...)</c>.
    /// </summary>
    public sealed class ServiceResult<T>
    {
        private ServiceResult(ServiceResult outcome, T? value)
        {
            this.Outcome = outcome;
            this.Value = value;
        }

        /// <summary>
        /// The status part of the result (the failure itself for a failed call).
        /// </summary>
        public ServiceResult Outcome { get; }

        public T? Value { get; }

        public ServiceStatus Status => this.Outcome.Status;

        public HttpStatusCode StatusCode => this.Outcome.StatusCode;

        public string? Error => this.Outcome.Error;

        public string SubStatus => this.Outcome.SubStatus;

        public object? Detail => this.Outcome.Detail;

        [MemberNotNullWhen(true, nameof(Value))]
        public bool IsOk => this.Outcome.IsOk;

        public static ServiceResult<T> Ok(T value)
            => new(ServiceResult.Ok(), value ?? throw new ArgumentNullException(nameof(value)));

        public static implicit operator ServiceResult<T>(ServiceResult failure)
        {
            if (failure is null)
            {
                throw new ArgumentNullException(nameof(failure));
            }

            if (failure.IsOk)
            {
                throw new InvalidOperationException($"An ok result needs a value; use {nameof(ServiceResult)}<T>.{nameof(Ok)}(value).");
            }

            return new ServiceResult<T>(failure, default);
        }
    }
}
```

Create `SafeExchange.Core\Services\ServiceOperationException.cs`:

```csharp
/// <summary>
/// ServiceOperationException
/// </summary>

namespace SafeExchange.Core.Services
{
    using System;

    /// <summary>
    /// Wraps an exception thrown by the operation part of a service call: everything after the steps
    /// REST runs outside its error handler today (purge-if-needed, and for access calls the secret lookup
    /// and permission checks). Hosts turn it into their internal-error response; exceptions thrown before
    /// the operation part are not wrapped and propagate unchanged.
    /// </summary>
    public sealed class ServiceOperationException : Exception
    {
        public ServiceOperationException(Exception innerException)
            : base("A service operation failed.", innerException ?? throw new ArgumentNullException(nameof(innerException)))
        {
        }
    }
}
```

Create `SafeExchange.Core\Services\ServiceOperation.cs`:

```csharp
/// <summary>
/// ServiceOperation
/// </summary>

namespace SafeExchange.Core.Services
{
    using System;
    using System.Threading.Tasks;

    /// <summary>
    /// Runs the operation part of a service call and wraps any exception it throws in a
    /// <see cref="ServiceOperationException"/>.
    /// </summary>
    public static class ServiceOperation
    {
        public static async Task<TResult> RunAsync<TResult>(Func<Task<TResult>> operation)
        {
            if (operation is null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            try
            {
                return await operation();
            }
            catch (ServiceOperationException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new ServiceOperationException(exception);
            }
        }

        public static async Task RunAsync(Func<Task> operation)
        {
            if (operation is null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            try
            {
                await operation();
            }
            catch (ServiceOperationException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new ServiceOperationException(exception);
            }
        }
    }
}
```

Create `SafeExchange.Core\Services\ContentDownload.cs`:

```csharp
/// <summary>
/// ContentDownload
/// </summary>

namespace SafeExchange.Core.Services
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A successful download: the headers a host needs before the bytes, and the deferred write of the
    /// bytes. The data is read from storage, and the read is audited, only inside <see cref="WriteToAsync"/>.
    /// </summary>
    public sealed class ContentDownload
    {
        private readonly Func<Stream, CancellationToken, Task> writeTo;

        public ContentDownload(string contentType, long contentLength, string? contentDisposition, Func<Stream, CancellationToken, Task> writeTo)
        {
            this.ContentType = contentType ?? throw new ArgumentNullException(nameof(contentType));
            this.ContentLength = contentLength;
            this.ContentDisposition = contentDisposition;
            this.writeTo = writeTo ?? throw new ArgumentNullException(nameof(writeTo));
        }

        public string ContentType { get; }

        public long ContentLength { get; }

        /// <summary>
        /// The <c>Content-Disposition</c> header value, or <c>null</c> when the download has none (chunks).
        /// </summary>
        public string? ContentDisposition { get; }

        public Task WriteToAsync(Stream destination, CancellationToken cancellationToken = default)
            => this.writeTo(destination ?? throw new ArgumentNullException(nameof(destination)), cancellationToken);
    }
}
```

- [ ] **Step 4: Share the 500 response in `ActionResults` and add `ServiceCallAsync`**

In `SafeExchange.Core.Functions\Utilities\ActionResults.cs` add the using (after
`using SafeExchange.Core.Permissions;`):

```csharp
    using SafeExchange.Core.Services;
```

Then replace the `catch` block of `TryCatchAsync` and the end of the method. Before:

```csharp
            catch (Exception ex)
            {
                var correlationId = Guid.NewGuid().ToString("N");
                log.LogError(
                    ex,
                    "Exception in {ActionName}, correlationId={CorrelationId}",
                    actionName,
                    correlationId);

                return await CreateResponseAsync(
                    request,
                    HttpStatusCode.InternalServerError,
                    new BaseResponseObject<object>
                    {
                        Status = "error",
                        SubStatus = "internal_exception",
                        Error = $"Internal error. Reference: {correlationId}",
                    });
            }
        }
```

After:

```csharp
            catch (Exception ex)
            {
                return await InternalErrorAsync(request, ex, actionName, log);
            }
        }

        /// <summary>
        /// Executes <paramref name="serviceCall"/>, which calls a service and writes its result, and turns a
        /// <see cref="ServiceOperationException"/> into the same 500 response as <see cref="TryCatchAsync"/>
        /// (logging the wrapped exception). Any other exception propagates: services throw unwrapped only
        /// from the steps that ran outside <see cref="TryCatchAsync"/> before they were extracted.
        /// </summary>
        public static async Task<HttpResponseData> ServiceCallAsync(
            HttpRequestData request,
            Func<Task<HttpResponseData>> serviceCall,
            string actionName,
            ILogger log)
        {
            try
            {
                return await serviceCall();
            }
            catch (ServiceOperationException ex)
            {
                return await InternalErrorAsync(request, ex.InnerException ?? ex, actionName, log);
            }
        }

        private static async Task<HttpResponseData> InternalErrorAsync(HttpRequestData request, Exception ex, string actionName, ILogger log)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            log.LogError(
                ex,
                "Exception in {ActionName}, correlationId={CorrelationId}",
                actionName,
                correlationId);

            return await CreateResponseAsync(
                request,
                HttpStatusCode.InternalServerError,
                new BaseResponseObject<object>
                {
                    Status = "error",
                    SubStatus = "internal_exception",
                    Error = $"Internal error. Reference: {correlationId}",
                });
        }
```

`TryCatchAsync` behaves exactly as before (same log call, same response); `InternalErrorAsync` is the old
`catch` body moved into a method.

- [ ] **Step 5: Add the REST mapping**

Create `SafeExchange.Core.Functions\Utilities\ServiceResultHttpExtensions.cs`:

```csharp
/// <summary>
/// ServiceResultHttpExtensions
/// </summary>

namespace SafeExchange.Core.Services
{
    using Microsoft.Azure.Functions.Worker.Http;
    using System;
    using System.Net;
    using System.Threading.Tasks;

    public static class ServiceResultHttpExtensions
    {
        /// <summary>
        /// Writes the result as today's REST response: <c>200 {"status":"ok","result":"ok"}</c> on success,
        /// otherwise the failure's status code and a <see cref="BaseResponseObject{T}"/> with its status,
        /// subStatus, error and detail (as <c>result</c>).
        /// </summary>
        public static Task<HttpResponseData> ToResponseAsync(this ServiceResult result, HttpRequestData request)
        {
            if (result is null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            return result.IsOk
                ? ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK, new BaseResponseObject<string> { Status = ServiceStatus.Ok.ToStatusString(), Result = "ok" })
                : WriteFailureAsync(result, request);
        }

        /// <summary>
        /// Writes the result as today's REST response: <c>200</c> with the value as <c>result</c> on success,
        /// otherwise as <see cref="ToResponseAsync(ServiceResult, HttpRequestData)"/>.
        /// </summary>
        public static Task<HttpResponseData> ToResponseAsync<T>(this ServiceResult<T> result, HttpRequestData request)
        {
            if (result is null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            return result.IsOk
                ? ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK, new BaseResponseObject<T> { Status = ServiceStatus.Ok.ToStatusString(), Result = result.Value })
                : WriteFailureAsync(result.Outcome, request);
        }

        /// <summary>
        /// Writes a successful download as a raw <c>200</c> response (<c>Content-Type</c>, <c>Content-Length</c>,
        /// then <c>Content-Disposition</c> when present) and streams the bytes into its body; a failure is
        /// written as JSON like <see cref="ToResponseAsync(ServiceResult, HttpRequestData)"/>.
        /// </summary>
        public static async Task<HttpResponseData> ToDownloadResponseAsync(this ServiceResult<ContentDownload> result, HttpRequestData request)
        {
            if (result is null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            if (!result.IsOk)
            {
                return await WriteFailureAsync(result.Outcome, request);
            }

            var download = result.Value;
            var response = request.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", download.ContentType);
            response.Headers.Add("Content-Length", $"{download.ContentLength}");
            if (download.ContentDisposition is not null)
            {
                response.Headers.Add("Content-Disposition", download.ContentDisposition);
            }

            await download.WriteToAsync(response.Body);
            return response;
        }

        private static Task<HttpResponseData> WriteFailureAsync(ServiceResult failure, HttpRequestData request)
            => ActionResults.CreateResponseAsync(
                request,
                failure.StatusCode,
                new BaseResponseObject<object>
                {
                    Status = failure.Status.ToStatusString(),
                    SubStatus = failure.SubStatus,
                    Error = failure.Error,
                    Result = failure.Detail,
                });
    }
}
```

- [ ] **Step 6: Run the new tests**

Run: `dotnet build SafeExchange.slnx --nologo -v q; dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchange.Tests.Services"`
Expected: `0 Error(s)` and `Passed!  - Failed:     0, Passed:    29, Skipped:     0, Total:    29`.

- [ ] **Step 7: Run the phase filter**

Expected: `Passed!  - Failed:     0, Passed:   297` (268 + 29).

- [ ] **Step 8: Commit**

```powershell
git add SafeExchange.Core/Services SafeExchange.Core.Functions/Utilities/ServiceResultHttpExtensions.cs SafeExchange.Core.Functions/Utilities/ActionResults.cs SafeExchange.Tests/Services
git --no-pager diff --cached --name-status
```
Expected exactly these 10 lines:
```text
M	SafeExchange.Core.Functions/Utilities/ActionResults.cs
A	SafeExchange.Core.Functions/Utilities/ServiceResultHttpExtensions.cs
A	SafeExchange.Core/Services/ContentDownload.cs
A	SafeExchange.Core/Services/ServiceOperation.cs
A	SafeExchange.Core/Services/ServiceOperationException.cs
A	SafeExchange.Core/Services/ServiceResult.cs
A	SafeExchange.Core/Services/ServiceStatus.cs
A	SafeExchange.Core/Services/ServiceStatusNames.cs
A	SafeExchange.Tests/Services/ServiceResultHttpExtensionsTests.cs
A	SafeExchange.Tests/Services/ServiceResultTests.cs
```

```powershell
git commit -m "refactor(core): add ServiceResult and its REST mapping" -m "Core gains SafeExchange.Core.Services with ServiceStatus (one member per REST status string), ServiceResult / ServiceResult<T> (status, HTTP code, error, subStatus, detail), ServiceOperation / ServiceOperationException (marks the part of a service call that ran inside TryCatchAsync) and ContentDownload. SafeExchange.Core.Functions adds ServiceResultHttpExtensions (byte-identical BaseResponseObject bodies, raw downloads) and ActionResults.ServiceCallAsync, which turns operation failures into the existing 500 response." -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 2: `SecretService` and the `SafeExchangeSecretMeta` adapter

**Files:**
- Create: `SafeExchange.Tests\Services\ServiceTestBase.cs`
- Create: `SafeExchange.Tests\Services\SecretServiceTests.cs`
- Create: `SafeExchange.Core\Services\SecretService.cs`
- Create: `SafeExchange.Core.Functions\Requests\Filters\GlobalFiltersCallerResolver.cs`
- Modify: `SafeExchange.Core.Functions\Requests\Filters\GlobalFilters.cs`
- Replace: `SafeExchange.Core.Functions\Functions\SafeExchangeSecretMeta.cs`

- [ ] **Step 1: Write the shared service-test base and the failing tests**

Create `SafeExchange.Tests\Services\ServiceTestBase.cs` (EF InMemory, fixed clock, the real purger, permissions
manager and audit writer, an in-memory blob store, three user callers; later service fixtures reuse it):

```csharp
/// <summary>
/// ServiceTestBase
/// </summary>

namespace SafeExchange.Tests.Services
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging.Abstractions;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Services;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// Shared plumbing for service tests: a fresh EF InMemory database per test, a fixed clock, the real
    /// purger, permissions manager and audit writer, an in-memory blob store and three user callers.
    /// </summary>
    public abstract class ServiceTestBase
    {
        protected const string OwnerUpn = "owner@services.test";

        protected const string ReaderUpn = "reader@services.test";

        protected const string StrangerUpn = "stranger@services.test";

        protected static readonly DateTime FixedNow = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);

        private DbContextOptions<SafeExchangeDbContext> dbContextOptions = null!;

        private bool previousUseSpecifiedDateTime;

        private DateTime previousSpecifiedDateTime;

        protected IConfiguration Configuration { get; private set; } = null!;

        protected SafeExchangeDbContext DbContext { get; private set; } = null!;

        protected TestBlobHelper BlobHelper { get; private set; } = null!;

        protected IPurger Purger { get; private set; } = null!;

        protected IPermissionsManager PermissionsManager { get; private set; } = null!;

        protected IAuditWriter AuditWriter { get; private set; } = null!;

        protected Caller Owner { get; } = UserCaller(OwnerUpn);

        protected Caller Reader { get; } = UserCaller(ReaderUpn);

        protected Caller Stranger { get; } = UserCaller(StrangerUpn);

        [SetUp]
        public void ServiceSetUp()
        {
            this.previousUseSpecifiedDateTime = DateTimeProvider.UseSpecifiedDateTime;
            this.previousSpecifiedDateTime = DateTimeProvider.SpecifiedDateTime;
            DateTimeProvider.SpecifiedDateTime = FixedNow;
            DateTimeProvider.UseSpecifiedDateTime = true;

            this.Configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "Features:UseGroupsAuthorization", "False" },
                    { "AccessTickets:AccessTicketTimeout", "00:10:00" },
                })
                .Build();

            this.dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseInMemoryDatabase($"{this.GetType().Name}-{Guid.NewGuid()}")
                .Options;
            this.DbContext = new SafeExchangeDbContext(this.dbContextOptions);
            this.BlobHelper = new TestBlobHelper();
            this.Purger = new PurgeManager(this.Configuration, this.BlobHelper, NullLogger<PurgeManager>.Instance);
            this.PermissionsManager = new PermissionsManager(this.Configuration, this.DbContext, NullLogger<PermissionsManager>.Instance);
            this.AuditWriter = new AuditWriter(new ServiceDbContextFactory(this.dbContextOptions), NullLogger<AuditWriter>.Instance);
        }

        [TearDown]
        public void ServiceTearDown()
        {
            this.DbContext.Database.EnsureDeleted();
            this.DbContext.Dispose();
            DateTimeProvider.SpecifiedDateTime = this.previousSpecifiedDateTime;
            DateTimeProvider.UseSpecifiedDateTime = this.previousUseSpecifiedDateTime;
        }

        protected static Caller UserCaller(string upn)
            => new(SubjectType.User, upn, upn, Array.Empty<string>(), CallerChannels.Rest, null);

        protected static MetadataCreationInput CreationInput(bool? auditEnabled = null)
            => new()
            {
                ExpirationSettings = new ExpirationSettingsInput
                {
                    ScheduleExpiration = false,
                    ExpireAt = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    ExpireOnIdleTime = false,
                    IdleTimeToExpire = TimeSpan.FromDays(180),
                },
                AuditEnabled = auditEnabled,
            };

        protected SecretService CreateSecretService()
            => new(this.Configuration, this.DbContext, this.Purger, this.PermissionsManager, this.AuditWriter, NullLogger<SecretService>.Instance);

        protected async Task CreateSecretAsync(string secretId, bool? auditEnabled = null)
        {
            var result = await this.CreateSecretService().CreateAsync(this.Owner, secretId, CreationInput(auditEnabled));
            Assert.That(result.IsOk, Is.True, $"Arrange: secret creation failed: {result.Error}");
        }

        protected async Task GrantAsync(string secretId, string upn, PermissionType permission)
        {
            await this.PermissionsManager.SetPermissionAsync(SubjectType.User, upn, upn, secretId, permission);
            await this.DbContext.SaveChangesAsync();
        }

        protected static void AssertInsufficientPermissions(ServiceResult outcome, PermissionType permission, string secretId, string subStatus = "")
        {
            Assert.That(outcome.Status, Is.EqualTo(ServiceStatus.Forbidden));
            Assert.That(outcome.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(outcome.Error, Is.EqualTo($"Insufficient permissions to do '{permission}' action on '{secretId}'"));
            Assert.That(outcome.SubStatus, Is.EqualTo(subStatus));
            Assert.That(outcome.Detail, Is.Null);
        }

        private sealed class ServiceDbContextFactory : IDbContextFactory<SafeExchangeDbContext>
        {
            private readonly DbContextOptions<SafeExchangeDbContext> options;

            public ServiceDbContextFactory(DbContextOptions<SafeExchangeDbContext> options)
            {
                this.options = options ?? throw new ArgumentNullException(nameof(options));
            }

            public SafeExchangeDbContext CreateDbContext() => new(this.options);
        }
    }
}
```

Create `SafeExchange.Tests\Services\SecretServiceTests.cs`. Besides the permission denials (D8) it pins the
exception scopes of D2 (purge failures propagate unwrapped, failures inside the operation are wrapped):

```csharp
/// <summary>
/// SecretServiceTests
/// </summary>

namespace SafeExchange.Tests.Services
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging.Abstractions;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Services;
    using System;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;

    [TestFixture]
    public class SecretServiceTests : ServiceTestBase
    {
        private SecretService service = null!;

        [SetUp]
        public void SetUp()
        {
            this.service = this.CreateSecretService();
        }

        [Test]
        public async Task Create_GivesTheCallerFullPermissions_AndGetReturnsTheMetadata()
        {
            var created = await this.service.CreateAsync(this.Owner, "alpha", CreationInput());
            var read = await this.service.GetAsync(this.Owner, "alpha");

            Assert.That(created.IsOk, Is.True);
            Assert.That(created.Value!.ObjectName, Is.EqualTo("alpha"));
            Assert.That(read.IsOk, Is.True);
            Assert.That(read.Value!.ObjectName, Is.EqualTo("alpha"));
            Assert.That(await this.PermissionsManager.IsAuthorizedAsync(SubjectType.User, OwnerUpn, "alpha", PermissionType.Full), Is.True);
        }

        [Test]
        public async Task Create_Existing_IsConflict()
        {
            await this.CreateSecretAsync("alpha");

            var result = await this.service.CreateAsync(this.Stranger, "alpha", CreationInput());

            Assert.That(result.Status, Is.EqualTo(ServiceStatus.Conflict));
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(result.Error, Is.EqualTo("Secret 'alpha' already exists."));
        }

        [Test]
        public async Task Create_WithoutInput_IsBadRequest()
        {
            var result = await this.service.CreateAsync(this.Owner, "alpha", null);

            Assert.That(result.Status, Is.EqualTo(ServiceStatus.BadRequest));
            Assert.That(result.Error, Is.EqualTo("Expiration settings are not provided."));
        }

        [Test]
        public async Task Get_Missing_IsNotFound()
        {
            var result = await this.service.GetAsync(this.Owner, "missing");

            Assert.That(result.Status, Is.EqualTo(ServiceStatus.NotFound));
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(result.Error, Is.EqualTo("Secret 'missing' does not exist."));
        }

        [Test]
        public async Task Get_CallerWithoutRead_IsForbidden()
        {
            await this.CreateSecretAsync("alpha");

            var result = await this.service.GetAsync(this.Stranger, "alpha");

            AssertInsufficientPermissions(result.Outcome, PermissionType.Read, "alpha");
        }

        [Test]
        public async Task Update_ReaderWithoutWrite_IsForbidden()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, PermissionType.Read);

            var result = await this.service.UpdateAsync(this.Reader, "alpha", new MetadataUpdateInput { ExpirationSettings = CreationInput().ExpirationSettings });

            AssertInsufficientPermissions(result.Outcome, PermissionType.Write, "alpha");
        }

        [Test]
        public async Task Delete_ReaderWithoutWrite_IsForbidden_AndKeepsTheSecret()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, PermissionType.Read);

            var result = await this.service.DeleteAsync(this.Reader, "alpha");

            AssertInsufficientPermissions(result, PermissionType.Write, "alpha");
            Assert.That(await this.DbContext.Objects.AnyAsync(o => o.ObjectName == "alpha"), Is.True);
        }

        [Test]
        public async Task Delete_Owner_RemovesTheSecret()
        {
            await this.CreateSecretAsync("alpha");

            var result = await this.service.DeleteAsync(this.Owner, "alpha");

            Assert.That(result.IsOk, Is.True);
            Assert.That(await this.DbContext.Objects.AnyAsync(o => o.ObjectName == "alpha"), Is.False);
        }

        [Test]
        public async Task Lists_OnlyContainSecretsTheCallerCanRead()
        {
            await this.CreateSecretAsync("alpha");
            await this.CreateSecretAsync("beta");
            await this.GrantAsync("beta", ReaderUpn, PermissionType.Read);

            var v3 = await this.service.ListAsync(this.Reader, null);
            var v2 = await this.service.ListV2Async(this.Reader, null);
            var stranger = await this.service.ListAsync(this.Stranger, null);

            Assert.That(v3.Value!.Select(s => s.ObjectName), Is.EqualTo(new[] { "beta" }));
            Assert.That(v3.Value!.Single().CallerEffectivePermissions.CanRead, Is.True);
            Assert.That(v2.Value!.Select(s => s.ObjectName), Is.EqualTo(new[] { "beta" }));
            Assert.That(stranger.Value, Is.Empty);
        }

        [Test]
        public async Task List_InvalidTag_IsBadRequest()
        {
            var result = await this.service.ListAsync(this.Owner, new[] { "not a tag!" });

            Assert.That(result.Status, Is.EqualTo(ServiceStatus.BadRequest));
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(result.Error, Is.Not.Empty);
        }

        [Test]
        public void PurgeFailure_PropagatesUnwrapped()
        {
            var purger = new Mock<IPurger>();
            var failure = new InvalidOperationException("purge failed");
            purger.Setup(p => p.PurgeIfNeededAsync(It.IsAny<string>(), It.IsAny<SafeExchangeDbContext>(), It.IsAny<IAuditWriter>(), It.IsAny<int>())).ThrowsAsync(failure);
            var service = new SecretService(this.Configuration, this.DbContext, purger.Object, this.PermissionsManager, this.AuditWriter, NullLogger<SecretService>.Instance);

            var thrown = Assert.ThrowsAsync<InvalidOperationException>(() => service.GetAsync(this.Owner, "alpha"));

            Assert.That(thrown, Is.SameAs(failure));
        }

        [Test]
        public async Task OperationFailure_IsWrappedInServiceOperationException()
        {
            await this.CreateSecretAsync("alpha");
            var permissions = new Mock<IPermissionsManager>();
            var failure = new InvalidOperationException("permission store failed");
            permissions.Setup(p => p.IsAuthorizedAsync(It.IsAny<SubjectType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PermissionType>())).ThrowsAsync(failure);
            var service = new SecretService(this.Configuration, this.DbContext, this.Purger, permissions.Object, this.AuditWriter, NullLogger<SecretService>.Instance);

            var thrown = Assert.ThrowsAsync<ServiceOperationException>(() => service.GetAsync(this.Owner, "alpha"));

            Assert.That(thrown!.InnerException, Is.SameAs(failure));
        }
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet build SafeExchange.slnx --nologo -v q`
Expected: FAIL with `error CS0246: The type or namespace name 'SecretService' could not be found` in
`ServiceTestBase.cs` and `SecretServiceTests.cs`.

- [ ] **Step 3: Create `SecretService`**

Create `SafeExchange.Core\Services\SecretService.cs`. The bodies are the old handler bodies with
`ActionResults.CreateResponseAsync(...)` replaced by `ServiceResult` values, `subjectType`/`subjectId` by
`caller.SubjectType`/`caller.SubjectId`, `log` by the service logger, and body parsing moved to the adapter:

```csharp
/// <summary>
/// SecretService
/// </summary>

namespace SafeExchange.Core.Services
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Telemetry;
    using SafeExchange.Core.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;

    /// <summary>
    /// Secret metadata: create, read, update, delete and the v2 / v3 secret lists.
    /// </summary>
    public sealed class SecretService
    {
        private readonly Features features;

        private readonly SafeExchangeDbContext dbContext;

        private readonly IPurger purger;

        private readonly IPermissionsManager permissionsManager;

        private readonly IAuditWriter auditWriter;

        private readonly ILogger log;

        public SecretService(IConfiguration configuration, SafeExchangeDbContext dbContext, IPurger purger, IPermissionsManager permissionsManager, IAuditWriter auditWriter, ILogger<SecretService> log)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            this.features = new Features();
            configuration.GetSection("Features").Bind(this.features);

            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.purger = purger ?? throw new ArgumentNullException(nameof(purger));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
            this.auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// The caller's readable secrets with direct and effective permissions (REST v3 list).
        /// </summary>
        public async Task<ServiceResult<List<SecretListItemOutput>>> ListAsync(Caller caller, IEnumerable<string>? tags)
        {
            ArgumentNullException.ThrowIfNull(caller);
            return await ServiceOperation.RunAsync<ServiceResult<List<SecretListItemOutput>>>(async () =>
            {
                var (tagsOk, requiredTags, tagsError) = TagValidator.TryNormalizeList(tags ?? Array.Empty<string>());
                if (!tagsOk)
                {
                    this.log.LogInformation($"Invalid tag filter: {tagsError}.");
                    return ServiceResult.BadRequest(tagsError!);
                }

                var readableSecrets = await this.permissionsManager.GetReadableSecretsAsync(caller.SubjectType, caller.SubjectId);

                var names = readableSecrets.Select(e => e.SecretName).Distinct().ToList();
                var tagMap = await this.LoadTagMapAsync(names, requiredTags);

                if (requiredTags.Count > 0)
                {
                    var matched = new HashSet<string>(tagMap.Keys);
                    readableSecrets = readableSecrets.Where(e => matched.Contains(e.SecretName)).ToList();
                }

                var dtos = readableSecrets.Select(e => new SecretListItemOutput
                {
                    ObjectName = e.SecretName,
                    SubjectType = caller.SubjectType.ToDto(),
                    SubjectName = caller.SubjectId,
                    SubjectId = caller.SubjectId,
                    CanRead = (e.Direct & PermissionType.Read) == PermissionType.Read,
                    CanWrite = (e.Direct & PermissionType.Write) == PermissionType.Write,
                    CanGrantAccess = (e.Direct & PermissionType.GrantAccess) == PermissionType.GrantAccess,
                    CanRevokeAccess = (e.Direct & PermissionType.RevokeAccess) == PermissionType.RevokeAccess,
                    CallerEffectivePermissions = EffectivePermissionsOutput.FromPermissionType(e.Effective),
                    Tags = tagMap.TryGetValue(e.SecretName, out var t) ? t.ToList() : new List<string>(),
                }).ToList();

                return ServiceResult<List<SecretListItemOutput>>.Ok(dtos);
            });
        }

        /// <summary>
        /// The caller's direct read permission rows (REST v2 list).
        /// </summary>
        public async Task<ServiceResult<List<SubjectPermissionsOutput>>> ListV2Async(Caller caller, IEnumerable<string>? tags)
        {
            ArgumentNullException.ThrowIfNull(caller);
            return await ServiceOperation.RunAsync<ServiceResult<List<SubjectPermissionsOutput>>>(async () =>
            {
                var (tagsOk, requiredTags, tagsError) = TagValidator.TryNormalizeList(tags ?? Array.Empty<string>());
                if (!tagsOk)
                {
                    this.log.LogInformation($"Invalid tag filter: {tagsError}.");
                    return ServiceResult.BadRequest(tagsError!);
                }

                var subjectType = caller.SubjectType;
                var subjectId = caller.SubjectId;
                var permissions = await this.dbContext.Permissions
                    .Where(p => p.SubjectType.Equals(subjectType) && p.SubjectId.Equals(subjectId) && p.CanRead)
                    .ToListAsync();

                var names = permissions.Select(p => p.SecretName).Distinct().ToList();
                var tagMap = await this.LoadTagMapAsync(names, requiredTags);

                if (requiredTags.Count > 0)
                {
                    var matched = new HashSet<string>(tagMap.Keys);
                    permissions = permissions.Where(p => matched.Contains(p.SecretName)).ToList();
                }

                var dtos = permissions.Select(p =>
                {
                    var dto = p.ToDto();
                    dto.Tags = tagMap.TryGetValue(p.SecretName, out var t) ? t.ToList() : new List<string>();
                    return dto;
                }).ToList();

                return ServiceResult<List<SubjectPermissionsOutput>>.Ok(dtos);
            });
        }

        /// <summary>
        /// Creates a secret and gives the caller full permissions. <paramref name="input"/> is <c>null</c> when
        /// the host could not read it (answered like missing expiration settings).
        /// </summary>
        public async Task<ServiceResult<ObjectMetadataOutput>> CreateAsync(Caller caller, string secretId, MetadataCreationInput? input)
        {
            ArgumentNullException.ThrowIfNull(caller);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync<ServiceResult<ObjectMetadataOutput>>(async () =>
            {
                var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
                if (existingMetadata != null)
                {
                    this.log.LogInformation($"Cannot create secret '{secretId}', as already exists");
                    return ServiceResult.Conflict($"Secret '{secretId}' already exists.");
                }

                if ((input is null) || input.ExpirationSettings == null)
                {
                    this.log.LogInformation($"Expiration settings for '{secretId}' not provided.");
                    return ServiceResult.BadRequest("Expiration settings are not provided.");
                }

                // Creation-time naming contract, mirrored by the UI for immediate feedback.
                // Deliberately not applied to read/update/delete: existing identifiers accepted
                // under earlier behavior stay usable, subject to normal authorization.
                if (!SecretNameValidator.TryValidate(secretId, out var secretIdError))
                {
                    this.log.LogInformation($"Secret id value is not valid: {secretIdError}");
                    return ServiceResult.BadRequest(secretIdError!);
                }

                var (tagsOk, normalisedTags, tagsError) = TagValidator.TryNormalizeList(input.Tags);
                if (!tagsOk)
                {
                    this.log.LogInformation($"Invalid tags for secret '{secretId}': {tagsError}.");
                    return ServiceResult.BadRequest(tagsError!);
                }
                input.Tags = normalisedTags.ToList();

                var createdMetadata = await this.CreateMetadataAndPermissionsAsync(secretId, input, caller);
                this.log.LogInformation($"Metadata for secret '{secretId}' added, full permissions for {caller.SubjectType} '(tid {TelemetryContext.Current})' are set.");

                if (createdMetadata.AuditEnabled)
                {
                    await this.auditWriter.EnsureAnchorAsync(createdMetadata, caller.SubjectId);
                    var creationPayload = new
                    {
                        tags = createdMetadata.Tags ?? new List<string>(),
                        expirationSettings = createdMetadata.ExpirationMetadata.ToDto(),
                        auditEnabled = true,
                    };
                    await this.auditWriter.AppendAsync(
                        createdMetadata, SecretAuditEventType.SecretCreated,
                        caller.SubjectType, caller.SubjectId, creationPayload, this.log);
                }

                return ServiceResult<ObjectMetadataOutput>.Ok(createdMetadata.ToDto());
            });
        }

        public async Task<ServiceResult<ObjectMetadataOutput>> GetAsync(Caller caller, string secretId)
        {
            ArgumentNullException.ThrowIfNull(caller);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync<ServiceResult<ObjectMetadataOutput>>(async () =>
            {
                var metadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
                if (metadata == null)
                {
                    this.log.LogInformation($"Cannot get secret '{secretId}', as no metadata exists.");
                    return ServiceResult.NotFound($"Secret '{secretId}' does not exist.");
                }

                if (!metadata.KeepInStorage)
                {
                    this.log.LogInformation($"The data is not kept in storage, cannot use this api.");
                    return ServiceResult.Unprocessable("Cannot use this endpoint for previous versions data.");
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Read)))
                {
                    return ServiceResult.InsufficientPermissions(PermissionType.Read, secretId);
                }

                metadata.LastAccessedAt = DateTimeProvider.UtcNow;
                await this.dbContext.SaveChangesAsync();

                return ServiceResult<ObjectMetadataOutput>.Ok(metadata.ToDto());
            });
        }

        /// <summary>
        /// Updates expiration settings and, when given, tags. <paramref name="input"/> is <c>null</c> when the
        /// host could not read it (answered like missing expiration settings).
        /// </summary>
        public async Task<ServiceResult<ObjectMetadataOutput>> UpdateAsync(Caller caller, string secretId, MetadataUpdateInput? input)
        {
            ArgumentNullException.ThrowIfNull(caller);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync<ServiceResult<ObjectMetadataOutput>>(async () =>
            {
                var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
                if (existingMetadata == null)
                {
                    this.log.LogInformation($"Cannot update secret '{secretId}', as no metadata exists.");
                    return ServiceResult.NotFound($"Secret '{secretId}' does not exist.");
                }

                if (!existingMetadata.KeepInStorage)
                {
                    this.log.LogInformation($"The data is not kept in storage, cannot use this api.");
                    return ServiceResult.Unprocessable("Cannot use this endpoint for previous versions data.");
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Write)))
                {
                    return ServiceResult.InsufficientPermissions(PermissionType.Write, secretId);
                }

                if ((input is null) || input.ExpirationSettings == null)
                {
                    this.log.LogInformation($"Expiration settings for '{secretId}' not provided.");
                    return ServiceResult.BadRequest("Expiration settings are not provided.");
                }

                if (string.IsNullOrEmpty(secretId))
                {
                    this.log.LogInformation("Secret id value is not provided.");
                    return ServiceResult.BadRequest("Secret id value is not provided.");
                }

                if (input.Tags is not null)
                {
                    var (tagsOk, normalisedTags, tagsError) = TagValidator.TryNormalizeList(input.Tags);
                    if (!tagsOk)
                    {
                        this.log.LogInformation($"Invalid tags for secret '{secretId}': {tagsError}.");
                        return ServiceResult.BadRequest(tagsError!);
                    }
                    input.Tags = normalisedTags.ToList();
                }

                // Build the audit diff against the live entity BEFORE UpdateMetadataAsync mutates
                // it in place. MetadataDiffBuilder is the single canonical implementation; do not
                // re-inline diff logic here.
                string? diffJson = null;
                if (existingMetadata.AuditEnabled)
                {
                    diffJson = MetadataDiffBuilder.BuildDiff(existingMetadata, input);
                }

                var updatedMetadata = await this.UpdateMetadataAsync(existingMetadata, input);
                this.log.LogInformation($"{caller.SubjectType} '(tid {TelemetryContext.Current})' updated metadata for secret '{existingMetadata.ObjectName}'.");

                if (diffJson is not null)
                {
                    var diffObj = System.Text.Json.JsonSerializer.Deserialize<object>(diffJson);
                    await this.auditWriter.AppendAsync(
                        updatedMetadata, SecretAuditEventType.SecretMetadataUpdated,
                        caller.SubjectType, caller.SubjectId, diffObj, this.log);
                }

                return ServiceResult<ObjectMetadataOutput>.Ok(updatedMetadata.ToDto());
            });
        }

        public async Task<ServiceResult> DeleteAsync(Caller caller, string secretId)
        {
            ArgumentNullException.ThrowIfNull(caller);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync(async () =>
            {
                var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
                if (existingMetadata == null)
                {
                    this.log.LogInformation($"Cannot delete secret '{secretId}', as no metadata exists.");
                    return ServiceResult.NotFound($"Secret '{secretId}' does not exist.");
                }

                if (!existingMetadata.KeepInStorage)
                {
                    this.log.LogInformation($"The data is not kept in storage, cannot use this api.");
                    return ServiceResult.Unprocessable("Cannot use this endpoint for previous versions data.");
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Write)))
                {
                    return ServiceResult.InsufficientPermissions(PermissionType.Write, secretId);
                }

                await this.purger.PurgeAsync(secretId, this.dbContext, this.auditWriter, caller.SubjectType, caller.SubjectId, this.features.AuditRetentionDays);
                this.log.LogInformation($"{caller.SubjectType} '(tid {TelemetryContext.Current})' deleted secret '{secretId}'");

                return ServiceResult.Ok();
            });
        }

        private async Task<Dictionary<string, List<string>>> LoadTagMapAsync(List<string> names, IReadOnlyList<string> requiredTags)
        {
            var tagMap = new Dictionary<string, List<string>>();
            if (names.Count == 0)
            {
                return tagMap;
            }

            IQueryable<ObjectMetadata> tagQuery = this.dbContext.Objects
                .Where(o => names.Contains(o.ObjectName));

            foreach (var rt in requiredTags)
            {
                var tag = rt; // local capture so closure binds correctly
                tagQuery = tagQuery.Where(o => o.Tags.Contains(tag));
            }

            var tagPairs = await tagQuery
                .Select(o => new { o.ObjectName, o.Tags })
                .ToListAsync();

            foreach (var pair in tagPairs)
            {
                tagMap[pair.ObjectName] = pair.Tags?.ToList() ?? new List<string>();
            }

            return tagMap;
        }

        private async Task<ObjectMetadata> CreateMetadataAndPermissionsAsync(string secretId, MetadataCreationInput metadataInput, Caller caller)
        {
            var objectMetadata = new ObjectMetadata(secretId, metadataInput, $"{caller.SubjectType} {caller.SubjectId}");
            var entity = await this.dbContext.Objects.AddAsync(objectMetadata);

            var subjectName = caller.SubjectId;
            await this.permissionsManager.SetPermissionAsync(caller.SubjectType, caller.SubjectId, subjectName, secretId, PermissionType.Full);

            await this.dbContext.SaveChangesAsync();

            return entity.Entity;
        }

        private async Task<ObjectMetadata> UpdateMetadataAsync(ObjectMetadata existingMetadata, MetadataUpdateInput metadataInput)
        {
            var updatedExpirationMetadata = new ExpirationMetadata(metadataInput.ExpirationSettings);
            existingMetadata.ExpirationMetadata = updatedExpirationMetadata;
            existingMetadata.LastAccessedAt = DateTimeProvider.UtcNow;

            if (metadataInput.Tags is not null)
            {
                existingMetadata.Tags = metadataInput.Tags.ToList();
            }

            await this.dbContext.SaveChangesAsync();

            return existingMetadata;
        }
    }
}
```

- [ ] **Step 4: Add the old-preamble resolver for the pre-service constructors (D5)**

In `SafeExchange.Core.Functions\Requests\Filters\GlobalFilters.cs` add the using after
`using Microsoft.Extensions.Options;`:

```csharp
    using SafeExchange.Core.Callers;
```

and insert this method between `GetFilterResultAsync` and `GetAdminFilterResultAsync` (nothing else changes):

```csharp
        /// <summary>
        /// The global access-group decision of <see cref="GetFilterResultAsync"/> without writing a response:
        /// <c>null</c> when the caller passes, otherwise the denial. Logs under this class, like the filter.
        /// </summary>
        public Task<CallerDenial?> GetGlobalAccessDenialAsync(ClaimsPrincipal principal, SafeExchangeDbContext dbContext)
            => new GlobalAccessPolicy(this.groupsConfiguration.CurrentValue, this.tokenHelper, this.log).EvaluateAsync(principal, dbContext);

```

Create `SafeExchange.Core.Functions\Requests\Filters\GlobalFiltersCallerResolver.cs`:

```csharp
/// <summary>
/// GlobalFiltersCallerResolver
/// </summary>

namespace SafeExchange.Core.Filters
{
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Model;
    using System;
    using System.Security.Claims;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// The caller resolution behind the handlers' pre-service constructors: exactly the old preamble, i.e.
    /// the <see cref="GlobalFilters"/> global access-group gate (its configuration, token helper and log),
    /// then <see cref="SubjectHelper.GetSubjectInfoAsync"/> with the handler's token helper, then the
    /// registered-application gate. Hosts that use DI get <see cref="CallerResolver"/> instead.
    /// </summary>
    internal sealed class GlobalFiltersCallerResolver : ICallerResolver
    {
        private readonly GlobalFilters globalFilters;

        private readonly ITokenHelper tokenHelper;

        private readonly SafeExchangeDbContext dbContext;

        public GlobalFiltersCallerResolver(GlobalFilters globalFilters, ITokenHelper tokenHelper, SafeExchangeDbContext dbContext)
        {
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public async Task<CallerResolution> ResolveAsync(ClaimsPrincipal principal, string channel, string? clientLabel, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(principal);
            ArgumentException.ThrowIfNullOrEmpty(channel);

            var denial = await this.globalFilters.GetGlobalAccessDenialAsync(principal, this.dbContext);
            if (denial is not null)
            {
                return CallerResolution.Denied(denial);
            }

            (SubjectType subjectType, string subjectId) = await SubjectHelper.GetSubjectInfoAsync(this.tokenHelper, principal, this.dbContext);
            if (SubjectType.Application.Equals(subjectType) && string.IsNullOrEmpty(subjectId))
            {
                return CallerResolution.Denied(CallerDenial.Forbidden(CallerResolver.ApplicationNotRegisteredError));
            }

            // The old preamble never read the display name and REST does not use it, so no token lookup is added.
            return CallerResolution.Ok(new Caller(subjectType, subjectId, subjectId, Array.Empty<string>(), channel, clientLabel));
        }
    }
}
```

- [ ] **Step 5: Replace the handler with the adapter**

Replace the whole content of `SafeExchange.Core.Functions\Functions\SafeExchangeSecretMeta.cs` with:

```csharp
/// <summary>
/// SafeExchangeSecretMeta
/// </summary>

namespace SafeExchange.Core.Functions
{
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using System.Security.Claims;
    using System;
    using System.IO;

    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging.Abstractions;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Services;
    using SafeExchange.Core.Telemetry;
    using Microsoft.Azure.Functions.Worker.Http;
    using System.Collections.Generic;
    using System.Net;

    /// <summary>
    /// HTTP adapter for secret metadata (<c>/secret/{secretId}</c>, <c>/secret-list</c>) over <see cref="SecretService"/>.
    /// </summary>
    public class SafeExchangeSecretMeta
    {
        private readonly ICallerResolver callerResolver;

        private readonly SecretService secretService;

        public SafeExchangeSecretMeta(ICallerResolver callerResolver, SecretService secretService)
        {
            this.callerResolver = callerResolver ?? throw new ArgumentNullException(nameof(callerResolver));
            this.secretService = secretService ?? throw new ArgumentNullException(nameof(secretService));
        }

        /// <summary>
        /// Pre-service constructor, kept for code that builds handlers by hand: resolves callers through
        /// <paramref name="globalFilters"/> exactly as before and builds the service itself (without logging).
        /// </summary>
        public SafeExchangeSecretMeta(IConfiguration configuration, SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, IPurger purger, IPermissionsManager permissionsManager, IAuditWriter auditWriter)
            : this(
                  new GlobalFiltersCallerResolver(globalFilters, tokenHelper, dbContext),
                  new SecretService(configuration, dbContext, purger, permissionsManager, auditWriter, NullLogger<SecretService>.Instance))
        {
        }

        public async Task<HttpResponseData> Run(
            HttpRequestData request,
            string secretId, ClaimsPrincipal principal, ILogger log)
        {
            var resolution = await this.callerResolver.ResolveAsync(principal, CallerChannels.Rest, null);
            if (!resolution.IsAllowed)
            {
                return await resolution.Denial.ToResponseAsync(request);
            }

            var caller = resolution.Caller;
            log.LogInformation($"{nameof(SafeExchangeSecretMeta)} triggered for '{secretId}' by {caller.SubjectType} (tid {TelemetryContext.Current}) [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "post":
                    return await ActionResults.ServiceCallAsync(request, async () =>
                    {
                        var input = await ReadInputAsync<MetadataCreationInput>(request, secretId, log);
                        return await (await this.secretService.CreateAsync(caller, secretId, input)).ToResponseAsync(request);
                    }, "HandleSecretMetaCreation", log);

                case "get":
                    return await ActionResults.ServiceCallAsync(
                        request,
                        async () => await (await this.secretService.GetAsync(caller, secretId)).ToResponseAsync(request),
                        "HandleSecretMetaRead", log);

                case "patch":
                    return await ActionResults.ServiceCallAsync(request, async () =>
                    {
                        var input = await ReadInputAsync<MetadataUpdateInput>(request, secretId, log);
                        return await (await this.secretService.UpdateAsync(caller, secretId, input)).ToResponseAsync(request);
                    }, "HandleSecretMetaUpdate", log);

                case "delete":
                    return await ActionResults.ServiceCallAsync(
                        request,
                        async () => await (await this.secretService.DeleteAsync(caller, secretId)).ToResponseAsync(request),
                        "HandleSecretMetaDeletion", log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        public async Task<HttpResponseData> RunList(
            HttpRequestData request, ClaimsPrincipal principal, ILogger log)
        {
            var resolution = await this.callerResolver.ResolveAsync(principal, CallerChannels.Rest, null);
            if (!resolution.IsAllowed)
            {
                return await resolution.Denial.ToResponseAsync(request);
            }

            var caller = resolution.Caller;
            log.LogInformation($"{nameof(SafeExchangeSecretMeta)}-{nameof(RunList)} triggered by {caller.SubjectType} (tid {TelemetryContext.Current}) [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "get":
                    return await ActionResults.ServiceCallAsync(
                        request,
                        async () => await (await this.secretService.ListV2Async(caller, GetTagFilter(request))).ToResponseAsync(request),
                        "HandleListSecretMeta", log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        public async Task<HttpResponseData> RunListV3(
            HttpRequestData request, ClaimsPrincipal principal, ILogger log)
        {
            var resolution = await this.callerResolver.ResolveAsync(principal, CallerChannels.Rest, null);
            if (!resolution.IsAllowed)
            {
                return await resolution.Denial.ToResponseAsync(request);
            }

            var caller = resolution.Caller;
            log.LogInformation($"{nameof(SafeExchangeSecretMeta)}-{nameof(RunListV3)} triggered by {caller.SubjectType} (tid {TelemetryContext.Current}) [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "get":
                    return await ActionResults.ServiceCallAsync(
                        request,
                        async () => await (await this.secretService.ListAsync(caller, GetTagFilter(request))).ToResponseAsync(request),
                        "HandleListSecretMetaV3", log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        private static IReadOnlyList<string> GetTagFilter(HttpRequestData request)
        {
            var query = request.Url?.Query;
            if (string.IsNullOrEmpty(query))
            {
                return Array.Empty<string>();
            }

            return System.Web.HttpUtility.ParseQueryString(query).GetValues("tag") ?? Array.Empty<string>();
        }

        private static async Task<T?> ReadInputAsync<T>(HttpRequestData request, string secretId, ILogger log) where T : class
        {
            var requestBody = await new StreamReader(request.Body ?? Stream.Null).ReadToEndAsync();
            try
            {
                return DefaultJsonSerializer.Deserialize<T>(requestBody);
            }
            catch
            {
                log.LogInformation($"Could not parse input data for '{secretId}'.");
                return null;
            }
        }
    }
}
```

- [ ] **Step 6: Run the service tests**

Run: `dotnet build SafeExchange.slnx --nologo -v q; dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchange.Tests.Services"`
Expected: `0 Error(s)` and `Passed!  - Failed:     0, Passed:    41` (29 + 12).

- [ ] **Step 7: Run the phase filter**

Expected: `Passed!  - Failed:     0, Passed:   309` (268 + 41). The secret-meta, list, global-filter and
mixed-case contract tests now run through the adapter and `SecretService`.

- [ ] **Step 8: Commit**

```powershell
git add SafeExchange.Core/Services/SecretService.cs SafeExchange.Core.Functions/Requests/Filters/GlobalFilters.cs SafeExchange.Core.Functions/Requests/Filters/GlobalFiltersCallerResolver.cs SafeExchange.Core.Functions/Functions/SafeExchangeSecretMeta.cs SafeExchange.Tests/Services/ServiceTestBase.cs SafeExchange.Tests/Services/SecretServiceTests.cs
git --no-pager diff --cached --name-status
```
Expected exactly:
```text
M	SafeExchange.Core.Functions/Functions/SafeExchangeSecretMeta.cs
M	SafeExchange.Core.Functions/Requests/Filters/GlobalFilters.cs
A	SafeExchange.Core.Functions/Requests/Filters/GlobalFiltersCallerResolver.cs
A	SafeExchange.Core/Services/SecretService.cs
A	SafeExchange.Tests/Services/SecretServiceTests.cs
A	SafeExchange.Tests/Services/ServiceTestBase.cs
```

```powershell
git commit -m "refactor(core): extract SecretService" -m "Secret metadata create/read/update/delete and the v2/v3 lists move from SafeExchangeSecretMeta into Core's SecretService (Caller in, ServiceResult out; purge-if-needed stays first). The handler becomes an HTTP adapter: ICallerResolver instead of the GlobalFilters/SubjectHelper preamble, body and query parsing, ServiceResult to the identical REST response. Its old constructor stays and resolves callers through the new GlobalFiltersCallerResolver, which reproduces the old preamble exactly." -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 3: `ContentService` (incl. commit) and the content-meta and commit adapters

**Files:**
- Create: `SafeExchange.Tests\Services\ContentServiceTests.cs`
- Create: `SafeExchange.Core\Services\AccessTicketManager.cs`
- Create: `SafeExchange.Core\Services\ContentCommitInput.cs`
- Create: `SafeExchange.Core\Services\ContentService.cs`
- Replace: `SafeExchange.Core.Functions\Functions\SafeExchangeSecretContentMeta.cs`
- Replace: `SafeExchange.Core.Functions\Functions\SafeExchangeContentCommit.cs`

Commit moves here (D7): it is a content state change without blob access, and `SafeExchangeContentCommit`'s old
constructor has no `IBlobHelper`.

- [ ] **Step 1: Write the failing tests**

Create `SafeExchange.Tests\Services\ContentServiceTests.cs`:

```csharp
/// <summary>
/// ContentServiceTests
/// </summary>

namespace SafeExchange.Tests.Services
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging.Abstractions;
    using NUnit.Framework;
    using SafeExchange.Core.Crypto;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Services;
    using System;
    using System.Linq;
    using System.Net;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading.Tasks;

    [TestFixture]
    public class ContentServiceTests : ServiceTestBase
    {
        private const string Ticket = "ticket-1";

        private ContentService service = null!;

        [SetUp]
        public void SetUp()
        {
            this.service = new ContentService(this.Configuration, this.DbContext, this.Purger, this.PermissionsManager, this.AuditWriter, NullLogger<ContentService>.Instance);
        }

        [Test]
        public async Task Create_Owner_AddsABlankContentItem()
        {
            await this.CreateSecretAsync("alpha");

            var result = await this.service.CreateAsync(this.Owner, "alpha", string.Empty, Attachment());

            Assert.That(result.IsOk, Is.True);
            Assert.That(result.Value!.ContentType, Is.EqualTo("application/pdf"));
            Assert.That(result.Value!.FileName, Is.EqualTo("report.pdf"));
            Assert.That(result.Value!.IsMain, Is.False);
        }

        [Test]
        public async Task EveryMethod_DeniesAReaderWithoutWrite()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, PermissionType.Read);

            AssertInsufficientPermissions((await this.service.CreateAsync(this.Reader, "alpha", string.Empty, Attachment())).Outcome, PermissionType.Write, "alpha");
            AssertInsufficientPermissions((await this.service.UpdateAsync(this.Reader, "alpha", contentId, new ContentMetadataUpdateInput { ContentType = "text/plain", FileName = "a.txt" })).Outcome, PermissionType.Write, "alpha");
            AssertInsufficientPermissions((await this.service.DropAsync(this.Reader, "alpha", contentId)).Outcome, PermissionType.Write, "alpha");
            AssertInsufficientPermissions(await this.service.DeleteAsync(this.Reader, "alpha", contentId), PermissionType.Write, "alpha");
            AssertInsufficientPermissions((await this.service.CommitAsync(this.Reader, "alpha", contentId, Ticket, new ContentCommitInput(new string('0', 64)))).Outcome, PermissionType.Write, "alpha");
        }

        [Test]
        public async Task Delete_MainContent_IsBadRequest()
        {
            await this.CreateSecretAsync("alpha");
            var main = (await this.DbContext.Objects.SingleAsync(o => o.ObjectName == "alpha")).Content.Single(c => c.IsMain).ContentName;

            var result = await this.service.DeleteAsync(this.Owner, "alpha", main);

            Assert.That(result.Status, Is.EqualTo(ServiceStatus.BadRequest));
            Assert.That(result.Error, Is.EqualTo("Cannot delete main content."));
        }

        [Test]
        public async Task Drop_LockedContent_IsUnprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");
            await this.LockAsync("alpha", contentId, FixedNow);

            var result = await this.service.DropAsync(this.Owner, "alpha", contentId);

            Assert.That(result.Status, Is.EqualTo(ServiceStatus.Unprocessable));
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
            Assert.That(result.Error, Is.EqualTo("Content is being updated."));
        }

        [Test]
        public async Task Drop_ExpiredLock_IsReleasedAndTheContentIsDropped()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");
            await this.LockAsync("alpha", contentId, FixedNow.AddMinutes(-11));

            var result = await this.service.DropAsync(this.Owner, "alpha", contentId);

            Assert.That(result.IsOk, Is.True);
            var content = await this.ContentAsync("alpha", contentId);
            Assert.That(content.AccessTicket, Is.Empty);
            Assert.That(content.Status, Is.EqualTo(ContentStatus.Blank));
            Assert.That(content.Chunks, Is.Empty);
        }

        [Test]
        public async Task Update_UnknownContent_FailsInsideTheOperation()
        {
            await this.CreateSecretAsync("alpha");

            var thrown = Assert.ThrowsAsync<ServiceOperationException>(() => this.service.UpdateAsync(
                this.Owner, "alpha", "missing", new ContentMetadataUpdateInput { ContentType = "text/plain", FileName = "a.txt" }));

            Assert.That(thrown!.InnerException, Is.InstanceOf<InvalidOperationException>());
        }

        [Test]
        public async Task Commit_MalformedInput_IsAnsweredAfterTheLockCheck()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");
            await this.StartHashedUploadAsync("alpha", contentId, Encoding.UTF8.GetBytes("hello"));

            var wrongTicket = await this.service.CommitAsync(this.Owner, "alpha", contentId, "other", ContentCommitInput.Malformed);
            var malformed = await this.service.CommitAsync(this.Owner, "alpha", contentId, Ticket, ContentCommitInput.Malformed);

            Assert.That(wrongTicket.Status, Is.EqualTo(ServiceStatus.Unauthorized));
            Assert.That(wrongTicket.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(wrongTicket.Error, Is.EqualTo("Access ticket missing or invalid."));
            Assert.That(malformed.Status, Is.EqualTo(ServiceStatus.BadRequest));
            Assert.That(malformed.Error, Is.EqualTo("Body is not valid JSON."));
        }

        [Test]
        public async Task Commit_HashMismatch_CarriesBothHashes()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");
            var data = Encoding.UTF8.GetBytes("hello");
            await this.StartHashedUploadAsync("alpha", contentId, data);
            var wrong = new string('a', 64);

            var result = await this.service.CommitAsync(this.Owner, "alpha", contentId, Ticket, new ContentCommitInput(wrong));

            Assert.That(result.Status, Is.EqualTo(ServiceStatus.HashMismatch));
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
            var detail = (ChunkHashMismatch)result.Detail!;
            Assert.That(detail.Expected, Is.EqualTo(wrong));
            Assert.That(detail.Actual, Is.EqualTo(Sha256Hex(data)));
        }

        [Test]
        public async Task Commit_MatchingHash_MarksTheContentReady()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");
            var data = Encoding.UTF8.GetBytes("hello");
            await this.StartHashedUploadAsync("alpha", contentId, data);

            var result = await this.service.CommitAsync(this.Owner, "alpha", contentId, Ticket, new ContentCommitInput(Sha256Hex(data).ToUpperInvariant()));

            Assert.That(result.IsOk, Is.True);
            Assert.That(result.Value!.Hash, Is.EqualTo(Sha256Hex(data)));
            var content = await this.ContentAsync("alpha", contentId);
            Assert.That(content.Status, Is.EqualTo(ContentStatus.Ready));
            Assert.That(content.AccessTicket, Is.Empty);
        }

        private static ContentMetadataCreationInput Attachment()
            => new() { ContentType = "application/pdf", FileName = "report.pdf", IsImage = false };

        private static string Sha256Hex(byte[] data)
            => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        private async Task<string> CreateAttachmentAsync(string secretId)
        {
            var result = await this.service.CreateAsync(this.Owner, secretId, string.Empty, Attachment());
            Assert.That(result.IsOk, Is.True, $"Arrange: attachment creation failed: {result.Error}");
            return result.Value!.ContentName;
        }

        private async Task<ContentMetadata> ContentAsync(string secretId, string contentId)
            => (await this.DbContext.Objects.SingleAsync(o => o.ObjectName == secretId)).Content.Single(c => c.ContentName == contentId);

        private async Task LockAsync(string secretId, string contentId, DateTime setAt)
        {
            var content = await this.ContentAsync(secretId, contentId);
            content.Status = ContentStatus.Updating;
            content.AccessTicket = Ticket;
            content.AccessTicketSetAt = setAt;
            await this.DbContext.SaveChangesAsync();
        }

        private async Task StartHashedUploadAsync(string secretId, string contentId, byte[] data)
        {
            await this.LockAsync(secretId, contentId, FixedNow);
            var running = new SerializableSha256();
            running.Append(data);
            var content = await this.ContentAsync(secretId, contentId);
            content.RunningHashState = running.SaveState();
            await this.DbContext.SaveChangesAsync();
        }
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet build SafeExchange.slnx --nologo -v q`
Expected: FAIL with `error CS0246: The type or namespace name 'ContentService' could not be found` (and
`'ContentCommitInput'`) in `ContentServiceTests.cs`.

- [ ] **Step 3: Create the shared upload-lock helper**

Create `SafeExchange.Core\Services\AccessTicketManager.cs` (the old `TryGetAccessTicketAsync`, identical in
`SafeExchangeSecretContentMeta` and `SafeExchangeSecretStream`, plus the ticket format):

```csharp
/// <summary>
/// AccessTicketManager
/// </summary>

namespace SafeExchange.Core.Services
{
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Purger;
    using System;
    using System.Threading.Tasks;

    /// <summary>
    /// The upload lock on a content item (<see cref="ContentMetadata.AccessTicket"/>), shared by
    /// <see cref="ContentService"/> and <see cref="ChunkService"/>.
    /// </summary>
    internal sealed class AccessTicketManager
    {
        private readonly AccessTicketConfiguration configuration;

        private readonly SafeExchangeDbContext dbContext;

        private readonly IPurger purger;

        private readonly ILogger log;

        public AccessTicketManager(AccessTicketConfiguration configuration, SafeExchangeDbContext dbContext, IPurger purger, ILogger log)
        {
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.purger = purger ?? throw new ArgumentNullException(nameof(purger));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public static string NewTicket() => $"{Guid.NewGuid()}-{Random.Shared.NextInt64():00000000}";

        /// <summary>
        /// Returns the content's current ticket. An expired ticket (when a timeout is configured) is dropped
        /// first, together with all uploaded data, and the content is reset to blank.
        /// </summary>
        public async ValueTask<string> TryGetAccessTicketAsync(ContentMetadata content)
        {
            if (string.IsNullOrEmpty(content.AccessTicket))
            {
                return String.Empty;
            }

            var utcNow = DateTimeProvider.UtcNow;
            if ((this.configuration.AccessTicketTimeout > TimeSpan.Zero)
                && utcNow > (content.AccessTicketSetAt + this.configuration.AccessTicketTimeout))
            {
                this.log.LogInformation($"Access ticket '{content.AccessTicket}' expired (was set at {content.AccessTicketSetAt}) and is to be dropped. All data will be erased.");

                await this.purger.DeleteContentDataAsync(content);

                content.Chunks.Clear();
                content.Status = ContentStatus.Blank;
                content.RunningHashState = null;

                content.AccessTicket = String.Empty;
                content.AccessTicketSetAt = DateTime.MinValue;

                await this.dbContext.SaveChangesAsync();
            }

            return content.AccessTicket;
        }
    }
}
```

- [ ] **Step 4: Create the commit input and `ContentService`**

Create `SafeExchange.Core\Services\ContentCommitInput.cs`:

```csharp
/// <summary>
/// ContentCommitInput
/// </summary>

namespace SafeExchange.Core.Services
{
    /// <summary>
    /// The client's whole-content hash for <see cref="ContentService.CommitAsync"/>.
    /// <see cref="Malformed"/> stands for a request body the host could not parse, which the service
    /// answers in its place in the check order (after the lock and upload-state checks).
    /// </summary>
    public sealed record ContentCommitInput(string? Hash)
    {
        public static ContentCommitInput Malformed { get; } = new((string?)null) { IsMalformed = true };

        public bool IsMalformed { get; private init; }
    }
}
```

Create `SafeExchange.Core\Services\ContentService.cs`. Note the preserved quirks: no `KeepInStorage` check on
create, `.First(...)` (unknown content id → exception → 500) on update/drop/delete, commit's own
`KeepInStorage` message and its lock comparison without expiry handling:

```csharp
/// <summary>
/// ContentService
/// </summary>

namespace SafeExchange.Core.Services
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Crypto;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using System;
    using System.Linq;
    using System.Net;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;

    /// <summary>
    /// Content items of a secret: create, update, drop (clear the data), delete, and commit (finish a
    /// hashed-mode upload). Commit lives here, not in <see cref="ChunkService"/>, because it is a content
    /// state change that needs no blob storage.
    /// </summary>
    public sealed class ContentService
    {
        private const int MaxContentTypeLength = 255;

        private static readonly Regex HexRegex = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

        private readonly Features features;

        private readonly SafeExchangeDbContext dbContext;

        private readonly IPurger purger;

        private readonly IPermissionsManager permissionsManager;

        private readonly IAuditWriter auditWriter;

        private readonly AccessTicketManager accessTickets;

        private readonly ILogger log;

        public ContentService(IConfiguration configuration, SafeExchangeDbContext dbContext, IPurger purger, IPermissionsManager permissionsManager, IAuditWriter auditWriter, ILogger<ContentService> log)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            this.features = new Features();
            configuration.GetSection("Features").Bind(this.features);

            var accessTicketConfiguration = new AccessTicketConfiguration();
            configuration.GetSection("AccessTickets").Bind(accessTicketConfiguration);

            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.purger = purger ?? throw new ArgumentNullException(nameof(purger));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
            this.auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.accessTickets = new AccessTicketManager(accessTicketConfiguration, this.dbContext, this.purger, this.log);
        }

        /// <summary>
        /// Adds a content item. <paramref name="contentId"/> must be empty (REST passes its route value);
        /// <paramref name="input"/> is <c>null</c> when the host could not read it.
        /// </summary>
        public async Task<ServiceResult<ContentMetadataOutput>> CreateAsync(Caller caller, string secretId, string contentId, ContentMetadataCreationInput? input)
        {
            ArgumentNullException.ThrowIfNull(caller);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync<ServiceResult<ContentMetadataOutput>>(async () =>
            {
                var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
                if (existingMetadata == null)
                {
                    this.log.LogInformation($"Cannot create content for secret '{secretId}', as secret not exists.");
                    return ServiceResult.NotFound($"Secret '{secretId}' does not exist.");
                }

                if (!string.IsNullOrEmpty(contentId))
                {
                    this.log.LogInformation($"Cannot create content for secret '{secretId}' with specified name.");
                    return ServiceResult.BadRequest("Cannot specify content name on creation.");
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Write)))
                {
                    return ServiceResult.InsufficientPermissions(PermissionType.Write, secretId);
                }

                if ((input == null) || string.IsNullOrEmpty(input.ContentType))
                {
                    this.log.LogInformation($"Content settings for '{secretId}' not provided.");
                    return ServiceResult.BadRequest("Content settings are not provided.");
                }

                if (!IsAcceptableContentType(input.ContentType))
                {
                    this.log.LogInformation($"Content type for '{secretId}' new content is in incorrect format.");
                    return ServiceResult.BadRequest("Content type is in incorrect format.");
                }

                var newContent = new ContentMetadata(input);
                existingMetadata.Content.Add(newContent);
                existingMetadata.LastAccessedAt = DateTimeProvider.UtcNow;
                await this.dbContext.SaveChangesAsync();

                this.log.LogInformation($"Created content meta for '{secretId}': {newContent.ContentName} ({(newContent.IsMain ? "main" : "not main")}), status: {newContent.Status}.");

                return ServiceResult<ContentMetadataOutput>.Ok(newContent.ToDto());
            });
        }

        /// <summary>
        /// Updates a content item's content type and file name. <paramref name="input"/> is <c>null</c> when
        /// the host could not read it. An unknown <paramref name="contentId"/> throws (REST: 500), as before.
        /// </summary>
        public async Task<ServiceResult<ContentMetadataOutput>> UpdateAsync(Caller caller, string secretId, string contentId, ContentMetadataUpdateInput? input)
        {
            ArgumentNullException.ThrowIfNull(caller);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync<ServiceResult<ContentMetadataOutput>>(async () =>
            {
                var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
                if (existingMetadata == null)
                {
                    this.log.LogInformation($"Cannot update content for secret '{secretId}', as secret not exists.");
                    return ServiceResult.NotFound($"Secret '{secretId}' does not exist.");
                }

                if (!existingMetadata.KeepInStorage)
                {
                    this.log.LogInformation($"The data is not kept in storage, cannot use this api.");
                    return ServiceResult.Unprocessable("Cannot use this endpoint for previous versions data.");
                }

                if (string.IsNullOrEmpty(contentId))
                {
                    this.log.LogInformation($"Cannot update content for secret '{secretId}' without specified name.");
                    return ServiceResult.BadRequest("Must specify content name on update.");
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Write)))
                {
                    return ServiceResult.InsufficientPermissions(PermissionType.Write, secretId);
                }

                if ((input == null) || string.IsNullOrEmpty(input.ContentType))
                {
                    this.log.LogInformation($"Content settings for '{secretId}' content '{contentId}' not provided.");
                    return ServiceResult.BadRequest("Content settings are not provided.");
                }

                if (!IsAcceptableContentType(input.ContentType))
                {
                    this.log.LogInformation($"Content type for '{secretId}' content '{contentId}' is in incorrect format.");
                    return ServiceResult.BadRequest("Content type is in incorrect format.");
                }

                var existingContent = existingMetadata.Content.First(c => c.ContentName.Equals(contentId));
                existingContent.ContentType = input.ContentType;
                existingContent.FileName = input.FileName ?? string.Empty;
                existingMetadata.LastAccessedAt = DateTimeProvider.UtcNow;
                await this.dbContext.SaveChangesAsync();

                this.log.LogInformation($"Updated content meta for '{secretId}': {existingContent.ContentName} ({(existingContent.IsMain ? "main" : "not main")}), status: {existingContent.Status}.");

                return ServiceResult<ContentMetadataOutput>.Ok(existingContent.ToDto());
            });
        }

        /// <summary>
        /// Deletes all uploaded data of a content item and keeps the (now blank) item.
        /// </summary>
        public async Task<ServiceResult<ContentMetadataOutput>> DropAsync(Caller caller, string secretId, string contentId)
        {
            ArgumentNullException.ThrowIfNull(caller);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync<ServiceResult<ContentMetadataOutput>>(async () =>
            {
                var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
                if (existingMetadata == null)
                {
                    this.log.LogInformation($"Cannot drop content for secret '{secretId}', as secret not exists.");
                    return ServiceResult.NotFound($"Secret '{secretId}' does not exist.");
                }

                if (!existingMetadata.KeepInStorage)
                {
                    this.log.LogInformation($"The data is not kept in storage, cannot use this api.");
                    return ServiceResult.Unprocessable("Cannot use this endpoint for previous versions data.");
                }

                if (string.IsNullOrEmpty(contentId))
                {
                    this.log.LogInformation($"Cannot drop content for secret '{secretId}' without specified name.");
                    return ServiceResult.BadRequest("Must specify content name on drop.");
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Write)))
                {
                    return ServiceResult.InsufficientPermissions(PermissionType.Write, secretId);
                }

                var existingContent = existingMetadata.Content.First(c => c.ContentName.Equals(contentId));
                var existingAccessTicket = await this.accessTickets.TryGetAccessTicketAsync(existingContent);
                if (!string.IsNullOrEmpty(existingAccessTicket))
                {
                    this.log.LogInformation($"Content '{contentId}' for secret '{secretId}' status is being changed by other user, cannot drop.");
                    return ServiceResult.Unprocessable("Content is being updated.");
                }

                await this.DeleteAllChunksAsync(existingMetadata, existingContent, false);

                this.log.LogInformation($"Dropped content meta for '{secretId}': {existingContent.ContentName} ({(existingContent.IsMain ? "main" : "not main")}), status: {existingContent.Status}.");

                return ServiceResult<ContentMetadataOutput>.Ok(existingContent.ToDto());
            });
        }

        /// <summary>
        /// Deletes a (non-main) content item and its data.
        /// </summary>
        public async Task<ServiceResult> DeleteAsync(Caller caller, string secretId, string contentId)
        {
            ArgumentNullException.ThrowIfNull(caller);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync(async () =>
            {
                var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
                if (existingMetadata == null)
                {
                    this.log.LogInformation($"Cannot delete content for secret '{secretId}', as secret not exists.");
                    return ServiceResult.NotFound($"Secret '{secretId}' does not exist.");
                }

                if (!existingMetadata.KeepInStorage)
                {
                    this.log.LogInformation($"The data is not kept in storage, cannot use this api.");
                    return ServiceResult.Unprocessable("Cannot use this endpoint for previous versions data.");
                }

                if (string.IsNullOrEmpty(contentId))
                {
                    this.log.LogInformation($"Cannot delete content for secret '{secretId}' without specified name.");
                    return ServiceResult.BadRequest("Must specify content name on deletion.");
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Write)))
                {
                    return ServiceResult.InsufficientPermissions(PermissionType.Write, secretId);
                }

                var existingContent = existingMetadata.Content.First(c => c.ContentName.Equals(contentId));

                if (existingContent.IsMain)
                {
                    this.log.LogInformation($"Cannot delete main content for secret '{secretId}'.");
                    return ServiceResult.BadRequest("Cannot delete main content.");
                }

                var existingAccessTicket = await this.accessTickets.TryGetAccessTicketAsync(existingContent);
                if (!string.IsNullOrEmpty(existingAccessTicket))
                {
                    this.log.LogInformation($"Content '{contentId}' for secret '{secretId}' status is being updated, cannot delete.");
                    return ServiceResult.Unprocessable("Content is being updated.");
                }

                await this.DeleteAllChunksAsync(existingMetadata, existingContent, true);

                this.log.LogInformation($"Deleted content meta for '{secretId}': {existingContent.ContentName} ({(existingContent.IsMain ? "main" : "not main")}), status: {existingContent.Status}.");

                return ServiceResult.Ok();
            });
        }

        /// <summary>
        /// Finishes a hashed-mode upload: checks the upload lock (<paramref name="accessTicket"/>) and the
        /// client's whole-content hash against the server's running hash, then marks the content ready.
        /// </summary>
        public async Task<ServiceResult<ContentCommitOutput>> CommitAsync(Caller caller, string secretId, string contentId, string accessTicket, ContentCommitInput input)
        {
            ArgumentNullException.ThrowIfNull(caller);
            ArgumentNullException.ThrowIfNull(input);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync<ServiceResult<ContentCommitOutput>>(async () =>
            {
                var existingMetadata = await this.dbContext.Objects.FindAsync(secretId);
                if (existingMetadata is null)
                {
                    return ServiceResult.NotFound($"Secret '{secretId}' does not exist.");
                }

                if (!existingMetadata.KeepInStorage)
                {
                    return ServiceResult.Unprocessable("Cannot commit data for previous-version secrets.");
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Write)))
                {
                    return ServiceResult.InsufficientPermissions(PermissionType.Write, secretId);
                }

                var existingContent = existingMetadata.Content.FirstOrDefault(c => c.ContentName.Equals(contentId));
                if (existingContent is null)
                {
                    return ServiceResult.NotFound($"Content '{contentId}' does not exist.");
                }

                if (existingContent.IsMain)
                {
                    return ServiceResult.Unprocessable("Main content does not support explicit commit.");
                }

                // Deliberately no expiry handling here: the lock is compared as stored.
                if (string.IsNullOrEmpty(accessTicket) || !accessTicket.Equals(existingContent.AccessTicket))
                {
                    return ServiceResult.Unauthorized("Access ticket missing or invalid.");
                }

                if (existingContent.RunningHashState is null || existingContent.RunningHashState.Length == 0)
                {
                    return ServiceResult.Failure(ServiceStatus.NoUploadState, HttpStatusCode.UnprocessableEntity, "No hashed-mode upload in progress for this content.");
                }

                if (input.IsMalformed)
                {
                    return ServiceResult.BadRequest("Body is not valid JSON.");
                }

                var clientHash = input.Hash?.ToLowerInvariant() ?? string.Empty;
                if (!HexRegex.IsMatch(clientHash))
                {
                    return ServiceResult.BadRequest("hash must be 64 hex characters.");
                }

                var running = SerializableSha256.Restore(existingContent.RunningHashState);
                var serverHash = Convert.ToHexString(running.Finish()).ToLowerInvariant();

                if (!serverHash.Equals(clientHash))
                {
                    this.log.LogWarning("Commit hash mismatch for '{ContentId}': client {Client}, server {Server}", contentId, clientHash, serverHash);
                    return ServiceResult.Failure(
                        ServiceStatus.HashMismatch, HttpStatusCode.UnprocessableEntity,
                        "Client-asserted whole-content hash does not match server computation.",
                        detail: new ChunkHashMismatch { Expected = clientHash, Actual = serverHash });
                }

                existingContent.Hash = serverHash;
                existingContent.Status = ContentStatus.Ready;
                existingContent.AccessTicket = string.Empty;
                existingContent.AccessTicketSetAt = DateTime.MinValue;
                existingContent.RunningHashState = null;
                existingMetadata.LastAccessedAt = DateTimeProvider.UtcNow;
                await this.dbContext.SaveChangesAsync();

                this.log.LogInformation(
                    "Content commit verified for '{ContentId}' across {ChunkCount} chunk(s) (hash {Hash})",
                    contentId, existingContent.Chunks.Count, serverHash);

                // Audit payload intentionally omits serverHash (plaintext-derived integrity hash).
                // Only opaque identifiers — fileName/contentType — are surfaced.
                await this.auditWriter.AppendAsync(
                    existingMetadata, SecretAuditEventType.ContentCommitted,
                    caller.SubjectType, caller.SubjectId,
                    new { contentId, fileName = existingContent.FileName, contentType = existingContent.ContentType }, this.log);

                return ServiceResult<ContentCommitOutput>.Ok(new ContentCommitOutput { ContentName = contentId, Hash = serverHash });
            });
        }

        private async Task DeleteAllChunksAsync(ObjectMetadata metadata, ContentMetadata content, bool removeContent)
        {
            this.log.LogInformation($"Deleting all chunks for content '{content.ContentName}', {content.Chunks.Count} chunks. {(removeContent ? "Remove content." : "Do not remove content.")}");

            try
            {
                content.Status = ContentStatus.Updating;

                this.log.LogInformation($"Setting access ticket to '{content.ContentName}'.");
                content.AccessTicket = AccessTicketManager.NewTicket();
                content.AccessTicketSetAt = DateTimeProvider.UtcNow;

                await this.dbContext.SaveChangesAsync();

                await this.purger.DeleteContentDataAsync(content);
            }
            catch (Exception exception)
            {
                this.log.LogError(exception, $"Exception inside exclusive operation on content '{content.ContentName}'.");
            }
            finally
            {
                content.Chunks.Clear();
                content.Status = ContentStatus.Blank;
                content.RunningHashState = null;

                this.log.LogInformation($"Clearing access ticket from '{content.ContentName}'.");
                content.AccessTicket = string.Empty;
                content.AccessTicketSetAt = DateTime.MinValue;

                if (removeContent)
                {
                    metadata.Content.Remove(content);
                }

                metadata.LastAccessedAt = DateTimeProvider.UtcNow;

                await this.dbContext.SaveChangesAsync();
            }
        }

        private static bool IsAcceptableContentType(string contentType)
        {
            if (string.IsNullOrEmpty(contentType) || contentType.Length > MaxContentTypeLength)
            {
                return false;
            }

            foreach (var ch in contentType)
            {
                if (char.IsControl(ch))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
```

- [ ] **Step 5: Replace the two handlers with adapters**

Replace the whole content of `SafeExchange.Core.Functions\Functions\SafeExchangeSecretContentMeta.cs` with (the
file header keeps its old `SafeExchangeSecretMeta` text, as before):

```csharp
/// <summary>
/// SafeExchangeSecretMeta
/// </summary>

namespace SafeExchange.Core.Functions
{
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using System.Security.Claims;
    using System;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging.Abstractions;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Services;
    using SafeExchange.Core.Telemetry;
    using Microsoft.Azure.Functions.Worker.Http;
    using System.Net;

    /// <summary>
    /// HTTP adapter for content items (<c>/secret/{secretId}/content[/{contentId}[/drop]]</c>) over <see cref="ContentService"/>.
    /// </summary>
    public class SafeExchangeSecretContentMeta
    {
        private readonly ICallerResolver callerResolver;

        private readonly ContentService contentService;

        public SafeExchangeSecretContentMeta(ICallerResolver callerResolver, ContentService contentService)
        {
            this.callerResolver = callerResolver ?? throw new ArgumentNullException(nameof(callerResolver));
            this.contentService = contentService ?? throw new ArgumentNullException(nameof(contentService));
        }

        /// <summary>
        /// Pre-service constructor, kept for code that builds handlers by hand: resolves callers through
        /// <paramref name="globalFilters"/> exactly as before and builds the service itself (without logging).
        /// </summary>
        public SafeExchangeSecretContentMeta(IConfiguration configuration, SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, IPurger purger, IPermissionsManager permissionsManager, IAuditWriter auditWriter)
            : this(
                  new GlobalFiltersCallerResolver(globalFilters, tokenHelper, dbContext),
                  new ContentService(configuration, dbContext, purger, permissionsManager, auditWriter, NullLogger<ContentService>.Instance))
        {
        }

        public async Task<HttpResponseData> Run(
            HttpRequestData request,
            string secretId, string contentId, ClaimsPrincipal principal, ILogger log)
        {
            var resolution = await this.callerResolver.ResolveAsync(principal, CallerChannels.Rest, null);
            if (!resolution.IsAllowed)
            {
                return await resolution.Denial.ToResponseAsync(request);
            }

            var caller = resolution.Caller;
            log.LogInformation($"{nameof(SafeExchangeSecretContentMeta)} triggered for '{secretId}' by {caller.SubjectType} (tid {TelemetryContext.Current}) [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "post":
                    return await ActionResults.ServiceCallAsync(request, async () =>
                    {
                        var input = await ReadInputAsync<ContentMetadataCreationInput>(request, secretId, log);
                        return await (await this.contentService.CreateAsync(caller, secretId, contentId, input)).ToResponseAsync(request);
                    }, "HandleSecretContentMetaCreation", log);

                case "patch":
                    return await ActionResults.ServiceCallAsync(request, async () =>
                    {
                        var input = await ReadInputAsync<ContentMetadataUpdateInput>(request, secretId, log);
                        return await (await this.contentService.UpdateAsync(caller, secretId, contentId, input)).ToResponseAsync(request);
                    }, "HandleSecretContentMetaDeletion", log);

                case "delete":
                    return await ActionResults.ServiceCallAsync(
                        request,
                        async () => await (await this.contentService.DeleteAsync(caller, secretId, contentId)).ToResponseAsync(request),
                        "HandleSecretContentMetaDeletion", log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        public async Task<HttpResponseData> RunDrop(
            HttpRequestData request,
            string secretId, string contentId, ClaimsPrincipal principal, ILogger log)
        {
            var resolution = await this.callerResolver.ResolveAsync(principal, CallerChannels.Rest, null);
            if (!resolution.IsAllowed)
            {
                return await resolution.Denial.ToResponseAsync(request);
            }

            var caller = resolution.Caller;
            log.LogInformation($"{nameof(SafeExchangeSecretContentMeta)} triggered for '{secretId}' by {caller.SubjectType} (tid {TelemetryContext.Current}) [DROP ({request.Method})].");

            switch (request.Method.ToLower())
            {
                case "patch":
                    return await ActionResults.ServiceCallAsync(
                        request,
                        async () => await (await this.contentService.DropAsync(caller, secretId, contentId)).ToResponseAsync(request),
                        "HandleSecretContentMetaDeletion", log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        private static async Task<T?> ReadInputAsync<T>(HttpRequestData request, string secretId, ILogger log) where T : class
        {
            var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            try
            {
                return DefaultJsonSerializer.Deserialize<T>(requestBody);
            }
            catch
            {
                log.LogInformation($"Could not parse input data for '{secretId}' new content.");
                return null;
            }
        }
    }
}
```

Replace the whole content of `SafeExchange.Core.Functions\Functions\SafeExchangeContentCommit.cs` with:

```csharp
/// <summary>
/// SafeExchangeContentCommit
/// </summary>

namespace SafeExchange.Core.Functions
{
    using System;
    using System.Linq;
    using System.Security.Claims;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Services;
    using SafeExchange.Core.Telemetry;

    /// <summary>
    /// HTTP adapter for <c>PATCH /secret/{secretId}/content/{contentId}/commit</c> over <see cref="ContentService.CommitAsync"/>.
    /// </summary>
    public class SafeExchangeContentCommit
    {
        private readonly ICallerResolver callerResolver;

        private readonly ContentService contentService;

        public SafeExchangeContentCommit(ICallerResolver callerResolver, ContentService contentService)
        {
            this.callerResolver = callerResolver ?? throw new ArgumentNullException(nameof(callerResolver));
            this.contentService = contentService ?? throw new ArgumentNullException(nameof(contentService));
        }

        /// <summary>
        /// Pre-service constructor, kept for code that builds handlers by hand: resolves callers through
        /// <paramref name="globalFilters"/> exactly as before and builds the service itself (without logging).
        /// </summary>
        public SafeExchangeContentCommit(IConfiguration configuration, SafeExchangeDbContext dbContext,
            ITokenHelper tokenHelper, GlobalFilters globalFilters, IPurger purger, IPermissionsManager permissionsManager, IAuditWriter auditWriter)
            : this(
                  new GlobalFiltersCallerResolver(globalFilters, tokenHelper, dbContext),
                  new ContentService(configuration, dbContext, purger, permissionsManager, auditWriter, NullLogger<ContentService>.Instance))
        {
        }

        public async Task<HttpResponseData> Run(HttpRequestData request, string secretId, string contentId,
            ClaimsPrincipal principal, ILogger log)
        {
            var resolution = await this.callerResolver.ResolveAsync(principal, CallerChannels.Rest, null);
            if (!resolution.IsAllowed)
            {
                return await resolution.Denial.ToResponseAsync(request);
            }

            var caller = resolution.Caller;
            log.LogInformation($"{nameof(SafeExchangeContentCommit)} triggered for '{secretId}' ({contentId}) by {caller.SubjectType} (tid {TelemetryContext.Current}).");

            return await ActionResults.ServiceCallAsync(request, async () =>
            {
                var ticketHeader = request.Headers.TryGetValues(SafeExchangeSecretStream.AccessTicketHeaderName, out var tickets)
                    ? tickets.FirstOrDefault() ?? string.Empty
                    : string.Empty;

                ContentCommitInput input;
                try
                {
                    var payload = await JsonSerializer.DeserializeAsync<CommitRequest>(request.Body,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    input = new ContentCommitInput(payload?.Hash);
                }
                catch (JsonException)
                {
                    input = ContentCommitInput.Malformed;
                }

                return await (await this.contentService.CommitAsync(caller, secretId, contentId, ticketHeader, input)).ToResponseAsync(request);
            }, nameof(SafeExchangeContentCommit), log);
        }

        private sealed class CommitRequest
        {
            public string? Hash { get; set; }
        }
    }
}
```

- [ ] **Step 6: Run the service tests**

Run: `dotnet build SafeExchange.slnx --nologo -v q; dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchange.Tests.Services"`
Expected: `0 Error(s)` and `Passed!  - Failed:     0, Passed:    50` (41 + 9).

- [ ] **Step 7: Run the phase filter**

Expected: `Passed!  - Failed:     0, Passed:   318` (268 + 50).

- [ ] **Step 8: Commit**

```powershell
git add SafeExchange.Core/Services/AccessTicketManager.cs SafeExchange.Core/Services/ContentCommitInput.cs SafeExchange.Core/Services/ContentService.cs SafeExchange.Core.Functions/Functions/SafeExchangeSecretContentMeta.cs SafeExchange.Core.Functions/Functions/SafeExchangeContentCommit.cs SafeExchange.Tests/Services/ContentServiceTests.cs
git --no-pager diff --cached --name-status
```
Expected exactly:
```text
M	SafeExchange.Core.Functions/Functions/SafeExchangeContentCommit.cs
M	SafeExchange.Core.Functions/Functions/SafeExchangeSecretContentMeta.cs
A	SafeExchange.Core/Services/AccessTicketManager.cs
A	SafeExchange.Core/Services/ContentCommitInput.cs
A	SafeExchange.Core/Services/ContentService.cs
A	SafeExchange.Tests/Services/ContentServiceTests.cs
```

```powershell
git commit -m "refactor(core): extract ContentService" -m "Content item create/update/drop/delete (SafeExchangeSecretContentMeta) and the hashed-upload commit (SafeExchangeContentCommit) move into Core's ContentService; commit lives here because it changes content state without blob access. The expiry-aware upload-lock lookup shared with chunk uploads becomes AccessTicketManager. Both handlers become HTTP adapters; an unreadable commit body is passed as ContentCommitInput.Malformed so it is still answered after the lock checks." -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 4: `ChunkService` and the `SafeExchangeSecretStream` adapter

**Files:**
- Create: `SafeExchange.Tests\Services\ChunkServiceTests.cs`
- Create: `SafeExchange.Core\Services\ChunkUploadRequest.cs`
- Create: `SafeExchange.Core\Services\MainContentSanitizer.cs`
- Create: `SafeExchange.Core\Services\ChunkService.cs`
- Replace: `SafeExchange.Core.Functions\Functions\SafeExchangeSecretStream.cs`

- [ ] **Step 1: Write the failing tests**

Create `SafeExchange.Tests\Services\ChunkServiceTests.cs`. It covers the permission denials, a hashed
upload → commit → download round trip, the deferred download (bytes and the `ContentRead` audit event only when
written; a storage failure while writing is an operation failure) and `ExpectedIndex` (D7):

```csharp
/// <summary>
/// ChunkServiceTests
/// </summary>

namespace SafeExchange.Tests.Services
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging.Abstractions;
    using NUnit.Framework;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Services;
    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading.Tasks;

    [TestFixture]
    public class ChunkServiceTests : ServiceTestBase
    {
        private static readonly byte[] Hello = Encoding.UTF8.GetBytes("hello");

        private static readonly byte[] World = Encoding.UTF8.GetBytes("world");

        private ChunkService service = null!;

        private ContentService contentService = null!;

        [SetUp]
        public void SetUp()
        {
            this.service = new ChunkService(this.Configuration, this.DbContext, this.Purger, this.BlobHelper, this.PermissionsManager, this.AuditWriter, NullLogger<ChunkService>.Instance);
            this.contentService = new ContentService(this.Configuration, this.DbContext, this.Purger, this.PermissionsManager, this.AuditWriter, NullLogger<ContentService>.Instance);
        }

        [Test]
        public async Task EveryMethod_DeniesACallerWithoutThePermission()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, PermissionType.Read);

            AssertInsufficientPermissions((await this.Upload(this.Reader, contentId, Hello)).Outcome, PermissionType.Write, "alpha");
            AssertInsufficientPermissions((await this.service.DownloadChunkAsync(this.Stranger, "alpha", contentId, $"{contentId}-00000000")).Outcome, PermissionType.Read, "alpha");
            AssertInsufficientPermissions((await this.service.DownloadContentAsync(this.Stranger, "alpha", contentId)).Outcome, PermissionType.Read, "alpha");
            Assert.That(this.BlobHelper.Blobs, Is.Empty);
        }

        [Test]
        public async Task HashedUpload_Commit_AndDownload_RoundTrip()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");

            var first = await this.Upload(this.Owner, contentId, Hello, hashed: true, expectedIndex: 0);
            var second = await this.Upload(this.Owner, contentId, World, hashed: true, ticket: first.Value!.AccessTicket, expectedIndex: 1);
            var whole = Hello.Concat(World).ToArray();
            var commit = await this.contentService.CommitAsync(this.Owner, "alpha", contentId, first.Value!.AccessTicket!, new ContentCommitInput(Sha256Hex(whole)));
            var chunk = await this.service.DownloadChunkAsync(this.Owner, "alpha", contentId, second.Value!.ChunkName);
            var content = await this.service.DownloadContentAsync(this.Owner, "alpha", contentId);

            Assert.That(second.Value!.Hash, Is.EqualTo(Sha256Hex(World)));
            Assert.That(commit.IsOk, Is.True, commit.Error);
            Assert.That(chunk.Value!.ContentLength, Is.EqualTo(World.Length));
            Assert.That(chunk.Value!.ContentDisposition, Is.Null);
            Assert.That(await ReadAllAsync(chunk.Value!), Is.EqualTo(World));
            Assert.That(content.Value!.ContentType, Is.EqualTo("application/pdf"));
            Assert.That(content.Value!.ContentLength, Is.EqualTo(whole.Length));
            Assert.That(content.Value!.ContentDisposition, Is.EqualTo("attachment; filename=report.pdf"));
            Assert.That(await ReadAllAsync(content.Value!), Is.EqualTo(whole));
        }

        [Test]
        public async Task Download_ReadsAndAuditsOnlyWhenWritten()
        {
            await this.CreateSecretAsync("alpha", auditEnabled: true);
            var contentId = await this.CreateAttachmentAsync("alpha");
            var upload = await this.Upload(this.Owner, contentId, Hello);
            Assert.That(upload.Value!.AccessTicket, Is.Empty, "Arrange: a final legacy chunk releases the lock.");

            var download = await this.service.DownloadChunkAsync(this.Owner, "alpha", contentId, upload.Value!.ChunkName);
            var readsBefore = await this.ContentReadEventsAsync();
            var bytes = await ReadAllAsync(download.Value!);
            var readsAfter = await this.ContentReadEventsAsync();

            Assert.That(bytes, Is.EqualTo(Hello));
            Assert.That(readsBefore, Is.EqualTo(0));
            Assert.That(readsAfter, Is.EqualTo(1));
        }

        [Test]
        public async Task Download_StorageFailureWhileWriting_IsAnOperationFailure()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");
            var upload = await this.Upload(this.Owner, contentId, Hello);
            this.BlobHelper.Blobs.Clear();

            var download = await this.service.DownloadChunkAsync(this.Owner, "alpha", contentId, upload.Value!.ChunkName);

            Assert.That(download.IsOk, Is.True);
            Assert.ThrowsAsync<ServiceOperationException>(() => download.Value!.WriteToAsync(new MemoryStream()));
        }

        [Test]
        public async Task ExpectedIndex_RetryOfAStoredChunkWithTheSameHash_IsANoOp()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");
            var first = await this.Upload(this.Owner, contentId, Hello, hashed: true, expectedIndex: 0);

            var retry = await this.Upload(this.Owner, contentId, Hello, hashed: true, ticket: first.Value!.AccessTicket, expectedIndex: 0);

            Assert.That(retry.IsOk, Is.True, retry.Error);
            Assert.That(retry.Value!.ChunkName, Is.EqualTo(first.Value!.ChunkName));
            Assert.That(retry.Value!.Hash, Is.EqualTo(Sha256Hex(Hello)));
            Assert.That(retry.Value!.AccessTicket, Is.EqualTo(first.Value!.AccessTicket));
            Assert.That((await this.ContentAsync(contentId)).Chunks, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task ExpectedIndex_NotTheNextIndex_IsUnprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");
            var first = await this.Upload(this.Owner, contentId, Hello, hashed: true, expectedIndex: 0);

            var skipped = await this.Upload(this.Owner, contentId, World, hashed: true, ticket: first.Value!.AccessTicket, expectedIndex: 2);
            var changed = await this.Upload(this.Owner, contentId, World, hashed: true, ticket: first.Value!.AccessTicket, expectedIndex: 0);

            foreach (var result in new[] { skipped, changed })
            {
                Assert.That(result.Status, Is.EqualTo(ServiceStatus.Unprocessable));
                Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
                Assert.That(result.Error, Is.EqualTo("Expected chunk index 1."));
            }

            Assert.That((await this.ContentAsync(contentId)).Chunks, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task ExpectedIndex_Mismatch_DoesNotTakeTheLock()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");

            var result = await this.Upload(this.Owner, contentId, Hello, hashed: true, expectedIndex: 3);

            Assert.That(result.Error, Is.EqualTo("Expected chunk index 0."));
            var content = await this.ContentAsync(contentId);
            Assert.That(content.AccessTicket, Is.Empty);
            Assert.That(content.Status, Is.EqualTo(ContentStatus.Blank));
        }

        private static string Sha256Hex(byte[] data)
            => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        private static async Task<byte[]> ReadAllAsync(ContentDownload download)
        {
            using var buffer = new MemoryStream();
            await download.WriteToAsync(buffer);
            return buffer.ToArray();
        }

        private Task<ServiceResult<ChunkCreationOutput>> Upload(
            Caller caller, string contentId, byte[] data, bool hashed = false, string? ticket = null, int? expectedIndex = null)
            => this.service.UploadChunkAsync(caller, "alpha", contentId, new MemoryStream(data), new ChunkUploadRequest
            {
                AccessTicket = ticket ?? string.Empty,
                ChunkHash = hashed ? Sha256Hex(data) : null,
                DeclaredLength = hashed ? data.Length : 0,
                ExpectedIndex = expectedIndex,
            });

        private async Task<string> CreateAttachmentAsync(string secretId)
        {
            var input = new ContentMetadataCreationInput { ContentType = "application/pdf", FileName = "report.pdf", IsImage = false };
            var result = await this.contentService.CreateAsync(this.Owner, secretId, string.Empty, input);
            Assert.That(result.IsOk, Is.True, $"Arrange: attachment creation failed: {result.Error}");
            return result.Value!.ContentName;
        }

        private async Task<ContentMetadata> ContentAsync(string contentId)
            => (await this.DbContext.Objects.SingleAsync(o => o.ObjectName == "alpha")).Content.Single(c => c.ContentName == contentId);

        private Task<int> ContentReadEventsAsync()
            => this.DbContext.SecretAuditEvents.AsNoTracking().CountAsync(e => e.EventType == SecretAuditEventType.ContentRead);
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet build SafeExchange.slnx --nologo -v q`
Expected: FAIL with `error CS0246: The type or namespace name 'ChunkService' could not be found` (and
`'ChunkUploadRequest'`) in `ChunkServiceTests.cs`.

- [ ] **Step 3: Create the upload request and the sanitizer policy**

Create `SafeExchange.Core\Services\ChunkUploadRequest.cs`:

```csharp
/// <summary>
/// ChunkUploadRequest
/// </summary>

namespace SafeExchange.Core.Services
{
    /// <summary>
    /// Everything about a chunk upload other than its bytes. REST fills it from the route and the
    /// <c>X-SafeExchange-*</c> / <c>Content-Length</c> headers.
    /// </summary>
    public sealed class ChunkUploadRequest
    {
        /// <summary>
        /// Must be empty: the server names chunks. REST passes its route value.
        /// </summary>
        public string ChunkId { get; init; } = string.Empty;

        /// <summary>
        /// The upload lock returned by the first chunk; empty for the first chunk.
        /// </summary>
        public string AccessTicket { get; init; } = string.Empty;

        /// <summary>
        /// <c>interim</c> keeps a legacy (unhashed) upload open after this chunk; anything else finishes it.
        /// </summary>
        public string OperationType { get; init; } = string.Empty;

        /// <summary>
        /// The client's SHA-256 (hex) of this chunk; selects the hashed upload mode for attachments.
        /// </summary>
        public string? ChunkHash { get; init; }

        /// <summary>
        /// The chunk length recorded for hashed uploads (REST: the <c>Content-Length</c> header, 0 if absent).
        /// </summary>
        public long DeclaredLength { get; init; }

        /// <summary>
        /// Optional resume guard (MCP). When set: a chunk already stored at this index with the same
        /// <see cref="ChunkHash"/> is answered as a successful no-op; otherwise the content's chunk count must
        /// equal it. REST leaves it <c>null</c>.
        /// </summary>
        public int? ExpectedIndex { get; init; }
    }
}
```

Create `SafeExchange.Core\Services\MainContentSanitizer.cs` (the `HtmlSanitizer` set-up from
`UploadMainContentAsync`, unchanged):

```csharp
/// <summary>
/// MainContentSanitizer
/// </summary>

namespace SafeExchange.Core.Services
{
    using Ganss.Xss;

    /// <summary>
    /// The HTML policy applied to a secret's main (rich text) content on upload.
    /// </summary>
    internal static class MainContentSanitizer
    {
        public static string Sanitize(string content)
        {
            var sanitizer = new HtmlSanitizer();
            sanitizer.AllowedSchemes.Add("data");
            sanitizer.AllowedAttributes.Add("class");
            sanitizer.AllowedAttributes.Add("data-value");
            sanitizer.AllowedAttributes.Add("data-bs-toggle");
            sanitizer.AllowedAttributes.Add("data-bs-placement");
            sanitizer.AllowedAttributes.Add("title");

            // images-as-attachments spike: keep inline-image geometry/alignment
            // (blotFormatter2 writes width/style) and the attachment reference marker
            // so a stored <img data-saex-attachment="..."> survives upload sanitizing.
            sanitizer.AllowedAttributes.Add("style");
            sanitizer.AllowedAttributes.Add("width");
            sanitizer.AllowedAttributes.Add("height");
            sanitizer.AllowedAttributes.Add("alt");
            sanitizer.AllowedAttributes.Add("data-saex-attachment");

            return sanitizer.Sanitize(content);
        }
    }
}
```

- [ ] **Step 4: Create `ChunkService`**

Create `SafeExchange.Core\Services\ChunkService.cs`. Preserved quirks: an `inconsistent_upload_mode` rejection
happens after a new lock was taken (as today); a final legacy chunk returns `accessTicket: ""`; the whole-content
download says `"Content '{id}' not exists."` while the chunk download says `"does not exist."`; the hashed
chunk length is the declared `Content-Length`:

```csharp
/// <summary>
/// ChunkService
/// </summary>

namespace SafeExchange.Core.Services
{
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Crypto;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Streams;
    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Mime;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Content bytes: chunk upload (legacy and hashed mode), chunk download and whole-content download.
    /// </summary>
    public sealed class ChunkService
    {
        /// <summary>
        /// <see cref="ChunkUploadRequest.OperationType"/> value that keeps a legacy upload open.
        /// </summary>
        public const string InterimOperationType = "interim";

        private const string DefaultContentType = "text/html; charset=utf-8";

        private readonly Features features;

        private readonly SafeExchangeDbContext dbContext;

        private readonly IPurger purger;

        private readonly IBlobHelper blobHelper;

        private readonly IPermissionsManager permissionsManager;

        private readonly IAuditWriter auditWriter;

        private readonly AccessTicketManager accessTickets;

        private readonly ILogger log;

        public ChunkService(IConfiguration configuration, SafeExchangeDbContext dbContext, IPurger purger, IBlobHelper blobHelper, IPermissionsManager permissionsManager, IAuditWriter auditWriter, ILogger<ChunkService> log)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            this.features = new Features();
            configuration.GetSection("Features").Bind(this.features);

            var accessTicketConfiguration = new AccessTicketConfiguration();
            configuration.GetSection("AccessTickets").Bind(accessTicketConfiguration);

            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.purger = purger ?? throw new ArgumentNullException(nameof(purger));
            this.blobHelper = blobHelper ?? throw new ArgumentNullException(nameof(blobHelper));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
            this.auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.accessTickets = new AccessTicketManager(accessTicketConfiguration, this.dbContext, this.purger, this.log);
        }

        /// <summary>
        /// Appends <paramref name="data"/> as the next chunk of a content item. The first chunk takes the
        /// upload lock and returns it as <see cref="ChunkCreationOutput.AccessTicket"/>.
        /// </summary>
        public async Task<ServiceResult<ChunkCreationOutput>> UploadChunkAsync(Caller caller, string secretId, string contentId, Stream data, ChunkUploadRequest request)
        {
            ArgumentNullException.ThrowIfNull(caller);
            ArgumentNullException.ThrowIfNull(data);
            ArgumentNullException.ThrowIfNull(request);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync<ServiceResult<ChunkCreationOutput>>(async () =>
            {
                var existingMetadata = await this.dbContext.Objects.FindAsync(secretId);
                if (existingMetadata == null)
                {
                    this.log.LogInformation($"Cannot upload content for secret '{secretId}', as secret not exists.");
                    return ServiceResult.NotFound($"Secret '{secretId}' does not exist.");
                }

                if (!existingMetadata.KeepInStorage)
                {
                    this.log.LogInformation($"The data is not kept in storage, cannot use this api.");
                    return ServiceResult.Unprocessable("Cannot use this endpoint for previous versions data.");
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Write)))
                {
                    return ServiceResult.InsufficientPermissions(PermissionType.Write, secretId);
                }

                var existingContent = existingMetadata.Content.FirstOrDefault(c => c.ContentName.Equals(contentId));
                if (existingContent == null)
                {
                    this.log.LogInformation($"Cannot upload content '{contentId}' for secret '{secretId}', as content not exists.");
                    return ServiceResult.NotFound($"Content '{contentId}' does not exist.");
                }

                if (!string.IsNullOrEmpty(request.ChunkId))
                {
                    this.log.LogInformation($"Cannot upload content for secret '{secretId}' with specified chunk Id.");
                    return ServiceResult.BadRequest($"Cannot specify chunk id on upload.");
                }

                var accessTicket = request.AccessTicket ?? string.Empty;
                var existingAccessTicket = await this.accessTickets.TryGetAccessTicketAsync(existingContent);
                if (!string.IsNullOrEmpty(existingAccessTicket) && !existingAccessTicket.Equals(accessTicket))
                {
                    this.log.LogInformation($"Cannot upload content for secret '{secretId}', is being changed by other user.");
                    return ServiceResult.Unprocessable("Content is being updated.");
                }

                if (request.ExpectedIndex is int expectedIndex)
                {
                    var chunkCount = existingContent.Chunks.Count;
                    if (expectedIndex >= 0 && expectedIndex < chunkCount
                        && !string.IsNullOrEmpty(request.ChunkHash)
                        && existingContent.Chunks[expectedIndex].Hash.Equals(request.ChunkHash, StringComparison.OrdinalIgnoreCase))
                    {
                        var storedChunk = existingContent.Chunks[expectedIndex];
                        this.log.LogInformation($"Chunk {expectedIndex} of '{contentId}' is already stored with the same hash, nothing to do.");
                        return ServiceResult<ChunkCreationOutput>.Ok(storedChunk.ToCreationDto(accessTicket));
                    }

                    if (expectedIndex != chunkCount)
                    {
                        return ServiceResult.Unprocessable($"Expected chunk index {chunkCount}.");
                    }
                }

                if (string.IsNullOrEmpty(accessTicket))
                {
                    this.log.LogInformation($"Setting access ticket to '{existingContent.ContentName}'.");
                    accessTicket = AccessTicketManager.NewTicket();

                    existingContent.Status = ContentStatus.Updating;
                    existingContent.AccessTicket = accessTicket;
                    existingContent.AccessTicketSetAt = DateTimeProvider.UtcNow;

                    await this.dbContext.SaveChangesAsync();
                }

                var newChunkName = $"{existingContent.ContentName}-{(existingContent.Chunks.Count):00000000}";

                // Main HTML content always flows through the sanitising legacy path —
                // a hash header on main content is ignored (would otherwise bypass sanitisation).
                var hashHeaderPresent = !existingContent.IsMain && !string.IsNullOrEmpty(request.ChunkHash);

                var mode = UploadModeResolver.Resolve(
                    existingContent,
                    hashHeaderPresent: hashHeaderPresent,
                    allowLegacy: this.features.AllowLegacyAttachmentUploads,
                    ignoreHeader: this.features.IgnoreChunkHashHeader || existingContent.IsMain);

                switch (mode)
                {
                    case UploadMode.Reject:
                        return ServiceResult.Failure(
                            ServiceStatus.InconsistentUploadMode, HttpStatusCode.BadRequest,
                            "Chunk-hash header usage is inconsistent with the content's upload mode.");

                    case UploadMode.Hashed:
                        return await this.UploadHashedChunkAsync(
                            caller, data, existingContent, newChunkName, existingMetadata,
                            request.ChunkHash!, request.DeclaredLength, accessTicket);

                    case UploadMode.Legacy:
                        return await this.UploadLegacyChunkAsync(
                            caller, data, existingContent, newChunkName, existingMetadata,
                            request.OperationType ?? string.Empty, accessTicket);
                }

                throw new InvalidOperationException("Unreachable.");
            });
        }

        /// <summary>
        /// Downloads one chunk of a ready content item. The bytes are read, and the read audited, when the
        /// returned <see cref="ContentDownload"/> is written.
        /// </summary>
        public async Task<ServiceResult<ContentDownload>> DownloadChunkAsync(Caller caller, string secretId, string contentId, string chunkId)
        {
            ArgumentNullException.ThrowIfNull(caller);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync<ServiceResult<ContentDownload>>(async () =>
            {
                var (failure, existingMetadata, existingContent) = await this.FindReadableContentAsync(caller, secretId, contentId, $"Content '{contentId}' does not exist.");
                if (failure is not null)
                {
                    return failure;
                }

                if (string.IsNullOrEmpty(chunkId))
                {
                    this.log.LogInformation($"Cannot download content for secret '{secretId}' without specified chunk Id.");
                    return ServiceResult.BadRequest($"Must specify chunk id on download.");
                }

                var existingChunk = existingContent!.Chunks.FirstOrDefault(c => c.ChunkName.Equals(chunkId));
                if (existingChunk == null)
                {
                    this.log.LogInformation($"Cannot download content for secret '{secretId}', as chunk '{chunkId}' for content '{contentId}' not exists.");
                    return ServiceResult.NotFound($"Chunk '{chunkId}' does not exist.");
                }

                var download = new ContentDownload(
                    ContentTypeOf(existingContent),
                    existingChunk.Length,
                    null,
                    (destination, cancellationToken) => ServiceOperation.RunAsync(async () =>
                    {
                        var dataStream = await this.blobHelper.DownloadAndDecryptBlobAsync(existingChunk.ChunkName);
                        await dataStream.CopyToAsync(destination, cancellationToken);

                        existingMetadata!.LastAccessedAt = DateTimeProvider.UtcNow;
                        await this.dbContext.SaveChangesAsync(cancellationToken);

                        await this.EmitContentReadAsync(existingMetadata, existingContent.ContentName, existingChunk.ChunkName, caller);
                    }));

                return ServiceResult<ContentDownload>.Ok(download);
            });
        }

        /// <summary>
        /// Downloads a ready content item as one stream (all chunks in order, one audited read per chunk).
        /// </summary>
        public async Task<ServiceResult<ContentDownload>> DownloadContentAsync(Caller caller, string secretId, string contentId)
        {
            ArgumentNullException.ThrowIfNull(caller);
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext, this.auditWriter, this.features.AuditRetentionDays);

            return await ServiceOperation.RunAsync<ServiceResult<ContentDownload>>(async () =>
            {
                var (failure, existingMetadata, existingContent) = await this.FindReadableContentAsync(caller, secretId, contentId, $"Content '{contentId}' not exists.");
                if (failure is not null)
                {
                    return failure;
                }

                var contentLength = 0L;
                foreach (var chunk in existingContent!.Chunks)
                {
                    contentLength += chunk.Length;
                }

                var download = new ContentDownload(
                    ContentTypeOf(existingContent),
                    contentLength,
                    new ContentDisposition { FileName = existingContent.FileName }.ToString(),
                    (destination, cancellationToken) => ServiceOperation.RunAsync(async () =>
                    {
                        foreach (var chunk in existingContent.Chunks)
                        {
                            var chunkStream = await this.blobHelper.DownloadAndDecryptBlobAsync(chunk.ChunkName);
                            await chunkStream.CopyToAsync(destination, cancellationToken);
                            await this.EmitContentReadAsync(existingMetadata!, existingContent.ContentName, chunk.ChunkName, caller);
                        }

                        existingMetadata!.LastAccessedAt = DateTimeProvider.UtcNow;
                        await this.dbContext.SaveChangesAsync(cancellationToken);
                    }));

                return ServiceResult<ContentDownload>.Ok(download);
            });
        }

        private static string ContentTypeOf(ContentMetadata content)
            => string.IsNullOrEmpty(content.ContentType) ? DefaultContentType : content.ContentType;

        // Shared download checks, in today's order; only the content-missing message differs per endpoint.
        private async Task<(ServiceResult? Failure, ObjectMetadata? Metadata, ContentMetadata? Content)> FindReadableContentAsync(
            Caller caller, string secretId, string contentId, string contentMissingError)
        {
            var existingMetadata = await this.dbContext.Objects.FindAsync(secretId);
            if (existingMetadata == null)
            {
                this.log.LogInformation($"Cannot download content for secret '{secretId}', as secret not exists.");
                return (ServiceResult.NotFound($"Secret '{secretId}' does not exist."), null, null);
            }

            if (!existingMetadata.KeepInStorage)
            {
                this.log.LogInformation($"The data is not kept in storage, cannot use this api.");
                return (ServiceResult.Unprocessable("Cannot use this endpoint for previous versions data."), null, null);
            }

            if (!(await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Read)))
            {
                return (ServiceResult.InsufficientPermissions(PermissionType.Read, secretId), null, null);
            }

            var existingContent = existingMetadata.Content.FirstOrDefault(c => c.ContentName.Equals(contentId));
            if (existingContent == null)
            {
                this.log.LogInformation($"Cannot download content '{contentId}' for secret '{secretId}', as content not exists.");
                return (ServiceResult.NotFound(contentMissingError), null, null);
            }

            var existingAccessTicket = await this.accessTickets.TryGetAccessTicketAsync(existingContent);
            if (!string.IsNullOrEmpty(existingAccessTicket))
            {
                this.log.LogInformation($"Cannot download content '{contentId}' for secret '{secretId}', as content is being updated by other user.");
                return (ServiceResult.Unprocessable($"Content '{contentId}' is being updated."), null, null);
            }

            if (existingContent.Status != ContentStatus.Ready)
            {
                this.log.LogInformation($"Cannot download content '{contentId}' for secret '{secretId}', as content is not ready.");
                return (ServiceResult.Unprocessable($"Content '{contentId}' is not ready."), null, null);
            }

            return (null, existingMetadata, existingContent);
        }

        private async Task<ServiceResult<ChunkCreationOutput>> UploadLegacyChunkAsync(
            Caller caller,
            Stream dataStream,
            ContentMetadata existingContent,
            string newChunkName,
            ObjectMetadata existingMetadata,
            string operationType,
            string accessTicket)
        {
            var dataLength = 0L;
            if (existingContent.IsMain)
            {
                dataLength = await this.UploadMainContentAsync(newChunkName, dataStream);
            }
            else
            {
                await this.blobHelper.EncryptAndUploadBlobAsync(newChunkName, dataStream);

                try
                {
                    dataLength = dataStream.Length;
                }
                catch (NotSupportedException exception)
                {
                    this.log.LogWarning(exception, $"Cannot get content length for '{existingContent.ContentName}': '{newChunkName}'.");
                }
            }

            var newChunk = new ChunkMetadata()
            {
                ChunkName = newChunkName,
                Hash = string.Empty,
                Length = dataLength,
            };

            existingContent.Chunks.Add(newChunk);

            if (InterimOperationType.Equals(operationType, StringComparison.InvariantCultureIgnoreCase))
            {
                existingContent.AccessTicketSetAt = DateTimeProvider.UtcNow;
            }
            else
            {
                this.log.LogInformation($"Clearing access ticket from '{existingContent.ContentName}'.");
                accessTicket = string.Empty;
                existingContent.Status = ContentStatus.Ready;
                existingContent.AccessTicket = string.Empty;
                existingContent.AccessTicketSetAt = DateTime.MinValue;
            }

            existingMetadata.LastAccessedAt = DateTimeProvider.UtcNow;
            await this.dbContext.SaveChangesAsync();

            await this.EmitContentWrittenAsync(existingMetadata, existingContent.ContentName, newChunkName, caller);

            return ServiceResult<ChunkCreationOutput>.Ok(newChunk.ToCreationDto(accessTicket));
        }

        private async Task<ServiceResult<ChunkCreationOutput>> UploadHashedChunkAsync(
            Caller caller,
            Stream data,
            ContentMetadata existingContent,
            string newChunkName,
            ObjectMetadata existingMetadata,
            string headerHash,
            long declaredLength,
            string accessTicket)
        {
            var running = existingContent.RunningHashState is { Length: > 0 }
                ? SerializableSha256.Restore(existingContent.RunningHashState)
                : new SerializableSha256();

            using var chunkHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var tee = new HashingReadStream(data, chunkHasher, running))
            {
                await this.blobHelper.EncryptAndUploadBlobAsync(newChunkName, tee);
            }

            var dataLength = declaredLength;
            var serverHash = Convert.ToHexString(chunkHasher.GetHashAndReset()).ToLowerInvariant();
            if (!serverHash.Equals(headerHash, StringComparison.OrdinalIgnoreCase))
            {
                this.log.LogWarning(
                    "Chunk hash mismatch on {ChunkName}: client claimed {Claimed}, server computed {Computed}",
                    newChunkName, headerHash, serverHash);
                await this.blobHelper.DeleteBlobIfExistsAsync(newChunkName);

                return ServiceResult.Failure(
                    ServiceStatus.ChunkHashMismatch, HttpStatusCode.UnprocessableEntity,
                    "Received chunk bytes do not match the client-asserted hash.",
                    detail: new ChunkHashMismatch { Expected = headerHash, Actual = serverHash });
            }

            existingContent.Chunks.Add(new ChunkMetadata
            {
                ChunkName = newChunkName,
                Hash = serverHash,
                Length = dataLength,
            });
            existingContent.RunningHashState = running.SaveState();

            // Hashed mode: ContentStatus.Ready is flipped only at commit - NOT here.
            // Keep access-ticket alive. Update LastAccessedAt as existing code does.
            existingContent.AccessTicketSetAt = DateTimeProvider.UtcNow;
            existingMetadata.LastAccessedAt = DateTimeProvider.UtcNow;
            await this.dbContext.SaveChangesAsync().ConfigureAwait(false);

            this.log.LogInformation(
                "Chunk hash verified for '{ContentId}' chunk {ChunkIndex} ({Hash}, {Length} bytes)",
                existingContent.ContentName, existingContent.Chunks.Count - 1, serverHash, dataLength);

            await this.EmitContentWrittenAsync(existingMetadata, existingContent.ContentName, newChunkName, caller);

            return ServiceResult<ChunkCreationOutput>.Ok(new ChunkCreationOutput
            {
                ChunkName = newChunkName,
                Hash = serverHash,
                Length = dataLength,
                AccessTicket = accessTicket,
            });
        }

        private async Task<long> UploadMainContentAsync(string chunkName, Stream inputStream)
        {
            this.log.LogInformation("Sanitizing main content.");

            string content;
            using (var reader = new StreamReader(inputStream))
            {
                content = await reader.ReadToEndAsync();
            }

            var lengthBefore = content.Length;
            content = MainContentSanitizer.Sanitize(content);
            this.log.LogInformation($"Lenght before sanitizing: {lengthBefore}, after sanitizing: {content.Length}.");

            using (var dataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)))
            {
                await this.blobHelper.EncryptAndUploadBlobAsync(chunkName, dataStream);
                return dataStream.Length;
            }
        }

        // Audit-payload guarantee: secretId/contentId/chunkId are opaque identifiers, never
        // derived from secret plaintext. The writer is a no-op for non-audited secrets.
        private ValueTask EmitContentReadAsync(ObjectMetadata secret, string contentId, string chunkId, Caller caller)
            => this.auditWriter.AppendAsync(secret, SecretAuditEventType.ContentRead, caller.SubjectType, caller.SubjectId, new { contentId, chunkId }, this.log);

        private ValueTask EmitContentWrittenAsync(ObjectMetadata secret, string contentId, string chunkId, Caller caller)
            => this.auditWriter.AppendAsync(secret, SecretAuditEventType.ContentWritten, caller.SubjectType, caller.SubjectId, new { contentId, chunkId }, this.log);
    }
}
```

- [ ] **Step 5: Replace the handler with the adapter**

Replace the whole content of `SafeExchange.Core.Functions\Functions\SafeExchangeSecretStream.cs` with (the four
public static fields stay; `InterimOperationType` now reads `ChunkService.InterimOperationType`, the same
`"interim"`):

```csharp
/// <summary>
/// SafeExchangeSecretStream
/// </summary>

namespace SafeExchange.Core.Functions
{
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using System.Security.Claims;
    using System;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging.Abstractions;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Services;
    using SafeExchange.Core.Telemetry;
    using Microsoft.Azure.Functions.Worker.Http;
    using System.Net;

    /// <summary>
    /// HTTP adapter for chunk upload and download (<c>/secret/{secretId}/content/{contentId}/chunk[/{chunkId}]</c>,
    /// <c>.../all</c>) over <see cref="ChunkService"/>.
    /// </summary>
    public class SafeExchangeSecretStream
    {
        public static readonly string AccessTicketHeaderName = "X-SafeExchange-Ticket";
        public static readonly string OperationTypeHeaderName = "X-SafeExchange-OpType";
        public static readonly string ChunkHashHeaderName = "X-SafeExchange-Chunk-Hash";

        public static readonly string InterimOperationType = ChunkService.InterimOperationType;

        private readonly ICallerResolver callerResolver;

        private readonly ChunkService chunkService;

        public SafeExchangeSecretStream(ICallerResolver callerResolver, ChunkService chunkService)
        {
            this.callerResolver = callerResolver ?? throw new ArgumentNullException(nameof(callerResolver));
            this.chunkService = chunkService ?? throw new ArgumentNullException(nameof(chunkService));
        }

        /// <summary>
        /// Pre-service constructor, kept for code that builds handlers by hand: resolves callers through
        /// <paramref name="globalFilters"/> exactly as before and builds the service itself (without logging).
        /// </summary>
        public SafeExchangeSecretStream(IConfiguration configuration, SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, IPurger purger, IBlobHelper blobHelper, IPermissionsManager permissionsManager, IAuditWriter auditWriter)
            : this(
                  new GlobalFiltersCallerResolver(globalFilters, tokenHelper, dbContext),
                  new ChunkService(configuration, dbContext, purger, blobHelper, permissionsManager, auditWriter, NullLogger<ChunkService>.Instance))
        {
        }

        public async Task<HttpResponseData> Run(
            HttpRequestData request,
            string secretId, string contentId, string chunkId, ClaimsPrincipal principal, ILogger log)
        {
            var resolution = await this.callerResolver.ResolveAsync(principal, CallerChannels.Rest, null);
            if (!resolution.IsAllowed)
            {
                return await resolution.Denial.ToResponseAsync(request);
            }

            var caller = resolution.Caller;
            log.LogInformation($"{nameof(SafeExchangeSecretStream)} triggered for '{secretId}' ({contentId}, {chunkId}) by {caller.SubjectType} (tid {TelemetryContext.Current}), [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "post":
                    return await ActionResults.ServiceCallAsync(
                        request,
                        async () => await (await this.chunkService.UploadChunkAsync(caller, secretId, contentId, request.Body, ToUploadRequest(request.Headers, chunkId))).ToResponseAsync(request),
                        "HandleSecretStreamUpload", log);

                case "get":
                    return await ActionResults.ServiceCallAsync(
                        request,
                        async () => await (await this.chunkService.DownloadChunkAsync(caller, secretId, contentId, chunkId)).ToDownloadResponseAsync(request),
                        "HandleSecretStreamDownload", log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        public async Task<HttpResponseData> RunContentDownload(
            HttpRequestData request, string secretId, string contentId, ClaimsPrincipal principal, ILogger log)
        {
            var resolution = await this.callerResolver.ResolveAsync(principal, CallerChannels.Rest, null);
            if (!resolution.IsAllowed)
            {
                return await resolution.Denial.ToResponseAsync(request);
            }

            var caller = resolution.Caller;
            log.LogInformation($"{nameof(SafeExchangeSecretStream)}-ContentDownload triggered for '{secretId}' ({contentId}) by {caller.SubjectType} (tid {TelemetryContext.Current}), [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "get":
                    return await ActionResults.ServiceCallAsync(
                        request,
                        async () => await (await this.chunkService.DownloadContentAsync(caller, secretId, contentId)).ToDownloadResponseAsync(request),
                        "HandleSecretStreamDownload", log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        private static ChunkUploadRequest ToUploadRequest(HttpHeadersCollection headers, string chunkId)
        {
            var declaredLength = 0L;
            if (headers.TryGetValues("Content-Length", out var lengthHeaders) &&
                long.TryParse(lengthHeaders.FirstOrDefault(), out var clen))
            {
                declaredLength = clen;
            }

            return new ChunkUploadRequest
            {
                ChunkId = chunkId,
                AccessTicket = FirstHeaderValue(headers, AccessTicketHeaderName) ?? string.Empty,
                OperationType = FirstHeaderValue(headers, OperationTypeHeaderName) ?? string.Empty,
                ChunkHash = FirstHeaderValue(headers, ChunkHashHeaderName),
                DeclaredLength = declaredLength,
            };
        }

        private static string? FirstHeaderValue(HttpHeadersCollection headers, string name)
            => headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    }
}
```

- [ ] **Step 6: Run the service tests**

Run: `dotnet build SafeExchange.slnx --nologo -v q; dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchange.Tests.Services"`
Expected: `0 Error(s)` and `Passed!  - Failed:     0, Passed:    57` (50 + 7).

- [ ] **Step 7: Run the phase filter**

Expected: `Passed!  - Failed:     0, Passed:   325` (268 + 57). The stream contract tests pin the raw download
headers and bytes, legacy and hashed uploads, hash mismatches and lock expiry through the adapter.

- [ ] **Step 8: Commit**

```powershell
git add SafeExchange.Core/Services/ChunkUploadRequest.cs SafeExchange.Core/Services/MainContentSanitizer.cs SafeExchange.Core/Services/ChunkService.cs SafeExchange.Core.Functions/Functions/SafeExchangeSecretStream.cs SafeExchange.Tests/Services/ChunkServiceTests.cs
git --no-pager diff --cached --name-status
```
Expected exactly:
```text
M	SafeExchange.Core.Functions/Functions/SafeExchangeSecretStream.cs
A	SafeExchange.Core/Services/ChunkService.cs
A	SafeExchange.Core/Services/ChunkUploadRequest.cs
A	SafeExchange.Core/Services/MainContentSanitizer.cs
A	SafeExchange.Tests/Services/ChunkServiceTests.cs
```

```powershell
git commit -m "refactor(core): extract ChunkService" -m "Chunk upload (legacy and hashed, on a Stream), chunk download and whole-content download move from SafeExchangeSecretStream into Core's ChunkService. Downloads return headers plus a deferred writer, so the read and its audit event happen while the host streams the body, as before. ChunkUploadRequest carries the lock/op-type/hash headers and an optional ExpectedIndex for resumable in-band uploads (REST passes null). The handler becomes an HTTP adapter." -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 5: `AccessService` and the `SafeExchangeAccess` adapter

**Files:**
- Create: `SafeExchange.Tests\Services\AccessServiceTests.cs`
- Create: `SafeExchange.Core\Services\AccessService.cs`
- Replace: `SafeExchange.Core.Functions\Functions\SafeExchangeAccess.cs`

- [ ] **Step 1: Write the failing tests**

Create `SafeExchange.Tests\Services\AccessServiceTests.cs`. Besides the denials (incl. the `consent_required`
subStatus) it pins D2 for access: a failing permission check in grant propagates unwrapped (it ran outside
`TryCatchAsync`), the same failure inside patch is wrapped (patch checked permissions inside it):

```csharp
/// <summary>
/// AccessServiceTests
/// </summary>

namespace SafeExchange.Tests.Services
{
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Groups;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Services;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;

    [TestFixture]
    public class AccessServiceTests : ServiceTestBase
    {
        private AccessService service = null!;

        [SetUp]
        public void SetUp()
        {
            var features = new Features();
            var orphanedSecretConfiguration = new OrphanedSecretConfiguration();
            var orphanedSecretManager = new OrphanedSecretManager(
                Mock.Of<IOptionsMonitor<Features>>(x => x.CurrentValue == features),
                Mock.Of<IOptionsMonitor<OrphanedSecretConfiguration>>(x => x.CurrentValue == orphanedSecretConfiguration),
                NullLogger<OrphanedSecretManager>.Instance);
            this.service = new AccessService(
                this.DbContext, new GroupsManager(this.DbContext, NullLogger<GroupsManager>.Instance), this.PermissionsManager,
                this.AuditWriter, orphanedSecretManager, NullLogger<AccessService>.Instance);
        }

        [Test]
        public async Task Grant_ThenList_ShowsTheNewSubject()
        {
            await this.CreateSecretAsync("alpha");

            var grant = await this.service.GrantAsync(this.Owner, "alpha", ReaderCanRead());
            var list = await this.service.ListAsync(this.Owner, "alpha");
            var listV2 = await this.service.ListV2Async(this.Owner, "alpha");

            Assert.That(grant.IsOk, Is.True);
            Assert.That(list.Value!.AccessList.Select(p => p.SubjectName), Is.EquivalentTo(new[] { OwnerUpn, ReaderUpn }));
            Assert.That(list.Value!.CallerEffectivePermissions.CanRevokeAccess, Is.True);
            Assert.That(listV2.Value!.Select(p => p.SubjectName), Is.EquivalentTo(new[] { OwnerUpn, ReaderUpn }));
        }

        [Test]
        public async Task EveryMethod_DeniesACallerWithoutThePermission()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, PermissionType.Read);
            var add = new AccessUpdateInput { Add = ReaderCanRead() };
            var remove = new AccessUpdateInput { Remove = ReaderCanRead() };

            AssertInsufficientPermissions(await this.service.GrantAsync(this.Reader, "alpha", ReaderCanRead()), PermissionType.GrantAccess, "alpha");
            AssertInsufficientPermissions(await this.service.RevokeAsync(this.Reader, "alpha", ReaderCanRead()), PermissionType.RevokeAccess, "alpha");
            AssertInsufficientPermissions(await this.service.PatchAsync(this.Reader, "alpha", add), PermissionType.GrantAccess, "alpha");
            AssertInsufficientPermissions(await this.service.PatchAsync(this.Reader, "alpha", remove), PermissionType.RevokeAccess, "alpha");
            AssertInsufficientPermissions((await this.service.ListAsync(this.Stranger, "alpha")).Outcome, PermissionType.Read, "alpha");
            AssertInsufficientPermissions((await this.service.ListV2Async(this.Stranger, "alpha")).Outcome, PermissionType.Read, "alpha");
        }

        [Test]
        public async Task Denial_ForAUserWhoMustConsent_CarriesTheConsentSubStatus()
        {
            await this.CreateSecretAsync("alpha");
            this.DbContext.Users.Add(new User("Stranger", "00000000-0000-0000-0000-0000000000a3", "00000000-0000-0000-0000-000000000001", StrangerUpn, StrangerUpn) { ConsentRequired = true });
            await this.DbContext.SaveChangesAsync();

            var result = await this.service.GrantAsync(this.Stranger, "alpha", ReaderCanRead());

            AssertInsufficientPermissions(result, PermissionType.GrantAccess, "alpha", "consent_required");
        }

        [Test]
        public async Task MissingSecret_IsNotFound_WithTheAccessWording()
        {
            var result = await this.service.ListAsync(this.Owner, "missing");

            Assert.That(result.Status, Is.EqualTo(ServiceStatus.NotFound));
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(result.Error, Is.EqualTo("Secret 'missing' not exists"));
        }

        [Test]
        public async Task EmptyInput_IsAnErrorStatusWith400()
        {
            await this.CreateSecretAsync("alpha");

            var grant = await this.service.GrantAsync(this.Owner, "alpha", null);
            var patch = await this.service.PatchAsync(this.Owner, "alpha", new AccessUpdateInput());

            Assert.That(grant.Status, Is.EqualTo(ServiceStatus.Error));
            Assert.That(grant.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(grant.Error, Is.EqualTo("Access settings are not provided."));
            Assert.That(patch.Status, Is.EqualTo(ServiceStatus.Error));
            Assert.That(patch.Error, Is.EqualTo("Access update body must include at least one of 'add' or 'remove'."));
        }

        [Test]
        public void LookupFailure_PropagatesUnwrapped_ButWorkFailureIsWrapped()
        {
            var permissions = new Mock<IPermissionsManager>();
            var authorizationFailure = new InvalidOperationException("authorization store failed");
            permissions.Setup(p => p.IsAuthorizedAsync(It.IsAny<SubjectType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PermissionType>())).ThrowsAsync(authorizationFailure);
            this.DbContext.Objects.Add(new ObjectMetadata("alpha", CreationInput(), "User owner"));
            this.DbContext.SaveChanges();
            var service = new AccessService(
                this.DbContext, Mock.Of<IGroupsManager>(), permissions.Object, this.AuditWriter, Mock.Of<IOrphanedSecretManager>(), NullLogger<AccessService>.Instance);

            var thrown = Assert.ThrowsAsync<InvalidOperationException>(() => service.GrantAsync(this.Owner, "alpha", ReaderCanRead()));
            var wrapped = Assert.ThrowsAsync<ServiceOperationException>(() => service.PatchAsync(this.Owner, "alpha", new AccessUpdateInput { Add = ReaderCanRead() }));

            Assert.That(thrown, Is.SameAs(authorizationFailure));
            Assert.That(wrapped!.InnerException, Is.SameAs(authorizationFailure));
        }

        private static List<SubjectPermissionsInput> ReaderCanRead()
            => new() { new SubjectPermissionsInput { SubjectType = SubjectTypeInput.User, SubjectName = ReaderUpn, CanRead = true } };
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet build SafeExchange.slnx --nologo -v q`
Expected: FAIL with `error CS0246: The type or namespace name 'AccessService' could not be found` in
`AccessServiceTests.cs`.

- [ ] **Step 3: Create `AccessService`**

Create `SafeExchange.Core\Services\AccessService.cs`. Preserved quirks: the not-found message
`"Secret '{id}' not exists"` (no period), status `error` with 400 for empty input, `RevokeAccess` masking on
grant and patch adds, one net change per subject in patch, the orphan rule after revoke and patch. The two
group-grant helpers of the handler are merged into `GrantAccessToGroupAsync` with the same branches and log
lines:

```csharp
/// <summary>
/// AccessService
/// </summary>

namespace SafeExchange.Core.Services
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Graph;
    using SafeExchange.Core.Groups;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// Who can access a secret: grant, list, revoke and the atomic add/remove patch. The secret lookup and
    /// the permission checks of grant, list and revoke run before the operation part (REST ran them outside
    /// its error handler), so their exceptions propagate unwrapped.
    /// </summary>
    public sealed class AccessService
    {
        private readonly SafeExchangeDbContext dbContext;

        private readonly IGroupsManager groupsManager;

        private readonly IPermissionsManager permissionsManager;

        private readonly IAuditWriter auditWriter;

        private readonly IOrphanedSecretManager orphanedSecretManager;

        private readonly ILogger log;

        public AccessService(
            SafeExchangeDbContext dbContext,
            IGroupsManager groupsManager,
            IPermissionsManager permissionsManager,
            IAuditWriter auditWriter,
            IOrphanedSecretManager orphanedSecretManager,
            ILogger<AccessService> log)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.groupsManager = groupsManager ?? throw new ArgumentNullException(nameof(groupsManager));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
            this.auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
            this.orphanedSecretManager = orphanedSecretManager ?? throw new ArgumentNullException(nameof(orphanedSecretManager));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// Grants each entry's permissions (OR-merged into existing ones). <c>RevokeAccess</c> is only passed
        /// on when the caller holds it. <paramref name="permissions"/> is <c>null</c> when the host could not read it.
        /// </summary>
        public async Task<ServiceResult> GrantAsync(Caller caller, string secretId, IReadOnlyList<SubjectPermissionsInput>? permissions)
        {
            ArgumentNullException.ThrowIfNull(caller);
            var secret = await this.FindSecretAsync(secretId);
            if (secret is null)
            {
                return NotFound(secretId);
            }

            if (!await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.GrantAccess))
            {
                return await this.InsufficientPermissionsAsync(caller, PermissionType.GrantAccess, secretId);
            }

            var userCanRevokeAccess = await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.RevokeAccess);
            return await ServiceOperation.RunAsync(async () =>
            {
                if ((permissions?.Count ?? 0) == 0)
                {
                    this.log.LogInformation($"Permissions data for '{secret.ObjectName}' not provided.");
                    return InvalidInput("Access settings are not provided.");
                }

                foreach (var permissionInput in permissions!)
                {
                    await this.ApplyGrantAsync(secret, permissionInput, userCanRevokeAccess, caller);
                }

                await this.dbContext.SaveChangesAsync();
                return ServiceResult.Ok();
            });
        }

        /// <summary>
        /// The secret's access list with the caller's effective permissions (REST v3).
        /// </summary>
        public async Task<ServiceResult<AccessListOutput>> ListAsync(Caller caller, string secretId)
        {
            ArgumentNullException.ThrowIfNull(caller);
            var secret = await this.FindSecretAsync(secretId);
            if (secret is null)
            {
                return NotFound(secretId);
            }

            if (!await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Read))
            {
                return await this.InsufficientPermissionsAsync(caller, PermissionType.Read, secretId);
            }

            return await ServiceOperation.RunAsync(async () =>
            {
                var objectName = secret.ObjectName;
                var existingPermissions = await this.dbContext.Permissions.Where(p => p.SecretName.Equals(objectName)).ToListAsync();
                var effective = await this.permissionsManager.GetEffectivePermissionsAsync(caller.SubjectType, caller.SubjectId, objectName);

                return ServiceResult<AccessListOutput>.Ok(new AccessListOutput
                {
                    AccessList = existingPermissions.Select(p => p.ToDto()).ToList(),
                    CallerEffectivePermissions = EffectivePermissionsOutput.FromPermissionType(effective),
                });
            });
        }

        /// <summary>
        /// The secret's access list (REST v2).
        /// </summary>
        public async Task<ServiceResult<List<SubjectPermissionsOutput>>> ListV2Async(Caller caller, string secretId)
        {
            ArgumentNullException.ThrowIfNull(caller);
            var secret = await this.FindSecretAsync(secretId);
            if (secret is null)
            {
                return NotFound(secretId);
            }

            if (!await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Read))
            {
                return await this.InsufficientPermissionsAsync(caller, PermissionType.Read, secretId);
            }

            return await ServiceOperation.RunAsync(async () =>
            {
                var objectName = secret.ObjectName;
                var existingPermissions = await this.dbContext.Permissions.Where(p => p.SecretName.Equals(objectName)).ToListAsync();
                return ServiceResult<List<SubjectPermissionsOutput>>.Ok(existingPermissions.Select(p => p.ToDto()).ToList());
            });
        }

        /// <summary>
        /// Removes each entry's permissions, then applies the orphan rule. <paramref name="permissions"/> is
        /// <c>null</c> when the host could not read it.
        /// </summary>
        public async Task<ServiceResult> RevokeAsync(Caller caller, string secretId, IReadOnlyList<SubjectPermissionsInput>? permissions)
        {
            ArgumentNullException.ThrowIfNull(caller);
            var secret = await this.FindSecretAsync(secretId);
            if (secret is null)
            {
                return NotFound(secretId);
            }

            if (!await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.RevokeAccess))
            {
                return await this.InsufficientPermissionsAsync(caller, PermissionType.RevokeAccess, secretId);
            }

            return await ServiceOperation.RunAsync(async () =>
            {
                if ((permissions?.Count ?? 0) == 0)
                {
                    this.log.LogInformation($"Permissions data for '{secret.ObjectName}' not provided.");
                    return InvalidInput("Access settings are not provided.");
                }

                foreach (var permissionInput in permissions!)
                {
                    await this.ApplyRevokeAsync(secret, permissionInput, caller);
                }

                // ApplyOrphanRuleAsync self-gates on Features.UseAccessGiveUp and no-ops when off.
                await this.orphanedSecretManager.ApplyOrphanRuleAsync(secret.ObjectName, this.dbContext);

                await this.dbContext.SaveChangesAsync();
                return ServiceResult.Ok();
            });
        }

        /// <summary>
        /// Applies adds and removes as one net change per subject. Adds need <c>GrantAccess</c>, removes need
        /// <c>RevokeAccess</c>. <paramref name="input"/> is <c>null</c> when the host could not read it.
        /// </summary>
        public async Task<ServiceResult> PatchAsync(Caller caller, string secretId, AccessUpdateInput? input)
        {
            ArgumentNullException.ThrowIfNull(caller);
            var secret = await this.FindSecretAsync(secretId);
            if (secret is null)
            {
                return NotFound(secretId);
            }

            return await ServiceOperation.RunAsync(() => this.PatchSecretAccessAsync(secret, caller, input));
        }

        private static ServiceResult NotFound(string secretId)
            => ServiceResult.NotFound($"Secret '{secretId}' not exists");

        private static ServiceResult InvalidInput(string error)
            => ServiceResult.Failure(ServiceStatus.Error, HttpStatusCode.BadRequest, error);

        private async Task<ObjectMetadata?> FindSecretAsync(string secretId)
        {
            var existingMetadata = await this.dbContext.Objects.FindAsync(secretId);
            if (existingMetadata == null)
            {
                this.log.LogInformation($"Cannot handle permissions for secret '{secretId}', as it not exists.");
            }

            return existingMetadata;
        }

        private async Task<ServiceResult> InsufficientPermissionsAsync(Caller caller, PermissionType permission, string secretId)
        {
            var consentRequired = await this.permissionsManager.IsConsentRequiredAsync(caller.SubjectId);
            return ServiceResult.InsufficientPermissions(permission, secretId, consentRequired ? GraphDataProvider.ConsentRequiredSubStatus : string.Empty);
        }

        private async Task<ServiceResult> PatchSecretAccessAsync(ObjectMetadata secret, Caller caller, AccessUpdateInput? input)
        {
            var secretId = secret.ObjectName;
            var actorType = caller.SubjectType;
            var actorId = caller.SubjectId;
            var addCount = input?.Add?.Count ?? 0;
            var removeCount = input?.Remove?.Count ?? 0;

            if (addCount == 0 && removeCount == 0)
            {
                return InvalidInput("Access update body must include at least one of 'add' or 'remove'.");
            }

            if (addCount > 0)
            {
                if (!await this.permissionsManager.IsAuthorizedAsync(actorType, actorId, secretId, PermissionType.GrantAccess))
                {
                    return await this.InsufficientPermissionsAsync(caller, PermissionType.GrantAccess, secretId);
                }
            }

            if (removeCount > 0)
            {
                if (!await this.permissionsManager.IsAuthorizedAsync(actorType, actorId, secretId, PermissionType.RevokeAccess))
                {
                    return await this.InsufficientPermissionsAsync(caller, PermissionType.RevokeAccess, secretId);
                }
            }

            // Adds inherit the same RevokeAccess masking behaviour as POST: a caller without RevokeAccess
            // cannot grant the RevokeAccess flag to others. (If they have RevokeAccess and removeCount > 0
            // we already verified that above; otherwise check it explicitly.)
            var userCanRevokeAccess = removeCount > 0
                || await this.permissionsManager.IsAuthorizedAsync(actorType, actorId, secretId, PermissionType.RevokeAccess);

            // Coalesce every remove and add into a single net change per resolved subject, then apply
            // each once. Removing then re-adding the SAME subject in one request must NOT delete-then-
            // re-insert the same row inside one unit of work — Entity Framework would resolve that to a
            // delete and the subject would silently vanish from the access list. Instead we compute
            // '(existing & ~removed) | added' and write the result a single time.
            // (The older separate POST-grant / DELETE-revoke endpoints are intentionally left untouched.)
            var netChanges = new Dictionary<(SubjectType SubjectType, string SubjectId), PermissionNetChange>();

            PermissionNetChange GetNetChange(SubjectType targetSubjectType, string targetSubjectId, string targetSubjectName)
            {
                var key = (targetSubjectType, PermissionsManager.NormalizeSubjectId(targetSubjectType, targetSubjectId));
                if (!netChanges.TryGetValue(key, out var netChange))
                {
                    netChange = new PermissionNetChange(targetSubjectType, targetSubjectId, targetSubjectName);
                    netChanges[key] = netChange;
                }

                return netChange;
            }

            foreach (var rem in input!.Remove ?? new List<SubjectPermissionsInput>())
            {
                var targetSubjectType = rem.SubjectType.ToModel();
                var (targetSubjectId, targetSubjectName, _) = await this.ResolveTargetAndBeforeAsync(secretId, targetSubjectType, rem);
                GetNetChange(targetSubjectType, targetSubjectId, targetSubjectName).Remove |= rem.GetPermissionType();
            }

            foreach (var add in input.Add ?? new List<SubjectPermissionsInput>())
            {
                var targetSubjectType = add.SubjectType.ToModel();
                var permission = add.GetPermissionType();
                if (!userCanRevokeAccess)
                {
                    permission &= ~PermissionType.RevokeAccess;
                }

                var resolved = await this.ResolveAddTargetAsync(targetSubjectType, add, actorType, actorId);
                if (resolved is null)
                {
                    // e.g. a mail-only group that is not registered — same no-op as the POST grant path.
                    continue;
                }

                var (targetSubjectId, targetSubjectName) = resolved.Value;
                GetNetChange(targetSubjectType, targetSubjectId, targetSubjectName).Add |= permission;
            }

            foreach (var netChange in netChanges.Values)
            {
                var (beforePerm, afterPerm) = await this.permissionsManager.ApplyNetPermissionAsync(
                    netChange.SubjectType, netChange.SubjectId, netChange.SubjectName, secretId, netChange.Remove, netChange.Add);

                if (secret.AuditEnabled && afterPerm != beforePerm && !string.IsNullOrEmpty(netChange.SubjectId))
                {
                    // One audit event per subject reflecting the true net effect. Direction picks the type:
                    // any newly-added flag -> Granted; otherwise (flags only removed) -> Revoked.
                    var eventType = (afterPerm & ~beforePerm) != PermissionType.None
                        ? SecretAuditEventType.PermissionGranted
                        : SecretAuditEventType.PermissionRevoked;

                    await this.AppendPermissionEventAsync(secret, eventType, caller, netChange.SubjectType, netChange.SubjectId, netChange.SubjectName, beforePerm, afterPerm);
                }
            }

            this.log.LogInformation($"Subject {actorType} '{actorId}' applied {addCount} adds and {removeCount} removes to '{secretId}'.");

            // ApplyOrphanRuleAsync self-gates on Features.UseAccessGiveUp and no-ops when off.
            await this.orphanedSecretManager.ApplyOrphanRuleAsync(secretId, this.dbContext);

            await this.dbContext.SaveChangesAsync();
            return ServiceResult.Ok();
        }

        private async Task ApplyGrantAsync(ObjectMetadata secret, SubjectPermissionsInput permissionInput, bool userCanRevokeAccess, Caller caller)
        {
            var secretId = secret.ObjectName;
            var permission = permissionInput.GetPermissionType();
            if (!userCanRevokeAccess)
            {
                permission &= ~PermissionType.RevokeAccess;
            }

            var targetSubjectType = permissionInput.SubjectType.ToModel();
            var (targetSubjectId, targetSubjectName, beforePerm) = await this.ResolveTargetAndBeforeAsync(secretId, targetSubjectType, permissionInput);

            if (targetSubjectType.Equals(SubjectType.Group))
            {
                await this.GrantAccessToGroupAsync(secretId, permissionInput, permission, caller.SubjectType, caller.SubjectId);
            }
            else
            {
                this.log.LogInformation($"Setting permissions for '{secretId}': '{targetSubjectType} {permissionInput.SubjectName}' -> '{permission}'");
                await this.permissionsManager.SetPermissionAsync(targetSubjectType, permissionInput.SubjectName, permissionInput.SubjectName, secretId, permission);
            }

            if (secret.AuditEnabled && !string.IsNullOrEmpty(targetSubjectId))
            {
                // SetPermissionAsync OR-merges into existing flags; report the resulting
                // effective permission as `to`, not the requested delta.
                var afterPerm = beforePerm | permission;
                await this.AppendPermissionEventAsync(secret, SecretAuditEventType.PermissionGranted, caller, targetSubjectType, targetSubjectId, targetSubjectName, beforePerm, afterPerm);
            }
        }

        private async Task ApplyRevokeAsync(ObjectMetadata secret, SubjectPermissionsInput permissionInput, Caller caller)
        {
            var secretId = secret.ObjectName;
            var permission = permissionInput.GetPermissionType();
            var targetSubjectType = permissionInput.SubjectType.ToModel();
            var (targetSubjectId, targetSubjectName, beforePerm) = await this.ResolveTargetAndBeforeAsync(secretId, targetSubjectType, permissionInput);

            // For groups, the row is keyed by GroupId; for users/applications subjectId == subjectName.
            // ResolveTargetAndBeforeAsync canonicalises that, so revoke uses the same lookup as grant.
            var unsetKey = string.IsNullOrEmpty(targetSubjectId) ? permissionInput.SubjectName : targetSubjectId;
            this.log.LogInformation($"Unsetting permissions for '{secretId}': '{targetSubjectType} {permissionInput.SubjectName}' -> '{permission}'");
            await this.permissionsManager.UnsetPermissionAsync(targetSubjectType, unsetKey, secretId, permission);

            if (secret.AuditEnabled && !string.IsNullOrEmpty(targetSubjectId))
            {
                var afterPerm = beforePerm & ~permission;
                await this.AppendPermissionEventAsync(secret, SecretAuditEventType.PermissionRevoked, caller, targetSubjectType, targetSubjectId, targetSubjectName, beforePerm, afterPerm);
            }
        }

        private ValueTask AppendPermissionEventAsync(
            ObjectMetadata secret, SecretAuditEventType eventType, Caller caller,
            SubjectType targetSubjectType, string targetSubjectId, string targetSubjectName,
            PermissionType beforePerm, PermissionType afterPerm)
            => this.auditWriter.AppendAsync(
                secret, eventType,
                caller.SubjectType, caller.SubjectId,
                new
                {
                    target = new
                    {
                        subjectType = targetSubjectType.ToString(),
                        subjectId = targetSubjectId,
                        subjectName = targetSubjectName,
                    },
                    permissions = new
                    {
                        from = AuditPayloads.PermissionFlags(beforePerm),
                        to = AuditPayloads.PermissionFlags(afterPerm),
                    },
                }, this.log);

        private async Task<(string subjectId, string subjectName, PermissionType beforePerm)> ResolveTargetAndBeforeAsync(
            string secretId, SubjectType targetSubjectType, SubjectPermissionsInput input)
        {
            // Canonicalise the target id so grant and revoke key off the same value.
            // Groups: GUID flavour uses SubjectId verbatim; mail flavour resolves to the
            // registered GroupId via groupsManager (same path as GrantAccessToGroupMailAsync).
            // Users / applications: SubjectName is the id.
            string subjectName = input.SubjectName ?? string.Empty;
            string subjectId;
            if (targetSubjectType == SubjectType.Group)
            {
                if (Guid.TryParse(input.SubjectId, out _))
                {
                    subjectId = input.SubjectId;
                }
                else
                {
                    var resolved = await this.groupsManager.TryFindGroupByMailAsync(subjectName);
                    subjectId = resolved == default ? subjectName : resolved.GroupId;
                }
            }
            else
            {
                subjectId = subjectName;
            }

            var existing = await this.dbContext.Permissions.FirstOrDefaultAsync(p =>
                p.SecretName == secretId && p.SubjectType == targetSubjectType && p.SubjectId == subjectId);
            return (subjectId, subjectName, AuditPayloads.ToPermissionType(existing));
        }

        /// <summary>
        /// Resolves the canonical (subjectId, subjectName) for an add target, mirroring the grant path:
        /// GUID groups are ensured to exist and keyed by GUID; mail groups resolve to the registered
        /// GroupId (returns null — a no-op — when the mail group is not registered); users and
        /// applications key off SubjectName.
        /// </summary>
        private async Task<(string subjectId, string subjectName)?> ResolveAddTargetAsync(
            SubjectType targetSubjectType, SubjectPermissionsInput add, SubjectType actorType, string actorId)
        {
            if (targetSubjectType != SubjectType.Group)
            {
                return (add.SubjectName, add.SubjectName);
            }

            if (Guid.TryParse(add.SubjectId, out _))
            {
                await this.EnsureGroupExistsAsync(add, actorType, actorId);
                return (add.SubjectId, add.SubjectName);
            }

            var existingGroup = await this.groupsManager.TryFindGroupByMailAsync(add.SubjectName);
            if (existingGroup == default)
            {
                return null;
            }

            return (existingGroup.GroupId, existingGroup.DisplayName);
        }

        private async Task GrantAccessToGroupAsync(string secretId, SubjectPermissionsInput permissionInput, PermissionType permission, SubjectType actorType, string actorId)
        {
            if (Guid.TryParse(permissionInput.SubjectId, out _))
            {
                await this.EnsureGroupExistsAsync(permissionInput, actorType, actorId);

                this.log.LogInformation($"Setting permissions for '{secretId}': group '{permissionInput.SubjectName}' ({permissionInput.SubjectId}) -> '{permission}'");
                await this.permissionsManager.SetPermissionAsync(SubjectType.Group, permissionInput.SubjectId, permissionInput.SubjectName, secretId, permission);
                return;
            }

            var existingGroup = await this.groupsManager.TryFindGroupByMailAsync(permissionInput.SubjectName);
            if (existingGroup == default)
            {
                return;
            }

            this.log.LogInformation($"Setting permissions for '{secretId}': group mail '{permissionInput.SubjectName}', id: '{existingGroup.GroupId}' -> '{permission}'");
            await this.permissionsManager.SetPermissionAsync(SubjectType.Group, existingGroup.GroupId, existingGroup.DisplayName, secretId, permission);
        }

        private async Task<GroupDictionaryItem> EnsureGroupExistsAsync(SubjectPermissionsInput permissionInput, SubjectType subjectType, string subjectId)
        {
            if (permissionInput.SubjectType != SubjectTypeInput.Group)
            {
                throw new ArgumentException($"{nameof(permissionInput)} is not of subject type {SubjectTypeInput.Group}.");
            }

            var groupIntput = new GroupInput()
            {
                DisplayName = permissionInput.SubjectName
            };

            return await this.groupsManager.PutGroupAsync(permissionInput.SubjectId, groupIntput, subjectType, subjectId);
        }
    }
}
```

- [ ] **Step 4: Replace the handler with the adapter**

Replace the whole content of `SafeExchange.Core.Functions\Functions\SafeExchangeAccess.cs` with (`v3` →
`ListAsync`, `v2` → `ListV2Async`; the unknown-method branch answers 400 without a lookup, D3):

```csharp
/// <summary>
/// SafeExchangeAccess
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Groups;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Services;
    using SafeExchange.Core.Telemetry;
    using System;
    using System.Net;
    using System.Security.Claims;

    /// <summary>
    /// HTTP adapter for <c>/access/{secretId}</c> over <see cref="AccessService"/>.
    /// </summary>
    public class SafeExchangeAccess
    {
        private readonly ICallerResolver callerResolver;

        private readonly AccessService accessService;

        public SafeExchangeAccess(ICallerResolver callerResolver, AccessService accessService)
        {
            this.callerResolver = callerResolver ?? throw new ArgumentNullException(nameof(callerResolver));
            this.accessService = accessService ?? throw new ArgumentNullException(nameof(accessService));
        }

        /// <summary>
        /// Pre-service constructor, kept for code that builds handlers by hand: resolves callers through
        /// <paramref name="globalFilters"/> exactly as before and builds the service itself (without logging).
        /// <paramref name="purger"/> is not used (it never was).
        /// </summary>
        public SafeExchangeAccess(
            SafeExchangeDbContext dbContext,
            IGroupsManager groupsManager,
            ITokenHelper tokenHelper,
            GlobalFilters globalFilters,
            IPurger purger,
            IPermissionsManager permissionsManager,
            IAuditWriter auditWriter,
            IOrphanedSecretManager orphanedSecretManager)
            : this(
                  new GlobalFiltersCallerResolver(globalFilters, tokenHelper, dbContext),
                  new AccessService(dbContext, groupsManager, permissionsManager, auditWriter, orphanedSecretManager, NullLogger<AccessService>.Instance))
        {
            ArgumentNullException.ThrowIfNull(purger);
        }

        public async Task<HttpResponseData> Run(
            HttpRequestData request,
            string secretId, ClaimsPrincipal principal, ILogger log, bool enrichedAccessList = false)
        {
            var resolution = await this.callerResolver.ResolveAsync(principal, CallerChannels.Rest, null);
            if (!resolution.IsAllowed)
            {
                return await resolution.Denial.ToResponseAsync(request);
            }

            var caller = resolution.Caller;
            log.LogInformation($"{nameof(SafeExchangeAccess)} triggered for '{secretId}' by {caller.SubjectType} (tid {TelemetryContext.Current}), [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "post":
                    return await ActionResults.ServiceCallAsync(request, async () =>
                    {
                        var input = await ReadInputAsync<List<SubjectPermissionsInput>>(request, "Could not parse input data for permissions input.", log);
                        return await (await this.accessService.GrantAsync(caller, secretId, input)).ToResponseAsync(request);
                    }, "GrantAccessAsync", log);

                case "get":
                    return enrichedAccessList
                        ? await ActionResults.ServiceCallAsync(
                            request,
                            async () => await (await this.accessService.ListAsync(caller, secretId)).ToResponseAsync(request),
                            "GetEnrichedAccessListAsync", log)
                        : await ActionResults.ServiceCallAsync(
                            request,
                            async () => await (await this.accessService.ListV2Async(caller, secretId)).ToResponseAsync(request),
                            "GetAccessListAsync", log);

                case "delete":
                    return await ActionResults.ServiceCallAsync(request, async () =>
                    {
                        var input = await ReadInputAsync<List<SubjectPermissionsInput>>(request, "Could not parse input data for permissions input.", log);
                        return await (await this.accessService.RevokeAsync(caller, secretId, input)).ToResponseAsync(request);
                    }, "RevokeAccessAsync", log);

                case "patch":
                    return await ActionResults.ServiceCallAsync(request, async () =>
                    {
                        var input = await ReadInputAsync<AccessUpdateInput>(request, "Could not parse input data for access update input.", log);
                        return await (await this.accessService.PatchAsync(caller, secretId, input)).ToResponseAsync(request);
                    }, "PatchAccessAsync", log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        private static async Task<T?> ReadInputAsync<T>(HttpRequestData request, string parseFailureMessage, ILogger log) where T : class
        {
            var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            try
            {
                return DefaultJsonSerializer.Deserialize<T>(requestBody);
            }
            catch (Exception exception)
            {
                log.LogWarning(exception, parseFailureMessage);
                return null;
            }
        }
    }
}
```

- [ ] **Step 5: Run the service tests**

Run: `dotnet build SafeExchange.slnx --nologo -v q; dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchange.Tests.Services"`
Expected: `0 Error(s)` and `Passed!  - Failed:     0, Passed:    63` (57 + 6).

- [ ] **Step 6: Run the phase filter**

Expected: `Passed!  - Failed:     0, Passed:   331` (268 + 63). `SecC_PermissionsAuthzTests`,
`SecF1_GroupRevokeKeyTests` and `EffectivePermissionsTests` exercise grant/revoke/list through the adapter.

- [ ] **Step 7: Commit**

```powershell
git add SafeExchange.Core/Services/AccessService.cs SafeExchange.Core.Functions/Functions/SafeExchangeAccess.cs SafeExchange.Tests/Services/AccessServiceTests.cs
git --no-pager diff --cached --name-status
```
Expected exactly:
```text
M	SafeExchange.Core.Functions/Functions/SafeExchangeAccess.cs
A	SafeExchange.Core/Services/AccessService.cs
A	SafeExchange.Tests/Services/AccessServiceTests.cs
```

```powershell
git commit -m "refactor(core): extract AccessService" -m "Grant, v2/v3 access lists, revoke and the atomic add/remove patch move from SafeExchangeAccess into Core's AccessService, incl. the consent subStatus, RevokeAccess masking, net-change coalescing, audit events and the orphan rule. The secret lookup and the grant/list/revoke permission checks still run before the operation part, so their failures propagate as before. The handler becomes an HTTP adapter." -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 6: `AuditQueryService` and the `SafeExchangeSecretAudit` adapter

**Files:**
- Create: `SafeExchange.Tests\Services\AuditQueryServiceTests.cs`
- Create: `SafeExchange.Core\Services\AuditPageQuery.cs`
- Create: `SafeExchange.Core\Services\AuditQueryService.cs`
- Replace: `SafeExchange.Core.Functions\Functions\SafeExchangeSecretAudit.cs`

`SafeExchangeAdminSecretAudit` (`Functions\Admin\SafeExchangeAdminAudit.cs`) keeps calling
`SafeExchangeSecretAudit.BuildAuditPageAsync(dbContext, auditInstanceId, queryString)`, which stays as an
internal forwarder; that file does not change.

- [ ] **Step 1: Write the failing tests**

Create `SafeExchange.Tests\Services\AuditQueryServiceTests.cs`:

```csharp
/// <summary>
/// AuditQueryServiceTests
/// </summary>

namespace SafeExchange.Tests.Services
{
    using NUnit.Framework;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Services;
    using System;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;

    [TestFixture]
    public class AuditQueryServiceTests : ServiceTestBase
    {
        private AuditQueryService service = null!;

        [SetUp]
        public void SetUp()
        {
            this.service = new AuditQueryService(this.DbContext, this.PermissionsManager);
        }

        [Test]
        public async Task Read_CallerWithoutRead_IsForbidden()
        {
            await this.CreateSecretAsync("alpha", auditEnabled: true);

            var result = await this.service.ReadAsync(this.Stranger, "alpha", new AuditPageQuery());

            AssertInsufficientPermissions(result.Outcome, PermissionType.Read, "alpha");
        }

        [Test]
        public async Task Read_Missing_IsNotFound()
        {
            var result = await this.service.ReadAsync(this.Owner, "missing", new AuditPageQuery());

            Assert.That(result.Status, Is.EqualTo(ServiceStatus.NotFound));
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(result.Error, Is.EqualTo("Secret 'missing' does not exist."));
        }

        [Test]
        public async Task Read_AuditDisabled_ReturnsAnEmptyDisabledPage()
        {
            await this.CreateSecretAsync("alpha");

            var result = await this.service.ReadAsync(this.Owner, "alpha", new AuditPageQuery());

            Assert.That(result.IsOk, Is.True);
            Assert.That(result.Value!.AuditEnabled, Is.False);
            Assert.That(result.Value!.Events, Is.Empty);
        }

        [Test]
        public async Task Read_PagesWithAContinuationToken()
        {
            await this.CreateSecretAsync("alpha", auditEnabled: true);
            await this.GrantAsync("alpha", ReaderUpn, PermissionType.Read);
            await this.CreateSecretService().UpdateAsync(this.Owner, "alpha", new MetadataUpdateInput { ExpirationSettings = CreationInput().ExpirationSettings, Tags = new() { "x" } });

            var first = await this.service.ReadAsync(this.Reader, "alpha", new AuditPageQuery { PageSize = 1 });
            var second = await this.service.ReadAsync(this.Reader, "alpha", new AuditPageQuery { PageSize = 1, Continuation = first.Value!.NextContinuation });

            Assert.That(first.Value!.Events.Single().EventType, Is.EqualTo(nameof(SecretAuditEventType.SecretCreated)));
            Assert.That(first.Value!.NextContinuation, Is.Not.Null);
            Assert.That(second.Value!.Events.Single().EventType, Is.EqualTo(nameof(SecretAuditEventType.SecretMetadataUpdated)));
            Assert.That(second.Value!.NextContinuation, Is.Null);
        }

        [Test]
        public void Parse_ReadsTheRestQueryString()
        {
            var query = AuditPageQuery.Parse("?from=2026-01-01T00:00:00Z&to=2026-02-01&raw=true&pageSize=7&continuation=MTA%3D&direction=DESC");

            Assert.That(query.From, Is.EqualTo(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(query.To, Is.EqualTo(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(query.Raw, Is.True);
            Assert.That(query.PageSize, Is.EqualTo(7));
            Assert.That(query.Continuation, Is.EqualTo("MTA="));
            Assert.That(query.Descending, Is.True);
            Assert.That(AuditPageQuery.Parse(null), Is.EqualTo(new AuditPageQuery()));
        }
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet build SafeExchange.slnx --nologo -v q`
Expected: FAIL with `error CS0246: The type or namespace name 'AuditQueryService' could not be found` (and
`'AuditPageQuery'`) in `AuditQueryServiceTests.cs`.

- [ ] **Step 3: Create the query and the service**

Create `SafeExchange.Core\Services\AuditPageQuery.cs` (the old query-string parsing; `PageSize` is clamped
when the page is built, so `Parse` keeps the raw number):

```csharp
/// <summary>
/// AuditPageQuery
/// </summary>

namespace SafeExchange.Core.Services
{
    using System;
    using System.Globalization;

    /// <summary>
    /// One page request against a secret's audit trail. <see cref="Parse"/> reads the REST query string
    /// (<c>from</c>, <c>to</c>, <c>raw</c>, <c>pageSize</c>, <c>continuation</c>, <c>direction</c>).
    /// </summary>
    public sealed record AuditPageQuery
    {
        public const int DefaultPageSize = 100;

        public const int MaxPageSize = 500;

        /// <summary>
        /// Inclusive lower bound on <c>OccurredAt</c> (UTC).
        /// </summary>
        public DateTime? From { get; init; }

        /// <summary>
        /// Exclusive upper bound on <c>OccurredAt</c> (UTC).
        /// </summary>
        public DateTime? To { get; init; }

        /// <summary>
        /// <c>true</c> returns every event; <c>false</c> merges runs of content reads.
        /// </summary>
        public bool Raw { get; init; }

        /// <summary>
        /// Requested page size; clamped to 1..<see cref="MaxPageSize"/> when the page is built.
        /// </summary>
        public int PageSize { get; init; } = DefaultPageSize;

        /// <summary>
        /// The previous page's <c>nextContinuation</c>; an unreadable token starts from the beginning.
        /// </summary>
        public string? Continuation { get; init; }

        public bool Descending { get; init; }

        public static AuditPageQuery Parse(string? queryString)
        {
            var qs = System.Web.HttpUtility.ParseQueryString(queryString ?? string.Empty);
            return new AuditPageQuery
            {
                From = TryParseUtc(qs["from"]),
                To = TryParseUtc(qs["to"]),
                Raw = bool.TryParse(qs["raw"], out var r) && r,
                PageSize = int.TryParse(qs["pageSize"], out var p) ? p : DefaultPageSize,
                Continuation = qs["continuation"],
                Descending = string.Equals(qs["direction"], "desc", StringComparison.OrdinalIgnoreCase),
            };
        }

        private static DateTime? TryParseUtc(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }
            return DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dt) ? dt : null;
        }
    }
}
```

Create `SafeExchange.Core\Services\AuditQueryService.cs` (`BuildAuditPageAsync` is the old static helper taking
an `AuditPageQuery` instead of a query string):

```csharp
/// <summary>
/// AuditQueryService
/// </summary>

namespace SafeExchange.Core.Services
{
    using Microsoft.EntityFrameworkCore;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Reads a secret's live audit trail, page by page, for callers with Read on the secret.
    /// </summary>
    public sealed class AuditQueryService
    {
        private readonly SafeExchangeDbContext dbContext;

        private readonly IPermissionsManager permissionsManager;

        public AuditQueryService(SafeExchangeDbContext dbContext, IPermissionsManager permissionsManager)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
        }

        public async Task<ServiceResult<SecretAuditPageOutput>> ReadAsync(Caller caller, string secretId, AuditPageQuery query)
        {
            ArgumentNullException.ThrowIfNull(caller);
            ArgumentNullException.ThrowIfNull(query);

            return await ServiceOperation.RunAsync<ServiceResult<SecretAuditPageOutput>>(async () =>
            {
                var metadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
                if (metadata is null)
                {
                    return ServiceResult.NotFound($"Secret '{secretId}' does not exist.");
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Read)))
                {
                    return ServiceResult.InsufficientPermissions(PermissionType.Read, secretId);
                }

                if (!metadata.AuditEnabled || string.IsNullOrEmpty(metadata.AuditInstanceId))
                {
                    return ServiceResult<SecretAuditPageOutput>.Ok(new SecretAuditPageOutput { AuditEnabled = false });
                }

                var page = await BuildAuditPageAsync(this.dbContext, metadata.AuditInstanceId!, query);
                return ServiceResult<SecretAuditPageOutput>.Ok(page);
            });
        }

        /// <summary>
        /// Builds one page of an audit instance. Shared with the admin audit endpoint, which addresses
        /// audit runs by instance id (a secret name can be reused after delete and recreate).
        /// </summary>
        public static async Task<SecretAuditPageOutput> BuildAuditPageAsync(SafeExchangeDbContext dbContext, string auditInstanceId, AuditPageQuery query)
        {
            ArgumentNullException.ThrowIfNull(dbContext);
            ArgumentNullException.ThrowIfNull(query);

            var pageSize = Math.Clamp(query.PageSize, 1, AuditPageQuery.MaxPageSize);
            var after = TryDecodeContinuation(query.Continuation);
            var descending = query.Descending;

            var events = dbContext.SecretAuditEvents.Where(e => e.AuditInstanceId == auditInstanceId);
            if (after.HasValue)
            {
                var afterVal = after.Value;
                events = descending
                    ? events.Where(e => e.SequenceNumber < afterVal)
                    : events.Where(e => e.SequenceNumber > afterVal);
            }

            if (query.From.HasValue)
            {
                var fromVal = query.From.Value;
                events = events.Where(e => e.OccurredAt >= fromVal);
            }

            if (query.To.HasValue)
            {
                var toVal = query.To.Value;
                events = events.Where(e => e.OccurredAt < toVal);
            }

            var ordered = descending
                ? events.OrderByDescending(e => e.SequenceNumber)
                : events.OrderBy(e => e.SequenceNumber);
            var page = await ordered.Take(pageSize + 1).ToListAsync();
            string? nextToken = null;
            if (page.Count > pageSize)
            {
                var lastInPage = page[pageSize - 1];
                nextToken = EncodeContinuation(lastInPage.SequenceNumber);
                page = page.GetRange(0, pageSize);
            }

            // ContentReadMerger expects ascending input. When paging desc, reverse the
            // page in/out so the merger sees ascending and the response preserves desc.
            List<SecretAuditEventOutput> dtoEvents;
            if (descending)
            {
                var ascForMerge = new List<SecretAuditEvent>(page);
                ascForMerge.Reverse();
                var merged = query.Raw ? ContentReadMerger.Raw(ascForMerge) : ContentReadMerger.Merge(ascForMerge);
                merged.Reverse();
                dtoEvents = merged;
            }
            else
            {
                dtoEvents = query.Raw ? ContentReadMerger.Raw(page) : ContentReadMerger.Merge(page);
            }

            return new SecretAuditPageOutput
            {
                AuditEnabled = true,
                Events = dtoEvents,
                NextContinuation = nextToken,
            };
        }

        private static long? TryDecodeContinuation(string? token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }
            try
            {
                var bytes = Convert.FromBase64String(token);
                var s = Encoding.UTF8.GetString(bytes);
                return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static string EncodeContinuation(long sequenceNumber)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(sequenceNumber.ToString(CultureInfo.InvariantCulture)));
    }
}
```

- [ ] **Step 4: Replace the handler with the adapter**

Replace the whole content of `SafeExchange.Core.Functions\Functions\SafeExchangeSecretAudit.cs` with:

```csharp
/// <summary>
/// SafeExchangeSecretAudit — GET /v2/secret/{secretId}/audit handler.
/// Returns the live audit instance's events for callers with Read on the secret.
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Services;
    using SafeExchange.Core.Telemetry;
    using System;
    using System.Security.Claims;
    using System.Threading.Tasks;

    /// <summary>
    /// HTTP adapter for <c>GET /secret/{secretId}/audit</c> over <see cref="AuditQueryService"/>.
    /// </summary>
    public class SafeExchangeSecretAudit
    {
        private readonly ICallerResolver callerResolver;

        private readonly AuditQueryService auditQueryService;

        public SafeExchangeSecretAudit(ICallerResolver callerResolver, AuditQueryService auditQueryService)
        {
            this.callerResolver = callerResolver ?? throw new ArgumentNullException(nameof(callerResolver));
            this.auditQueryService = auditQueryService ?? throw new ArgumentNullException(nameof(auditQueryService));
        }

        /// <summary>
        /// Pre-service constructor, kept for code that builds handlers by hand: resolves callers through
        /// <paramref name="globalFilters"/> exactly as before and builds the service itself.
        /// </summary>
        public SafeExchangeSecretAudit(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, IPermissionsManager permissionsManager)
            : this(
                  new GlobalFiltersCallerResolver(globalFilters, tokenHelper, dbContext),
                  new AuditQueryService(dbContext, permissionsManager))
        {
        }

        public async Task<HttpResponseData> Run(HttpRequestData request, string secretId, ClaimsPrincipal principal, ILogger log)
        {
            var resolution = await this.callerResolver.ResolveAsync(principal, CallerChannels.Rest, null);
            if (!resolution.IsAllowed)
            {
                return await resolution.Denial.ToResponseAsync(request);
            }

            var caller = resolution.Caller;
            log.LogInformation($"{nameof(SafeExchangeSecretAudit)} triggered for '{secretId}' by {caller.SubjectType} (tid {TelemetryContext.Current}).");

            return await ActionResults.ServiceCallAsync(
                request,
                async () => await (await this.auditQueryService.ReadAsync(caller, secretId, AuditPageQuery.Parse(request.Url?.Query))).ToResponseAsync(request),
                nameof(SafeExchangeSecretAudit), log);
        }

        // Shared with SafeExchangeAdminSecretAudit, which passes the raw request URL query.
        internal static Task<SecretAuditPageOutput> BuildAuditPageAsync(
            SafeExchangeDbContext dbContext, string auditInstanceId, string queryString)
            => AuditQueryService.BuildAuditPageAsync(dbContext, auditInstanceId, AuditPageQuery.Parse(queryString));
    }
}
```

- [ ] **Step 5: Run the service tests**

Run: `dotnet build SafeExchange.slnx --nologo -v q; dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchange.Tests.Services"`
Expected: `0 Error(s)` and `Passed!  - Failed:     0, Passed:    68` (63 + 5).

- [ ] **Step 6: Run the phase filter**

Expected: `Passed!  - Failed:     0, Passed:   336` (268 + 68). `SecretAuditContractTests` pins paging,
`direction=desc`, `raw`, bad continuations and the hash chain through the adapter.

- [ ] **Step 7: Commit**

```powershell
git add SafeExchange.Core/Services/AuditPageQuery.cs SafeExchange.Core/Services/AuditQueryService.cs SafeExchange.Core.Functions/Functions/SafeExchangeSecretAudit.cs SafeExchange.Tests/Services/AuditQueryServiceTests.cs
git --no-pager diff --cached --name-status
```
Expected exactly:
```text
M	SafeExchange.Core.Functions/Functions/SafeExchangeSecretAudit.cs
A	SafeExchange.Core/Services/AuditPageQuery.cs
A	SafeExchange.Core/Services/AuditQueryService.cs
A	SafeExchange.Tests/Services/AuditQueryServiceTests.cs
```

```powershell
git commit -m "refactor(core): extract AuditQueryService" -m "The secret audit page read moves from SafeExchangeSecretAudit into Core's AuditQueryService, with a typed AuditPageQuery (AuditPageQuery.Parse keeps the REST query parsing). BuildAuditPageAsync moves along; the handler keeps an internal forwarder so the admin audit endpoint is unchanged. The handler becomes an HTTP adapter." -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 7: Services and handlers in DI; triggers get handlers from DI

**Files:**
- Create: `SafeExchange.Tests\Services\ServiceRegistrationTests.cs`
- Modify: `SafeExchange.Tests\Tests\SafeExchangeFunctionsStartupTests.cs`
- Modify: `SafeExchange.Core\SafeExchangeStartup.cs`
- Modify: `SafeExchange.Core.Functions\SafeExchangeFunctionsStartup.cs`
- Modify: `SafeExchange.Functions\Functions\SafeSecret.cs`
- Modify: `SafeExchange.Functions\Functions\SafeAccess.cs`

- [ ] **Step 1: Write the failing tests**

Create `SafeExchange.Tests\Services\ServiceRegistrationTests.cs`. The second test builds the app's container
(Functions + Core registrations, `ValidateScopes = true`), replaces `ICallerResolver` the way a consumer with its
own claims would, and proves that handlers from DI use it (so the factories pick the DI constructors). The
in-memory blob helper stands in for `BlobHelper`, whose constructor needs Key Vault settings:

```csharp
/// <summary>
/// ServiceRegistrationTests
/// </summary>

namespace SafeExchange.Tests.Services
{
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Functions;
    using SafeExchange.Tests.Contract;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Uses the contract fixture plumbing (production serializer settings) to call handlers resolved from DI.
    /// </summary>
    [TestFixture]
    public class ServiceRegistrationTests : ContractTestBase
    {
        [Test]
        public void ConfigureServices_RegistersTheServicesAsScoped()
        {
            var services = new ServiceCollection();

            SafeExchangeStartup.ConfigureServices(StartupConfiguration(), services);

            var registrations = services
                .Where(d => d.ServiceType.Namespace == "SafeExchange.Core.Services")
                .Select(d => $"{d.Lifetime} {d.ServiceType.Name} <- {d.ImplementationType?.Name}")
                .ToList();
            Assert.That(registrations, Is.EqualTo(new[]
            {
                "Scoped SecretService <- SecretService",
                "Scoped ContentService <- ContentService",
                "Scoped ChunkService <- ChunkService",
                "Scoped AccessService <- AccessService",
                "Scoped AuditQueryService <- AuditQueryService",
            }));
        }

        [Test]
        public async Task HandlersFromDi_UseTheRegisteredCallerResolver()
        {
            var configuration = StartupConfiguration();
            var services = new ServiceCollection();
            SafeExchangeFunctionsStartup.ConfigureFunctionsServices(services);
            SafeExchangeStartup.ConfigureServices(configuration, services);
            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging();
            services.AddSingleton<IBlobHelper>(new TestBlobHelper());
            services.AddScoped<ICallerResolver, DenyingCallerResolver>();

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            using var scope = provider.CreateScope();
            var handlers = scope.ServiceProvider;
            Assert.That(handlers.GetRequiredService<SafeExchangeSecretContentMeta>(), Is.Not.Null);
            Assert.That(handlers.GetRequiredService<SafeExchangeSecretStream>(), Is.Not.Null);
            Assert.That(handlers.GetRequiredService<SafeExchangeContentCommit>(), Is.Not.Null);
            Assert.That(handlers.GetRequiredService<SafeExchangeAccess>(), Is.Not.Null);
            Assert.That(handlers.GetRequiredService<SafeExchangeSecretAudit>(), Is.Not.Null);

            var response = await handlers.GetRequiredService<SafeExchangeSecretMeta>().RunListV3(Request("get"), this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Unauthorized, """
                {"status":"unauthorized","subStatus":"","error":"Denied by the registered resolver."}
                """);
        }

        private static IConfiguration StartupConfiguration()
            => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "CosmosDb:CosmosDbEndpoint", "https://localhost:8081/" },
                    { "CosmosDb:DatabaseName", "startup-test" },
                })
                .Build();

        private sealed class DenyingCallerResolver : ICallerResolver
        {
            public Task<CallerResolution> ResolveAsync(ClaimsPrincipal principal, string channel, string? clientLabel, CancellationToken cancellationToken = default)
                => Task.FromResult(CallerResolution.Denied(new CallerDenial(HttpStatusCode.Unauthorized, "unauthorized", "Denied by the registered resolver.", string.Empty)));
        }
    }
}
```

In `SafeExchange.Tests\Tests\SafeExchangeFunctionsStartupTests.cs`, test
`ConfigureFunctionsServices_RegistersTheFunctionsTypedServices`, replace

```csharp
                .Select(d => $"{d.Lifetime} {d.ServiceType.Name} <- {d.ImplementationType?.Name}")
```

with

```csharp
                .Select(d => $"{d.Lifetime} {d.ServiceType.Name} <- {d.ImplementationType?.Name ?? "factory"}")
```

and replace

```csharp
                "Singleton GlobalFilters <- GlobalFilters",
```

with

```csharp
                "Singleton GlobalFilters <- GlobalFilters",
                "Scoped SafeExchangeSecretMeta <- factory",
                "Scoped SafeExchangeSecretContentMeta <- factory",
                "Scoped SafeExchangeSecretStream <- factory",
                "Scoped SafeExchangeContentCommit <- factory",
                "Scoped SafeExchangeAccess <- factory",
                "Scoped SafeExchangeSecretAudit <- factory",
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet build SafeExchange.slnx --nologo -v q; dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchange.Tests.Services.ServiceRegistrationTests|FullyQualifiedName~SafeExchangeFunctionsStartupTests"`
Expected: `Failed!  - Failed:     3, Passed:     1, Skipped:     0, Total:     4`, with these messages:
```text
ConfigureServices_RegistersTheServicesAsScoped:
  Expected is <System.String[5]>, actual is <System.Collections.Generic.List`1[System.String]> with 0 elements
HandlersFromDi_UseTheRegisteredCallerResolver:
  System.InvalidOperationException : No service for type 'SafeExchange.Core.Functions.SafeExchangeSecretContentMeta' has been registered.
ConfigureFunctionsServices_RegistersTheFunctionsTypedServices:
  Expected is <System.String[10]>, actual is <System.Collections.Generic.List`1[System.String]> with 4 elements
```

- [ ] **Step 3: Register the services (Core)**

In `SafeExchange.Core\SafeExchangeStartup.cs` add the using after `using SafeExchange.Core.Purger;`:

```csharp
    using SafeExchange.Core.Services;
```

and in `ConfigureServices`, after `services.AddScoped<ICallerResolver, CallerResolver>();`, insert:

```csharp

            services.AddScoped<SecretService>();
            services.AddScoped<ContentService>();
            services.AddScoped<ChunkService>();
            services.AddScoped<AccessService>();
            services.AddScoped<AuditQueryService>();
```

- [ ] **Step 4: Register the handlers (Functions library)**

In `SafeExchange.Core.Functions\SafeExchangeFunctionsStartup.cs` replace the usings

```csharp
    using Microsoft.Extensions.Hosting;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Middleware;
```

with

```csharp
    using Microsoft.Extensions.Hosting;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Middleware;
    using SafeExchange.Core.Services;
```

and in `ConfigureFunctionsServices`, after `services.AddSingleton<GlobalFilters>();`, insert:

```csharp

            // Factories pick the service-based constructors; the pre-service ones would also be satisfiable.
            services.AddScoped(sp => new SafeExchangeSecretMeta(sp.GetRequiredService<ICallerResolver>(), sp.GetRequiredService<SecretService>()));
            services.AddScoped(sp => new SafeExchangeSecretContentMeta(sp.GetRequiredService<ICallerResolver>(), sp.GetRequiredService<ContentService>()));
            services.AddScoped(sp => new SafeExchangeSecretStream(sp.GetRequiredService<ICallerResolver>(), sp.GetRequiredService<ChunkService>()));
            services.AddScoped(sp => new SafeExchangeContentCommit(sp.GetRequiredService<ICallerResolver>(), sp.GetRequiredService<ContentService>()));
            services.AddScoped(sp => new SafeExchangeAccess(sp.GetRequiredService<ICallerResolver>(), sp.GetRequiredService<AccessService>()));
            services.AddScoped(sp => new SafeExchangeSecretAudit(sp.GetRequiredService<ICallerResolver>(), sp.GetRequiredService<AuditQueryService>()));
```

- [ ] **Step 5: Run the tests**

Run: `dotnet build SafeExchange.slnx --nologo -v q; dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchange.Tests.Services|FullyQualifiedName~SafeExchangeFunctionsStartupTests|FullyQualifiedName~CallerResolverTests"`
Expected: `Passed!  - Failed:     0, Passed:    84` (70 service tests + 2 startup + 12 resolver).

- [ ] **Step 6: Let the triggers take their handlers from DI**

In `SafeExchange.Functions\Functions\SafeSecret.cs` (keep the file's BOM) replace the usings

```csharp
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Filters;
    using System.Security.Claims;
```

with

```csharp
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core;
    using SafeExchange.Core.Functions;
    using System.Security.Claims;
```

and the constructor

```csharp
        public SafeSecret(IConfiguration configuration, SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, IPurger purger, IPermissionsManager permissionsManager, IBlobHelper blobHelper, IAuditWriter auditWriter, ILogger<SafeSecret> log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.metaHandler = new SafeExchangeSecretMeta(configuration, dbContext, tokenHelper, globalFilters, purger, permissionsManager, auditWriter);
            this.contentMetaHandler = new SafeExchangeSecretContentMeta(configuration, dbContext, tokenHelper, globalFilters, purger, permissionsManager, auditWriter);
            this.contentHandler = new SafeExchangeSecretStream(configuration, dbContext, tokenHelper, globalFilters, purger, blobHelper, permissionsManager, auditWriter);
            this.contentCommitHandler = new SafeExchangeContentCommit(configuration, dbContext, tokenHelper, globalFilters, purger, permissionsManager, auditWriter);
            this.auditHandler = new SafeExchangeSecretAudit(dbContext, tokenHelper, globalFilters, permissionsManager);
        }
```

with

```csharp
        public SafeSecret(SafeExchangeSecretMeta metaHandler, SafeExchangeSecretContentMeta contentMetaHandler, SafeExchangeSecretStream contentHandler, SafeExchangeContentCommit contentCommitHandler, SafeExchangeSecretAudit auditHandler, ILogger<SafeSecret> log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.metaHandler = metaHandler ?? throw new ArgumentNullException(nameof(metaHandler));
            this.contentMetaHandler = contentMetaHandler ?? throw new ArgumentNullException(nameof(contentMetaHandler));
            this.contentHandler = contentHandler ?? throw new ArgumentNullException(nameof(contentHandler));
            this.contentCommitHandler = contentCommitHandler ?? throw new ArgumentNullException(nameof(contentCommitHandler));
            this.auditHandler = auditHandler ?? throw new ArgumentNullException(nameof(auditHandler));
        }
```

In `SafeExchange.Functions\Functions\SafeAccess.cs` replace the usings

```csharp
    using SafeExchange.Core;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Groups;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
```

with

```csharp
    using SafeExchange.Core;
    using SafeExchange.Core.Functions;
```

and the constructor

```csharp
        public SafeAccess(
            SafeExchangeDbContext dbContext,
            IGroupsManager groupsManager,
            ITokenHelper tokenHelper,
            GlobalFilters globalFilters,
            IPurger purger,
            IPermissionsManager permissionsManager,
            IOrphanedSecretManager orphanedSecretManager,
            IAuditWriter auditWriter,
            ILogger<SafeAccess> log)
        {
            this.accessHandler = new SafeExchangeAccess(
                dbContext, groupsManager, tokenHelper, globalFilters,
                purger, permissionsManager, auditWriter, orphanedSecretManager);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }
```

with

```csharp
        public SafeAccess(SafeExchangeAccess accessHandler, ILogger<SafeAccess> log)
        {
            this.accessHandler = accessHandler ?? throw new ArgumentNullException(nameof(accessHandler));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }
```

Routes, `[Function]` names, methods and `Run*` calls do not change.

- [ ] **Step 7: Build and run the phase filter**

Run: `dotnet build SafeExchange.slnx --nologo -v q`, then the phase filter.
Expected: `0 Error(s)`; `Passed!  - Failed:     0, Passed:   338, Skipped:     0, Total:   338`
(212 contract + 2 architecture + 54 handler regression + 70 service tests).

- [ ] **Step 8: Commit**

```powershell
git add SafeExchange.Core/SafeExchangeStartup.cs SafeExchange.Core.Functions/SafeExchangeFunctionsStartup.cs SafeExchange.Functions/Functions/SafeSecret.cs SafeExchange.Functions/Functions/SafeAccess.cs SafeExchange.Tests/Tests/SafeExchangeFunctionsStartupTests.cs SafeExchange.Tests/Services/ServiceRegistrationTests.cs
git --no-pager diff --cached --name-status
```
Expected exactly:
```text
M	SafeExchange.Core.Functions/SafeExchangeFunctionsStartup.cs
M	SafeExchange.Core/SafeExchangeStartup.cs
M	SafeExchange.Functions/Functions/SafeAccess.cs
M	SafeExchange.Functions/Functions/SafeSecret.cs
A	SafeExchange.Tests/Services/ServiceRegistrationTests.cs
M	SafeExchange.Tests/Tests/SafeExchangeFunctionsStartupTests.cs
```

```powershell
git commit -m "refactor(functions): resolve the six handlers and their services from DI" -m "SafeExchangeStartup registers SecretService, ContentService, ChunkService, AccessService and AuditQueryService as scoped; SafeExchangeFunctionsStartup registers the six handlers as scoped through factories that call their (ICallerResolver, service) constructors. SafeSecret and SafeAccess now take their handlers by constructor. Routes, function names and methods are unchanged." -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 8: Documentation, full verification and the plan commit

**Files:**
- Modify: `docs\architecture.md`
- Add: `docs\superpowers\plans\2026-11-21-mcp-p4-services.md` (this plan)

- [ ] **Step 1: Update the project structure in `docs\architecture.md`**

Replace

```text
└── Requests/             — Request/response DTOs
```

with

```text
├── Requests/             — Request/response DTOs
└── Services/             — Host-agnostic services (SecretService, ContentService, ChunkService, AccessService, AuditQueryService) taking a Caller and returning ServiceResult
```

replace

```text
├── Functions/             — HTTP business logic handlers for each endpoint
```

with

```text
├── Functions/             — HTTP handlers for each endpoint (the secret, content, chunk, commit, access and audit handlers are thin adapters over Core's Services)
```

replace

```text
├── Utilities/             — ActionResults, TokenHelperHttpExtensions, CallerDenialHttpExtensions
```

with

```text
├── Utilities/             — ActionResults, TokenHelperHttpExtensions, CallerDenialHttpExtensions, ServiceResultHttpExtensions
```

and replace (in the `SafeExchange.Functions/` block)

```text
├── Functions/             — HTTP-triggered Azure Function definitions
```

with

```text
├── Functions/             — HTTP-triggered Azure Function definitions (handlers come from DI)
```

`docs/api-endpoints.md` is not touched: REST does not change (its known gaps are phase 7).

- [ ] **Step 2: Full rebuild, no new warnings**

Run: `dotnet build SafeExchange.slnx --nologo -v q --no-incremental`
Expected: `704 Warning(s)` and `0 Error(s)` (Task 0's number minus the removed
`SafeExchangeAccess.cs(469,33) CS8602`). Stricter check — no warning in a new or changed source file (the
pattern is anchored on the file name so that warnings in other files that merely mention these types do not
match):
```powershell
dotnet build SafeExchange.slnx --nologo -v q --no-incremental 2>&1 | Select-String -Pattern '^\S*\\(Services\\\w+|ActionResults|GlobalFilters\w*|SafeExchange(SecretMeta|SecretContentMeta|SecretStream|ContentCommit|Access|SecretAudit)|SafeSecret|SafeAccess|SafeExchangeFunctionsStartup\w*|SafeExchangeStartup)\.cs\(\d+,\d+\): warning'
```
Expected: no output.

- [ ] **Step 3: Phase filter**

Expected: `Passed!  - Failed:     0, Passed:   338, Skipped:     0, Total:   338`.

- [ ] **Step 4: Broader DB-free regression sets (phase 3's sets)**

Run:
```powershell
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SecC|FullyQualifiedName~SecE|FullyQualifiedName~SecF|FullyQualifiedName~EffectivePermissions|FullyQualifiedName~TokenClassification|FullyQualifiedName~SecA"
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~TokenHelperTests|FullyQualifiedName~SecB|FullyQualifiedName~ApplicationSearch|FullyQualifiedName~ApplicationOwnerInvariant|FullyQualifiedName~S2SAllowedTenantsLogging|FullyQualifiedName~S2SAppRegistrationAllowlist|FullyQualifiedName~RegistrarGuard|FullyQualifiedName~ActionResultsTests|FullyQualifiedName~ApiVersionTelemetryMiddleware|FullyQualifiedName~DefaultAuthenticationMiddleware|FullyQualifiedName~SafeExchangeFunctionsStartup|FullyQualifiedName~TokenHelperHttpExtensions"
```
Expected: `Passed!  - Failed:     0, Passed:    66` and `Passed!  - Failed:     0, Passed:    69` (the second set
includes the two `SafeExchangeFunctionsStartupTests`, updated in Task 7).

- [ ] **Step 5 (optional, needs the Cosmos emulator): handler fixtures on Cosmos**

With the emulator running (master plan, §Commands):
```powershell
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchange.Tests.SecretMetaTests|FullyQualifiedName~SafeExchange.Tests.SecretContentMetaTests|FullyQualifiedName~SafeExchange.Tests.SecretStreamTests|FullyQualifiedName~SafeExchange.Tests.ContentCommitTests|FullyQualifiedName~SafeExchange.Tests.SecretAccessTests|FullyQualifiedName~SafeExchange.Tests.SecretAccessPatchTests|FullyQualifiedName~SafeExchange.Tests.SecretAccessAuditTests|FullyQualifiedName~SafeExchange.Tests.SecretAccessGiveUpTests|FullyQualifiedName~SafeExchange.Tests.SecretAccessRequestTests|FullyQualifiedName~SafeExchange.Tests.UserTests"
```
Expected: the same result as Task 0, Step 4; where the emulator works (e.g. in CI) all 136 tests pass. These
fixtures build the handlers with the old constructors, so they run `GlobalFiltersCallerResolver` and the
services against real Cosmos queries.

- [ ] **Step 6: Check the working tree and commit**

Run: `git status --short`
Expected exactly:
```text
 M docs/architecture.md
?? docs/superpowers/plans/2026-11-21-mcp-p4-services.md
```
Nothing under `SafeExchange.Tests/Contract/` may appear in `git --no-pager log --stat -7` either.

```powershell
git add docs/architecture.md docs/superpowers/plans/2026-11-21-mcp-p4-services.md
git commit -m "docs: phase 4 services plan and project structure" -m "Adds the phase 4 implementation plan and documents SafeExchange.Core/Services and the handler adapters in docs/architecture.md." -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```
Expected: `2 files changed`.

---

## Risks and notes for later phases

- **Exception scopes (D2).** Exact for every step that can realistically fail (database and blob storage calls).
  Request body read/parse and response writing moved outside the operation part; they cannot fail on the
  Functions worker (buffered body, caught parse errors, plain DTOs). If a future host streams request bodies
  lazily, revisit this.
- **Unknown HTTP methods (D3).** No purge / access lookup before the 400 any more. Unreachable through the
  triggers; a consumer that calls `Run` directly with another method gets 400 also for a missing secret.
- **Two caller resolvers.** Handlers built with the old constructors use `GlobalFiltersCallerResolver` (old
  preamble, `GlobalFilters` log category); DI-built handlers use `CallerResolver` (phase 3; its category). Both
  produce the same `Caller` fields REST uses (`SubjectType`, `SubjectId`); only `DisplayName` differs for users
  (subject id vs token name), and no service reads it. Phase 6 (MCP) must use DI-built services, never the old
  constructors.
- **Service logs from old constructors are dropped** (`NullLogger`). Production uses DI, so only tests and
  hand-built consumers are affected; the phase 7 consumer migration note should recommend DI.
- **Commit is on `ContentService` (D7),** not `ChunkService` as in the spec table; phase 6's
  `file_upload_complete` and the phase 7 docs must say so.
- **`ExpectedIndex` and the lock.** A retried first chunk without the ticket (lost response) still gets
  `"Content is being updated."` 422 because the lock check runs first; the MCP tool should surface the ticket
  from `file_upload_begin`/first chunk and, after a lost first response, drop the content (`ContentService.DropAsync`)
  or wait for lock expiry. Rejections never take the lock.
- **Deferred downloads.** Nothing is read or audited until `ContentDownload.WriteToAsync` runs. MCP must call it
  (into a `MemoryStream`) before base64-encoding; skipping it means no `ContentRead` event, which is correct
  because no bytes left the server.
- **Phase 5 (audit attribution)** adds `channel`/`clientLabel` to `IAuditWriter.AppendAsync`; every call site is
  now in `SecretService`, `ContentService`, `ChunkService` and `AccessService` (plus the purger), each of which
  has the `Caller` at hand.
