# Phase 1 — REST contract (characterization) tests — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Pin the exact current wire behaviour (status code, JSON body, headers, raw bytes) of the six
SPA-used handlers — `SafeExchangeSecretMeta`, `SafeExchangeSecretContentMeta`, `SafeExchangeSecretStream`,
`SafeExchangeContentCommit`, `SafeExchangeAccess`, `SafeExchangeSecretAudit` — so phases 2–4 (project split,
caller resolver, service extraction) can prove REST stays byte-identical.

**Architecture:** New test-only folder `SafeExchange.Tests\Contract\` (namespace `SafeExchange.Tests.Contract`).
Three helpers — `ContractJson` (canonicalise + normalise), `ContractAssert` (status + exact headers + body),
`ContractTestBase` (fresh EF InMemory database per test, fixed clock, production serializer settings, real
`TokenHelper`, all six handlers constructed in one place) — and one NUnit fixture per handler area.
**No production code changes in this phase.**

**Tech stack:** .NET 10, NUnit 4.6, Moq 4.20, EF Core InMemory 10.0.10, Azure Functions Worker 2.52 /
Http extension 3.3, `System.Text.Json` (`JsonNode`).

**Master plan:** `docs/superpowers/plans/2026-11-21-mcp-endpoint.md` (ground rules, style, commands).
**Spec:** `docs/superpowers/specs/2026-11-21-mcp-endpoint-design.md` (§Testing 1, §REST compatibility contract).

**Verified baseline:** every file in this plan was compiled and run against commit `b15d3a0`
(`feat/mcp-endpoint`) in a scratch worktree: **83/83 tests pass** (79 contract tests + 4 normaliser
self-tests), 3 consecutive runs, no warnings in the new files, and the suite also passes when run together
with the neighbouring InMemory fixtures (`SecC_*`, `SecE*`, `SecF*`, `SecB_*`, `EffectivePermissionsTests`,
`ActionResultsTests`, `ApplicationSearchTests`: 152/152). The Cosmos emulator is **not** needed for this phase.

---

## Ground rules for this phase

1. **Only new files under `SafeExchange.Tests\Contract\`.** Do not touch production code, existing tests or
   test utilities.
2. **Characterization rule.** Expected literals describe the *current* code. If a contract test fails because
   an expected literal differs from the actual output: re-read the handler code path, confirm the actual
   output is what the current code produces, and **fix the EXPECTED value** (never production code). Add a
   one-line `// Quirk: ...` comment only if the behaviour is surprising (the plan already marks the known
   quirks). If a failure is *not* a literal mismatch (exception, wrong status because the arrange step is
   wrong), fix the test's arrange code, not the handler.
3. **Frozen afterwards.** From phase 2 on these tests must not be edited to make a later phase pass. The only
   permitted edits are `using`/namespace changes and construction plumbing, and all construction lives in
   `ContractTestBase.ContractSetUp` so that is the only place to touch.
4. **Style** (master plan): file header `/// <summary>\n/// TypeName\n/// </summary>`, block namespace,
   `using` directives inside the namespace, `this.` prefix on instance members, 4-space indent, NUnit
   `Assert.That(..., Is....)`, no new comments except where a line needs clarifying (the `// Quirk:` lines).
   The test project has no implicit usings — every file lists its usings. Files are CRLF, no BOM (match the
   repository).
5. **No commits until Task 8.** The phase is committed once, after all contract tests are green together.

## Design decisions

### Database: EF Core InMemory for all six fixtures

None of the six handlers issues a Cosmos-specific query (no raw SQL, no `EF.Functions`, no Cosmos-only
operators); everything is `FirstOrDefaultAsync` / `Where` / `FindAsync` / `OrderBy` / `Take` / `Contains`
over entities whose owned collections (`ObjectMetadata.Content`, `ContentMetadata.Chunks`,
`ExpirationMetadata`) load with their owner on InMemory. Each test gets its own database
(`UseInMemoryDatabase($"{GetType().Name}-{Guid.NewGuid()}")`), so tests are isolated, need no teardown
cleanup and see a deterministic (insertion) order for multi-row lists.

| Fixture | DB | Reason |
|---|---|---|
| `SecretMetaContractTests` | InMemory | `Objects`/`Permissions` lookups and the tag filter (`names.Contains(o.ObjectName)`, `o.Tags.Contains(tag)`) translate on InMemory (verified); `SecE2_SecretMetaAuthzTests` precedent. |
| `ContentMetaContractTests` | InMemory | Owned `Content`/`Chunks` collections are mutated and saved in place; `SecE2_SecretMetaAuthzTests` already runs this handler on InMemory. |
| `StreamContractTests` | InMemory | `Objects.FindAsync(secretId)` + owned collections work on InMemory (`SecF2_SecretStreamReadNoPurgeTests` precedent); blobs go to `TestBlobHelper`. |
| `ContentCommitContractTests` | InMemory | Same `FindAsync` + owned-content access as the stream handler; the running hash state is a plain `byte[]` column. |
| `AccessContractTests` | InMemory | `SecC_PermissionsAuthzTests` runs this handler on InMemory; composite-key `Permissions.FindAsync` and `GroupDictionary` lookups work. |
| `SecretAuditContractTests` | InMemory | The real `AuditWriter` writes through an `IDbContextFactory` built on the same InMemory options (same store); the audit page query (`Where`/`OrderBy`/`Take`) translates on InMemory. |

### Caller identity: the real `SafeExchange.Core.TokenHelper`

`TestTokenHelper.IsUserToken` always returns `true` and `GetApplicationClientId` returns `""`, so it cannot
model S2S callers. The base therefore uses the production `TokenHelper(IConfiguration, ILogger<TokenHelper>)`
with an empty `Authentication` section (its `UpnClaims` default is `upn,…/claims/upn`). Users carry
`upn`/`oid`/`tid`; apps carry the v2 app-only shape `azp` + `azpacr` (no scope claim), which
`TokenClassification.IsAppOnlyToken` classifies as an application, and `SubjectHelper` resolves to the
**enabled** `Application` row's `DisplayName`. This is exactly the production path, and `TokenHelper` stays in
Core after phase 2 (only `GetAccountIdAndToken` moves to an extension, master plan decision 2), so the
contract tests need no edits when the interface changes.

### Serializer

`ActionResults.CreateResponseAsync` → `response.WriteAsJsonAsync(value)` (worker
`HttpResponseDataExtensions`): resolves `response.FunctionContext.InstanceServices
.GetService<IOptions<WorkerOptions>>().Value.Serializer`, adds `Content-Type: application/json; charset=utf-8`
and leaves the status code untouched. In production the worker's `WorkerOptionsSetup` sets
`Serializer ??= new JsonObjectSerializer(IOptions<JsonSerializerOptions>.Value)`, and
`SafeExchangeStartup.ConfigureServices` configures those options with `PropertyNamingPolicy = CamelCase` and
`DefaultIgnoreCondition = WhenWritingNull` (worker defaults add `PropertyNameCaseInsensitive = true`, which
only affects reads). No custom `Encoder` and no `JsonStringEnumConverter` → enums are integers and `'` is
written as `\u0027`.

`TestHttpRequestData.CreateResponse()` builds `TestHttpResponseData(FunctionContext)` from the **static,
shared** `TestFactory.FunctionContext`, which other fixtures overwrite in their `OneTimeSetUp` with a
default (PascalCase) serializer. `ContractTestBase` therefore sets it in `[SetUp]` (per test) and restores the
previous value — and the previous `DateTimeProvider` state — in `[TearDown]`.

### Comparison and normalisation

- `ContractAssert.Json(response, status, expectedJson, literals?, params maskedProperties)` asserts:
  1. the status code;
  2. the **exact header set**: only `Content-Type: application/json; charset=utf-8`;
  3. the raw body is **compact default-encoder System.Text.Json** (`ContractJson.IsWireCompact`: re-serialising
     the parsed tree with default options reproduces the raw text byte for byte — so no whitespace, ordering or
     escaping difference can hide behind the canonical comparison);
  4. `ContractJson.Normalize(raw, …) == ContractJson.Canonical(expectedJson)` with `Assert.That(actual,
     Is.EqualTo(expected))`. Canonical = `JsonNode` round-trip, compact, wire property order kept,
     `UnsafeRelaxedJsonEscaping` so expected literals are readable (`'` instead of `\u0027`).
- Normalisation (applied to the actual body only):
  - **masked properties**: every *non-empty* string value of a named property becomes `"<name>"`
    (`accessTicket`, audit `hash`/`prevHash`). Empty strings stay visible — this is what pins the
    final-legacy-chunk `"accessTicket":""` quirk;
  - **correlation id**: `Internal error. Reference: <32 hex>` → `Internal error. Reference: <correlationId>`;
  - **literals**: generated content names (`BLOB-20261121100000-<guid>`) → `<main>` / `<att>`; chunk names
    follow as `<att>-00000000`.
- Made deterministic instead of masked: `DateTimeProvider` fixed at `2026-11-21T10:00:00Z` (audit
  `occurredAt`, content-name timestamp), expiry `2027-01-01T00:00:00Z`, chunk hashes are SHA-256 of fixed
  bytes (`hello` = `2cf24dba…9824`, `world` = `486ea462…b8a7`, `helloworld` = `936a185c…07af`).
- `ContractAssert.Bytes(response, status, expectedBody, params expectedHeaderLines)` pins raw downloads:
  status, the exact ordered header lines, and the exact body bytes.

### Route versions

| Entry point | v2 | v3 | Pinned by |
|---|---|---|---|
| `GET {v}/secret-list` | `RunList` | `RunListV3` | `ListV2_*` and `ListV3_*` in `SecretMetaContractTests` (same data, both shapes) |
| `GET {v}/access/{id}` | `Run(..., enrichedAccessList: false)` | `Run(..., enrichedAccessList: true)` | `GetV2_ReturnsPlainAccessList`, `GetV3_*` in `AccessContractTests` |
| every other route of the six handlers | same handler call, same arguments | same | one test covers both versions (`SafeSecret.cs`, `SafeAccess.cs` pass `apiVersion` only to the two calls above) |

### Pinned quirks (keep byte-identical)

- `SafeExchangeAccess` not-found: `Secret '{id}' not exists` — different wording, **no trailing period**.
- `SafeExchangeAccess` 400s use `"status":"error"` (not `bad_request`).
- `/all` download not-found: `Content '{id}' not exists.` vs chunk routes `Content '{id}' does not exist.`.
- Final (non-interim) legacy chunk upload returns `"accessTicket":""`.
- Content-meta update/drop/delete with an unknown content id → **500** `internal_exception` (`First()`).
- Content-meta update with `fileName` omitted → `"fileName":""`.
- Main-content upload is HTML-sanitised; its reported `length` is the sanitised byte length.
- Enums are integers (`"subjectType":0` / `100`); `subStatus` is always present (`""`); `result`/`error`
  are omitted when null.

### Not pinned here (and why)

- `204 No Content` when a global filter returns `shouldReturn` without a response — unreachable today
  (`GlobalAccessFilter` always builds a response). The reachable global-group denial **is** pinned.
- Request-method dispatch for every handler — pinned once (`UnknownMethod_BadRequest`); the switch is
  identical in all `Run*` methods that have one.
- Access-ticket expiry purge — side-effect behaviour, already covered by `SecretStreamTests`/`SecF2`; its
  wire responses are the same as the pinned not-ready/being-updated responses.

## File structure

| File | Responsibility |
|---|---|
| `SafeExchange.Tests\Contract\ContractJson.cs` | Canonical form, wire-compactness check, normalisation (mask, correlation id, literals). |
| `SafeExchange.Tests\Contract\ContractJsonTests.cs` | Self-tests of `ContractJson` (4). |
| `SafeExchange.Tests\Contract\ContractAssert.cs` | `Json(...)` and `Bytes(...)` assertions, header/body readers. |
| `SafeExchange.Tests\Contract\ContractTestBase.cs` | Per-test environment, principals, handler construction, arrange helpers. |
| `SafeExchange.Tests\Contract\SecretMetaContractTests.cs` | `Run` post/get/patch/delete, `RunList`, `RunListV3`, S2S + global-group gates (22). |
| `SafeExchange.Tests\Contract\ContentMetaContractTests.cs` | `Run` post/patch/delete, `RunDrop` (13). |
| `SafeExchange.Tests\Contract\StreamContractTests.cs` | Legacy + hashed chunk upload, chunk download, `/all` download (17). |
| `SafeExchange.Tests\Contract\ContentCommitContractTests.cs` | `Run` commit (9). |
| `SafeExchange.Tests\Contract\AccessContractTests.cs` | `Run` post/get(v2, v3)/delete/patch (13). |
| `SafeExchange.Tests\Contract\SecretAuditContractTests.cs` | `Run` audit page (5). |

Test counts: 22 + 13 + 17 + 9 + 13 + 5 = **79 contract tests**, plus 4 normaliser self-tests = **83**.

All commands run from the repository root `C:\Users\yurio\Documents\github\safeexchange`.

---

## Task 1: Contract helpers and normaliser self-test

**Files:**
- Create: `SafeExchange.Tests\Contract\ContractJsonTests.cs`
- Create: `SafeExchange.Tests\Contract\ContractJson.cs`
- Create: `SafeExchange.Tests\Contract\ContractAssert.cs`
- Create: `SafeExchange.Tests\Contract\ContractTestBase.cs`

- [ ] **Step 1: Write the failing normaliser self-test**

Create `SafeExchange.Tests\Contract\ContractJsonTests.cs`:

```csharp
/// <summary>
/// ContractJsonTests
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using NUnit.Framework;
    using System.Collections.Generic;

    [TestFixture]
    public class ContractJsonTests
    {
        private static readonly IReadOnlyDictionary<string, string> NoLiterals = new Dictionary<string, string>();

        [Test]
        public void Canonical_KeepsPropertyOrder_RemovesWhitespace_UnescapesQuotes()
        {
            var canonical = ContractJson.Canonical("{ \"b\": 1,\n \"a\": \"it\\u0027s\" }");

            Assert.That(canonical, Is.EqualTo("{\"b\":1,\"a\":\"it's\"}"));
        }

        [Test]
        public void IsWireCompact_AcceptsDefaultEncoderOutput_RejectsIndentedOrRelaxed()
        {
            Assert.That(ContractJson.IsWireCompact("{\"a\":\"it\\u0027s\",\"b\":[1,2]}"), Is.True);
            Assert.That(ContractJson.IsWireCompact("{ \"a\": 1 }"), Is.False);
            Assert.That(ContractJson.IsWireCompact("{\"a\":\"it's\"}"), Is.False);
        }

        [Test]
        public void Normalize_MasksNonEmptyNamedValues_AtAnyDepth_KeepsEmptyStrings()
        {
            var json = "{\"result\":{\"accessTicket\":\"abc-123\",\"items\":[{\"accessTicket\":\"\"},{\"hash\":\"x\"}]}}";

            var normalized = ContractJson.Normalize(json, new[] { "accessTicket", "hash" }, NoLiterals);

            Assert.That(
                normalized,
                Is.EqualTo("{\"result\":{\"accessTicket\":\"<accessTicket>\",\"items\":[{\"accessTicket\":\"\"},{\"hash\":\"<hash>\"}]}}"));
        }

        [Test]
        public void Normalize_ReplacesLiteralsAndCorrelationId()
        {
            var json = "{\"name\":\"BLOB-1-00000000\",\"error\":\"Internal error. Reference: 0123456789abcdef0123456789abcdef\"}";
            var literals = new Dictionary<string, string> { { "BLOB-1", "<main>" } };

            var normalized = ContractJson.Normalize(json, new string[0], literals);

            Assert.That(
                normalized,
                Is.EqualTo("{\"name\":\"<main>-00000000\",\"error\":\"Internal error. Reference: <correlationId>\"}"));
        }
    }
}
```

- [ ] **Step 2: Run it and observe the failure**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.Contract.ContractJsonTests"`

Expected: build FAILS with `error CS0103: The name 'ContractJson' does not exist in the current context`.

- [ ] **Step 3: Implement `ContractJson`**

Create `SafeExchange.Tests\Contract\ContractJson.cs`:

```csharp
/// <summary>
/// ContractJson
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Encodings.Web;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Canonical form for REST contract comparisons: compact, wire property order kept,
    /// relaxed escaping so expected literals stay readable. Volatile values are replaced
    /// by placeholder tokens before comparison.
    /// </summary>
    public static class ContractJson
    {
        public const string CorrelationIdToken = "<correlationId>";

        private static readonly JsonSerializerOptions CanonicalOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private static readonly JsonSerializerOptions WireOptions = new();

        private static readonly Regex CorrelationIdPattern = new(
            "Internal error\\. Reference: [0-9a-f]{32}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static string Canonical(string json)
            => Parse(json).ToJsonString(CanonicalOptions);

        /// <summary>
        /// True when the text is exactly what System.Text.Json writes with default options
        /// (compact, default encoder) for its own tree, i.e. the canonical comparison does
        /// not hide whitespace, ordering or escaping differences on the wire.
        /// </summary>
        public static bool IsWireCompact(string json)
            => string.Equals(Parse(json).ToJsonString(WireOptions), json, StringComparison.Ordinal);

        /// <summary>
        /// Canonicalises <paramref name="json"/>, replaces every non-empty string value of a
        /// property named in <paramref name="maskedProperties"/> by <c>"&lt;name&gt;"</c>,
        /// replaces the TryCatch correlation id by <see cref="CorrelationIdToken"/>, then
        /// replaces each literal key of <paramref name="literals"/> by its token.
        /// </summary>
        public static string Normalize(string json, IEnumerable<string> maskedProperties, IReadOnlyDictionary<string, string> literals)
        {
            var node = Parse(json);
            var masked = new HashSet<string>(maskedProperties, StringComparer.Ordinal);
            if (masked.Count > 0)
            {
                Mask(node, masked);
            }

            var canonical = node.ToJsonString(CanonicalOptions);
            canonical = CorrelationIdPattern.Replace(canonical, $"Internal error. Reference: {CorrelationIdToken}");
            foreach (var literal in literals)
            {
                canonical = canonical.Replace(literal.Key, literal.Value, StringComparison.Ordinal);
            }

            return canonical;
        }

        private static JsonNode Parse(string json)
            => JsonNode.Parse(json) ?? throw new InvalidOperationException("Response body is the JSON literal null.");

        private static void Mask(JsonNode? node, HashSet<string> masked)
        {
            switch (node)
            {
                case JsonObject jsonObject:
                    foreach (var name in jsonObject.Select(p => p.Key).ToList())
                    {
                        var child = jsonObject[name];
                        if (masked.Contains(name)
                            && child is JsonValue value
                            && value.TryGetValue<string>(out var text)
                            && text.Length > 0)
                        {
                            jsonObject[name] = JsonValue.Create($"<{name}>");
                        }
                        else
                        {
                            Mask(child, masked);
                        }
                    }

                    break;

                case JsonArray jsonArray:
                    foreach (var item in jsonArray)
                    {
                        Mask(item, masked);
                    }

                    break;
            }
        }
    }
}
```

- [ ] **Step 4: Run the self-test green**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.Contract.ContractJsonTests"`

Expected: `Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4`

- [ ] **Step 5: Add `ContractAssert`**

Create `SafeExchange.Tests\Contract\ContractAssert.cs`:

```csharp
/// <summary>
/// ContractAssert
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using Microsoft.Azure.Functions.Worker.Http;
    using NUnit.Framework;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;

    public static class ContractAssert
    {
        public const string JsonContentType = "application/json; charset=utf-8";

        private static readonly IReadOnlyDictionary<string, string> NoLiterals = new Dictionary<string, string>();

        /// <summary>
        /// Asserts status, the exact header set (only Content-Type for JSON responses) and the
        /// body: the wire text must be compact default-encoded JSON and, after normalisation,
        /// equal the canonical form of <paramref name="expectedJson"/>.
        /// </summary>
        public static void Json(
            HttpResponseData response,
            HttpStatusCode expectedStatus,
            string expectedJson,
            IReadOnlyDictionary<string, string>? literals = null,
            params string[] maskedProperties)
        {
            var actual = AsTest(response);
            Assert.That(actual.StatusCode, Is.EqualTo(expectedStatus));
            Assert.That(HeaderLines(actual), Is.EqualTo(new[] { $"Content-Type: {JsonContentType}" }));

            var raw = actual.ReadBodyAsString();
            Assert.That(ContractJson.IsWireCompact(raw), Is.True, $"Wire body is not compact default-encoded JSON: {raw}");
            Assert.That(
                ContractJson.Normalize(raw, maskedProperties, literals ?? NoLiterals),
                Is.EqualTo(ContractJson.Canonical(expectedJson)));
        }

        /// <summary>
        /// Asserts status, the exact header set (in order) and the exact body bytes of a raw
        /// (non-JSON) response.
        /// </summary>
        public static void Bytes(
            HttpResponseData response,
            HttpStatusCode expectedStatus,
            byte[] expectedBody,
            params string[] expectedHeaderLines)
        {
            var actual = AsTest(response);
            Assert.That(actual.StatusCode, Is.EqualTo(expectedStatus));
            Assert.That(HeaderLines(actual), Is.EqualTo(expectedHeaderLines));
            Assert.That(ReadBodyBytes(actual), Is.EqualTo(expectedBody));
        }

        public static TestHttpResponseData AsTest(HttpResponseData response)
        {
            Assert.That(response, Is.InstanceOf<TestHttpResponseData>());
            return (TestHttpResponseData)response;
        }

        public static string[] HeaderLines(HttpResponseData response)
            => response.Headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}").ToArray();

        public static byte[] ReadBodyBytes(HttpResponseData response)
        {
            using var copy = new MemoryStream();
            response.Body.Seek(0, SeekOrigin.Begin);
            response.Body.CopyTo(copy);
            return copy.ToArray();
        }
    }
}
```

- [ ] **Step 6: Add `ContractTestBase`**

Create `SafeExchange.Tests\Contract\ContractTestBase.cs`. Constructor parameter orders used here were checked
against the source: `SafeExchangeSecretMeta`/`SafeExchangeSecretContentMeta`/`SafeExchangeContentCommit`
`(IConfiguration, SafeExchangeDbContext, ITokenHelper, GlobalFilters, IPurger, IPermissionsManager, IAuditWriter)`;
`SafeExchangeSecretStream` `(IConfiguration, SafeExchangeDbContext, ITokenHelper, GlobalFilters, IPurger, IBlobHelper, IPermissionsManager, IAuditWriter)`;
`SafeExchangeAccess` `(SafeExchangeDbContext, IGroupsManager, ITokenHelper, GlobalFilters, IPurger, IPermissionsManager, IAuditWriter, IOrphanedSecretManager)`;
`SafeExchangeSecretAudit` `(SafeExchangeDbContext, ITokenHelper, GlobalFilters, IPermissionsManager)`;
`GlobalFilters` `(IOptionsMonitor<GloballyAllowedGroupsConfiguration>, IOptionsMonitor<AdminConfiguration>, ITokenHelper, ILogger<GlobalFilters>)`;
`PermissionsManager` `(IConfiguration, SafeExchangeDbContext, ILogger<PermissionsManager>)`;
`PurgeManager` `(IConfiguration, IBlobHelper, ILogger<PurgeManager>)`;
`OrphanedSecretManager` `(IOptionsMonitor<Features>, IOptionsMonitor<OrphanedSecretConfiguration>, ILogger<OrphanedSecretManager>)`
(`UseAccessGiveUp` defaults to `false`, so the orphan rule is a no-op);
`GroupsManager` `(SafeExchangeDbContext, ILogger<GroupsManager>)`;
`AuditWriter` `(IDbContextFactory<SafeExchangeDbContext>, ILogger<AuditWriter>)`;
`TokenHelper` `(IConfiguration, ILogger<TokenHelper>)`.

```csharp
/// <summary>
/// ContractTestBase
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using Azure.Core.Serialization;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;
    using Microsoft.IdentityModel.Tokens;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Groups;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;

    /// <summary>
    /// Shared fixture plumbing for the REST contract (characterization) tests. Every test
    /// gets a fresh EF InMemory database, a fixed clock, the production response serializer
    /// settings and freshly constructed handlers. Handler construction lives only here so
    /// later phases change plumbing in one place.
    /// </summary>
    public abstract class ContractTestBase
    {
        protected const string TenantId = "00000000-0000-0000-0000-000000000001";

        protected const string OwnerUpn = "owner@contract.test";

        protected const string ReaderUpn = "reader@contract.test";

        protected const string StrangerUpn = "stranger@contract.test";

        protected const string AgentClientId = "00000000-0000-0000-0000-0000000000c1";

        protected const string AgentDisplayName = "contract-agent";

        protected const string UnregisteredClientId = "00000000-0000-0000-0000-0000000000c9";

        protected static readonly DateTime FixedNow = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);

        protected static readonly DateTime FixedExpireAt = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private IServiceProvider previousInstanceServices = null!;

        private bool previousUseSpecifiedDateTime;

        private DateTime previousSpecifiedDateTime;

        protected ILogger Logger { get; private set; } = null!;

        protected SafeExchangeDbContext DbContext { get; private set; } = null!;

        protected TestBlobHelper BlobHelper { get; private set; } = null!;

        /// <summary>
        /// The live GlobalAccessFilter configuration (read per request); empty means no global group gate.
        /// </summary>
        protected GloballyAllowedGroupsConfiguration GlobalAccessGroups { get; private set; } = null!;

        protected SafeExchangeSecretMeta SecretMeta { get; private set; } = null!;

        protected SafeExchangeSecretContentMeta ContentMeta { get; private set; } = null!;

        protected SafeExchangeSecretStream SecretStream { get; private set; } = null!;

        protected SafeExchangeContentCommit ContentCommit { get; private set; } = null!;

        protected SafeExchangeAccess SecretAccess { get; private set; } = null!;

        protected SafeExchangeSecretAudit SecretAudit { get; private set; } = null!;

        protected ClaimsPrincipal Owner => UserPrincipal(OwnerUpn, "00000000-0000-0000-0000-0000000000a1");

        protected ClaimsPrincipal Reader => UserPrincipal(ReaderUpn, "00000000-0000-0000-0000-0000000000a2");

        protected ClaimsPrincipal Stranger => UserPrincipal(StrangerUpn, "00000000-0000-0000-0000-0000000000a3");

        protected ClaimsPrincipal Agent => AppPrincipal(AgentClientId);

        protected ClaimsPrincipal UnregisteredApp => AppPrincipal(UnregisteredClientId);

        [SetUp]
        public void ContractSetUp()
        {
            this.previousInstanceServices = TestFactory.FunctionContext.InstanceServices;
            this.previousUseSpecifiedDateTime = DateTimeProvider.UseSpecifiedDateTime;
            this.previousSpecifiedDateTime = DateTimeProvider.SpecifiedDateTime;

            DateTimeProvider.SpecifiedDateTime = FixedNow;
            DateTimeProvider.UseSpecifiedDateTime = true;

            // Mirrors production: the worker's JsonObjectSerializer is built from the
            // JsonSerializerOptions configured in SafeExchangeStartup.ConfigureServices
            // (plus the worker default PropertyNameCaseInsensitive, which only affects reads).
            var serializer = new JsonObjectSerializer(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNameCaseInsensitive = true,
            });
            var workerOptions = Options.Create(new WorkerOptions { Serializer = serializer });
            var serviceProvider = new Mock<IServiceProvider>();
            serviceProvider
                .Setup(x => x.GetService(typeof(IOptions<WorkerOptions>)))
                .Returns(workerOptions);
            TestFactory.FunctionContext.InstanceServices = serviceProvider.Object;

            this.Logger = TestFactory.CreateLogger();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "Features:UseGroupsAuthorization", "False" },
                    { "AccessTickets:AccessTicketTimeout", "00:10:00" },
                })
                .Build();

            var dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseInMemoryDatabase($"{this.GetType().Name}-{Guid.NewGuid()}")
                .Options;
            this.DbContext = new SafeExchangeDbContext(dbContextOptions);

            var tokenHelper = new TokenHelper(configuration, NullLogger<TokenHelper>.Instance);

            var allowedGroups = new GloballyAllowedGroupsConfiguration { AllowedGroups = string.Empty };
            this.GlobalAccessGroups = allowedGroups;
            var adminConfiguration = new AdminConfiguration();
            var globalFilters = new GlobalFilters(
                Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(x => x.CurrentValue == allowedGroups),
                Mock.Of<IOptionsMonitor<AdminConfiguration>>(x => x.CurrentValue == adminConfiguration),
                tokenHelper,
                NullLogger<GlobalFilters>.Instance);

            this.BlobHelper = new TestBlobHelper();
            var purger = new PurgeManager(configuration, this.BlobHelper, NullLogger<PurgeManager>.Instance);
            var permissionsManager = new PermissionsManager(configuration, this.DbContext, NullLogger<PermissionsManager>.Instance);
            var groupsManager = new GroupsManager(this.DbContext, NullLogger<GroupsManager>.Instance);
            var features = new Features();
            var orphanedSecretConfiguration = new OrphanedSecretConfiguration();
            var orphanedSecretManager = new OrphanedSecretManager(
                Mock.Of<IOptionsMonitor<Features>>(x => x.CurrentValue == features),
                Mock.Of<IOptionsMonitor<OrphanedSecretConfiguration>>(x => x.CurrentValue == orphanedSecretConfiguration),
                NullLogger<OrphanedSecretManager>.Instance);
            var auditWriter = new AuditWriter(new ContractDbContextFactory(dbContextOptions), NullLogger<AuditWriter>.Instance);

            this.SecretMeta = new SafeExchangeSecretMeta(
                configuration, this.DbContext, tokenHelper, globalFilters, purger, permissionsManager, auditWriter);
            this.ContentMeta = new SafeExchangeSecretContentMeta(
                configuration, this.DbContext, tokenHelper, globalFilters, purger, permissionsManager, auditWriter);
            this.SecretStream = new SafeExchangeSecretStream(
                configuration, this.DbContext, tokenHelper, globalFilters, purger, this.BlobHelper, permissionsManager, auditWriter);
            this.ContentCommit = new SafeExchangeContentCommit(
                configuration, this.DbContext, tokenHelper, globalFilters, purger, permissionsManager, auditWriter);
            this.SecretAccess = new SafeExchangeAccess(
                this.DbContext, groupsManager, tokenHelper, globalFilters, purger, permissionsManager, auditWriter, orphanedSecretManager);
            this.SecretAudit = new SafeExchangeSecretAudit(
                this.DbContext, tokenHelper, globalFilters, permissionsManager);
        }

        [TearDown]
        public void ContractTearDown()
        {
            this.DbContext.Database.EnsureDeleted();
            this.DbContext.Dispose();

            // TestFactory.FunctionContext and DateTimeProvider are process-wide; restore them so
            // fixtures that configure them once in OneTimeSetUp are unaffected by these tests.
            TestFactory.FunctionContext.InstanceServices = this.previousInstanceServices;
            DateTimeProvider.SpecifiedDateTime = this.previousSpecifiedDateTime;
            DateTimeProvider.UseSpecifiedDateTime = this.previousUseSpecifiedDateTime;
        }

        protected static ClaimsPrincipal UserPrincipal(string upn, string objectId)
            => new(new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new("upn", upn),
                new("oid", objectId),
                new("tid", TenantId),
            }.AsEnumerable()));

        // App-only (client-credentials) v2 token shape: azp + azpacr and no scope claim.
        protected static ClaimsPrincipal AppPrincipal(string clientId)
            => new(new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new("azp", clientId),
                new("azpacr", "1"),
                new("oid", "00000000-0000-0000-0000-0000000000b1"),
                new("tid", TenantId),
            }.AsEnumerable()));

        protected static TestHttpRequestData Request(string method)
        {
            var request = TestFactory.CreateHttpRequestData(method);
            request.SetBodyAsStream(new MemoryStream());
            return request;
        }

        protected static TestHttpRequestData JsonRequest<T>(string method, T body)
        {
            var request = TestFactory.CreateHttpRequestData(method);
            request.SetBodyAsJson(body);
            return request;
        }

        protected static TestHttpRequestData TextRequest(string method, string body)
            => BytesRequest(method, Encoding.UTF8.GetBytes(body));

        protected static TestHttpRequestData BytesRequest(string method, byte[] body)
        {
            var request = TestFactory.CreateHttpRequestData(method);
            request.SetBodyAsStream(new MemoryStream(body));
            return request;
        }

        protected static MetadataCreationInput CreationInput(bool? auditEnabled = null, List<string>? tags = null)
            => new()
            {
                ExpirationSettings = new ExpirationSettingsInput
                {
                    ScheduleExpiration = false,
                    ExpireAt = FixedExpireAt,
                    ExpireOnIdleTime = false,
                    IdleTimeToExpire = TimeSpan.FromDays(180),
                },
                Tags = tags,
                AuditEnabled = auditEnabled,
            };

        protected static IReadOnlyDictionary<string, string> Literals(params (string Value, string Token)[] pairs)
            => pairs.ToDictionary(p => p.Value, p => p.Token);

        protected static JsonNode ReadJson(HttpResponseData response)
            => JsonNode.Parse(ContractAssert.AsTest(response).ReadBodyAsString())
                ?? throw new InvalidOperationException("Response body is the JSON literal null.");

        protected static string Sha256Hex(byte[] data)
            => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        protected async Task CreateSecretAsync(string secretId, ClaimsPrincipal? creator = null, bool? auditEnabled = null, List<string>? tags = null)
        {
            var response = await this.SecretMeta.Run(
                JsonRequest("post", CreationInput(auditEnabled, tags)), secretId, creator ?? this.Owner, this.Logger);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Arrange: secret creation failed.");
        }

        protected async Task GrantAsync(
            string secretId,
            string subjectName,
            bool read,
            bool write = false,
            bool grant = false,
            bool revoke = false,
            SubjectTypeInput subjectType = SubjectTypeInput.User,
            ClaimsPrincipal? actor = null)
        {
            var input = new List<SubjectPermissionsInput>
            {
                new()
                {
                    SubjectType = subjectType,
                    SubjectName = subjectName,
                    CanRead = read,
                    CanWrite = write,
                    CanGrantAccess = grant,
                    CanRevokeAccess = revoke,
                },
            };
            var response = await this.SecretAccess.Run(JsonRequest("post", input), secretId, actor ?? this.Owner, this.Logger);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Arrange: grant failed.");
        }

        protected async Task<string> MainContentNameAsync(string secretId)
        {
            var secret = await this.DbContext.Objects.FirstAsync(o => o.ObjectName == secretId);
            return secret.Content.Single(c => c.IsMain).ContentName;
        }

        protected async Task<string> CreateAttachmentAsync(string secretId, string contentType = "application/pdf", string fileName = "report.pdf")
        {
            var input = new ContentMetadataCreationInput { ContentType = contentType, FileName = fileName, IsImage = false };
            var response = await this.ContentMeta.Run(JsonRequest("post", input), secretId, string.Empty, this.Owner, this.Logger);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Arrange: attachment creation failed.");
            return ReadJson(response)["result"]!["contentName"]!.GetValue<string>();
        }

        protected async Task<HttpResponseData> UploadChunkAsync(
            string secretId,
            string contentId,
            byte[] data,
            string? ticket = null,
            bool interim = false,
            string? chunkHash = null,
            ClaimsPrincipal? caller = null)
        {
            var request = BytesRequest("post", data);
            if (!string.IsNullOrEmpty(ticket))
            {
                request.Headers.Add(SafeExchangeSecretStream.AccessTicketHeaderName, ticket);
            }

            if (interim)
            {
                request.Headers.Add(SafeExchangeSecretStream.OperationTypeHeaderName, SafeExchangeSecretStream.InterimOperationType);
            }

            if (chunkHash is not null)
            {
                request.Headers.Add(SafeExchangeSecretStream.ChunkHashHeaderName, chunkHash);
                request.Headers.Add("Content-Length", data.Length.ToString());
            }

            return await this.SecretStream.Run(request, secretId, contentId, string.Empty, caller ?? this.Owner, this.Logger);
        }

        protected static string TicketOf(HttpResponseData response)
            => ReadJson(response)["result"]!["accessTicket"]!.GetValue<string>();

        protected async Task MarkAsPreviousVersionAsync(string secretId)
        {
            var secret = await this.DbContext.Objects.FirstAsync(o => o.ObjectName == secretId);
            secret.KeepInStorage = false;
            await this.DbContext.SaveChangesAsync();
        }

        protected async Task SeedApplicationAsync(string clientId, string displayName, bool enabled)
        {
            this.DbContext.Applications.Add(new Application
            {
                Id = Guid.NewGuid().ToString(),
                PartitionKey = Application.DefaultPartitionKey,
                Enabled = enabled,
                DisplayName = displayName,
                ContactEmail = "agent-owner@contract.test",
                AadClientId = clientId,
                AadTenantId = TenantId,
                CreatedAt = FixedNow,
                CreatedBy = "contract-test",
                ModifiedAt = DateTime.MinValue,
                ModifiedBy = string.Empty,
            });
            await this.DbContext.SaveChangesAsync();
        }

        private sealed class ContractDbContextFactory : IDbContextFactory<SafeExchangeDbContext>
        {
            private readonly DbContextOptions<SafeExchangeDbContext> options;

            public ContractDbContextFactory(DbContextOptions<SafeExchangeDbContext> options)
            {
                this.options = options ?? throw new ArgumentNullException(nameof(options));
            }

            public SafeExchangeDbContext CreateDbContext() => new(this.options);
        }
    }
}
```

- [ ] **Step 7: Build and check for new warnings**

Run: `dotnet build SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-incremental -v q "-clp:ForceNoAlign;NoSummary" 2>&1 | Select-String '\\Contract\\'`

Expected: no output (the build succeeds; pre-existing warnings in other test files are not printed by the
filter, and none mention `\Contract\`).

- [ ] **Step 8: Re-run the self-test**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.Contract"`

Expected: `Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4`

---

## Task 2: `SecretMetaContractTests` (`Run`, `RunList` v2, `RunListV3` v3, S2S and global-group gates)

**Files:**
- Create: `SafeExchange.Tests\Contract\SecretMetaContractTests.cs`

Covers: create ok (plain, audited + tag normalisation/dedup), 409 conflict, 400 missing expiration, 400
invalid name, 400 invalid tag, read ok/404/403/422, update ok/403, delete ok, unknown method 400, v2 list
(owner, tag filter, read-only grantee), v3 list (read-only grantee, registered app), unregistered app 403,
disabled app 403 (kill switch), global access group 403.

- [ ] **Step 1: Write the fixture**

Create `SafeExchange.Tests\Contract\SecretMetaContractTests.cs`:

```csharp
/// <summary>
/// SecretMetaContractTests
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using NUnit.Framework;
    using SafeExchange.Core.Model.Dto.Input;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// Pins the wire contract of SafeExchangeSecretMeta: Run (post/get/patch/delete),
    /// RunList (v2 secret-list) and RunListV3 (v3 secret-list). Run is route-version agnostic.
    /// </summary>
    [TestFixture]
    public class SecretMetaContractTests : ContractTestBase
    {
        [Test]
        public async Task Create_ReturnsCreatedMetadata()
        {
            var response = await this.SecretMeta.Run(JsonRequest("post", CreationInput()), "alpha", this.Owner, this.Logger);

            var main = await this.MainContentNameAsync("alpha");
            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"objectName":"alpha",
                 "content":[{"contentName":"<main>","isMain":true,"isImage":false,"contentType":"","fileName":"","isReady":false,"chunks":[]}],
                 "expirationSettings":{"scheduleExpiration":false,"expireAt":"2027-01-01T00:00:00Z","expireOnIdleTime":false,"idleTimeToExpire":"180.00:00:00"},
                 "tags":[],"auditEnabled":false}}
                """, Literals((main, "<main>")));
        }

        [Test]
        public async Task Create_WithAuditAndTags_ReturnsNormalisedTags()
        {
            var input = CreationInput(auditEnabled: true, tags: new List<string> { "Team:A", "beta", "team:a" });

            var response = await this.SecretMeta.Run(JsonRequest("post", input), "alpha", this.Owner, this.Logger);

            var main = await this.MainContentNameAsync("alpha");
            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"objectName":"alpha",
                 "content":[{"contentName":"<main>","isMain":true,"isImage":false,"contentType":"","fileName":"","isReady":false,"chunks":[]}],
                 "expirationSettings":{"scheduleExpiration":false,"expireAt":"2027-01-01T00:00:00Z","expireOnIdleTime":false,"idleTimeToExpire":"180.00:00:00"},
                 "tags":["team:a","beta"],"auditEnabled":true}}
                """, Literals((main, "<main>")));
        }

        [Test]
        public async Task Create_ExistingSecret_Conflict()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.SecretMeta.Run(JsonRequest("post", CreationInput()), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Conflict, """
                {"status":"conflict","subStatus":"","error":"Secret 'alpha' already exists."}
                """);
        }

        [Test]
        public async Task Create_WithoutExpirationSettings_BadRequest()
        {
            var response = await this.SecretMeta.Run(TextRequest("post", "{}"), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"Expiration settings are not provided."}
                """);
        }

        [Test]
        public async Task Create_InvalidSecretName_BadRequest()
        {
            var response = await this.SecretMeta.Run(JsonRequest("post", CreationInput()), "bad.name", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"Secret id must contain only English letters, digits, '-' or '_'."}
                """);
        }

        [Test]
        public async Task Create_InvalidTag_BadRequest()
        {
            var input = CreationInput(tags: new List<string> { "Bad Tag!" });

            var response = await this.SecretMeta.Run(JsonRequest("post", input), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"Tag 'Bad Tag!' must match ^[a-z0-9][a-z0-9:_-]*$."}
                """);
        }

        [Test]
        public async Task Read_ReturnsMetadata()
        {
            await this.CreateSecretAsync("alpha", tags: new List<string> { "team:a" });

            var response = await this.SecretMeta.Run(Request("get"), "alpha", this.Owner, this.Logger);

            var main = await this.MainContentNameAsync("alpha");
            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"objectName":"alpha",
                 "content":[{"contentName":"<main>","isMain":true,"isImage":false,"contentType":"","fileName":"","isReady":false,"chunks":[]}],
                 "expirationSettings":{"scheduleExpiration":false,"expireAt":"2027-01-01T00:00:00Z","expireOnIdleTime":false,"idleTimeToExpire":"180.00:00:00"},
                 "tags":["team:a"],"auditEnabled":false}}
                """, Literals((main, "<main>")));
        }

        [Test]
        public async Task Read_MissingSecret_NotFound()
        {
            var response = await this.SecretMeta.Run(Request("get"), "missing", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.NotFound, """
                {"status":"not_found","subStatus":"","error":"Secret 'missing' does not exist."}
                """);
        }

        [Test]
        public async Task Read_WithoutReadPermission_Forbidden()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.SecretMeta.Run(Request("get"), "alpha", this.Stranger, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Insufficient permissions to do 'Read' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task Read_PreviousVersionSecret_Unprocessable()
        {
            await this.CreateSecretAsync("alpha");
            await this.MarkAsPreviousVersionAsync("alpha");

            var response = await this.SecretMeta.Run(Request("get"), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Cannot use this endpoint for previous versions data."}
                """);
        }

        [Test]
        public async Task Update_ReturnsUpdatedMetadata()
        {
            await this.CreateSecretAsync("alpha");
            var input = new MetadataUpdateInput
            {
                ExpirationSettings = new ExpirationSettingsInput
                {
                    ScheduleExpiration = true,
                    ExpireAt = FixedExpireAt,
                    ExpireOnIdleTime = true,
                    IdleTimeToExpire = TimeSpan.FromDays(1),
                },
                Tags = new List<string> { "B" },
            };

            var response = await this.SecretMeta.Run(JsonRequest("patch", input), "alpha", this.Owner, this.Logger);

            var main = await this.MainContentNameAsync("alpha");
            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"objectName":"alpha",
                 "content":[{"contentName":"<main>","isMain":true,"isImage":false,"contentType":"","fileName":"","isReady":false,"chunks":[]}],
                 "expirationSettings":{"scheduleExpiration":true,"expireAt":"2027-01-01T00:00:00Z","expireOnIdleTime":true,"idleTimeToExpire":"1.00:00:00"},
                 "tags":["b"],"auditEnabled":false}}
                """, Literals((main, "<main>")));
        }

        [Test]
        public async Task Update_WithoutWritePermission_Forbidden()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, read: true);

            var response = await this.SecretMeta.Run(JsonRequest("patch", CreationInput()), "alpha", this.Reader, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Insufficient permissions to do 'Write' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task Delete_ReturnsOk()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.SecretMeta.Run(Request("delete"), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":"ok"}
                """);
        }

        [Test]
        public async Task UnknownMethod_BadRequest()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.SecretMeta.Run(Request("put"), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"error","subStatus":"","error":"Request method not recognized"}
                """);
        }

        [Test]
        public async Task ListV2_ReturnsDirectPermissionRowsWithTags()
        {
            await this.CreateSecretAsync("alpha");
            await this.CreateSecretAsync("beta", tags: new List<string> { "team:a" });

            var response = await this.SecretMeta.RunList(Request("get"), this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":[
                 {"objectName":"alpha","subjectType":0,"subjectName":"owner@contract.test","subjectId":"owner@contract.test","canRead":true,"canWrite":true,"canGrantAccess":true,"canRevokeAccess":true,"tags":[]},
                 {"objectName":"beta","subjectType":0,"subjectName":"owner@contract.test","subjectId":"owner@contract.test","canRead":true,"canWrite":true,"canGrantAccess":true,"canRevokeAccess":true,"tags":["team:a"]}]}
                """);
        }

        [Test]
        public async Task ListV2_TagFilter_ReturnsOnlyMatchingSecrets()
        {
            await this.CreateSecretAsync("alpha");
            await this.CreateSecretAsync("beta", tags: new List<string> { "team:a" });
            var request = Request("get");
            request.SetQueryString("tag=TEAM:A");

            var response = await this.SecretMeta.RunList(request, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":[
                 {"objectName":"beta","subjectType":0,"subjectName":"owner@contract.test","subjectId":"owner@contract.test","canRead":true,"canWrite":true,"canGrantAccess":true,"canRevokeAccess":true,"tags":["team:a"]}]}
                """);
        }

        [Test]
        public async Task ListV2_ForReadOnlyGrantee_ReturnsPermissionRow()
        {
            await this.CreateSecretAsync("alpha", tags: new List<string> { "team:a" });
            await this.GrantAsync("alpha", ReaderUpn, read: true);

            var response = await this.SecretMeta.RunList(Request("get"), this.Reader, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":[
                 {"objectName":"alpha","subjectType":0,"subjectName":"reader@contract.test","subjectId":"reader@contract.test","canRead":true,"canWrite":false,"canGrantAccess":false,"canRevokeAccess":false,"tags":["team:a"]}]}
                """);
        }

        [Test]
        public async Task ListV3_ForReadOnlyGrantee_AddsCallerEffectivePermissions()
        {
            await this.CreateSecretAsync("alpha", tags: new List<string> { "team:a" });
            await this.GrantAsync("alpha", ReaderUpn, read: true);

            var response = await this.SecretMeta.RunListV3(Request("get"), this.Reader, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":[
                 {"objectName":"alpha","subjectType":0,"subjectName":"reader@contract.test","subjectId":"reader@contract.test","canRead":true,"canWrite":false,"canGrantAccess":false,"canRevokeAccess":false,"tags":["team:a"],
                  "callerEffectivePermissions":{"canRead":true,"canWrite":false,"canGrantAccess":false,"canRevokeAccess":false}}]}
                """);
        }

        [Test]
        public async Task UnregisteredApplication_Forbidden()
        {
            var response = await this.SecretMeta.Run(JsonRequest("post", CreationInput()), "alpha", this.UnregisteredApp, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Application is not registered or disabled."}
                """);
        }

        [Test]
        public async Task DisabledApplication_ListV3_Forbidden()
        {
            await this.SeedApplicationAsync(AgentClientId, AgentDisplayName, enabled: false);

            var response = await this.SecretMeta.RunListV3(Request("get"), this.Agent, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Application is not registered or disabled."}
                """);
        }

        [Test]
        public async Task GlobalAccessGroupConfigured_CallerNotMember_Forbidden()
        {
            this.GlobalAccessGroups.AllowedGroups = "00000000-0000-0000-0000-0000000000d1";

            var response = await this.SecretMeta.RunListV3(Request("get"), this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Not a member of a global group."}
                """);
        }

        [Test]
        public async Task RegisteredApplication_ListV3_ShowsApplicationSubject()
        {
            await this.SeedApplicationAsync(AgentClientId, AgentDisplayName, enabled: true);
            await this.CreateSecretAsync("agent-secret", creator: this.Agent);

            var response = await this.SecretMeta.RunListV3(Request("get"), this.Agent, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":[
                 {"objectName":"agent-secret","subjectType":100,"subjectName":"contract-agent","subjectId":"contract-agent","canRead":true,"canWrite":true,"canGrantAccess":true,"canRevokeAccess":true,"tags":[],
                  "callerEffectivePermissions":{"canRead":true,"canWrite":true,"canGrantAccess":true,"canRevokeAccess":true}}]}
                """);
        }
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.Contract.SecretMetaContractTests"`

Expected: `Passed!  - Failed:     0, Passed:    22, Skipped:     0, Total:    22`.
These are characterization tests, so they are expected to pass on the first run. If one fails on a literal
mismatch, apply the characterization rule (ground rule 2).

- [ ] **Step 3: Prove the comparison is not vacuous (one-time check, then revert)**

In `Create_ExistingSecret_Conflict`, temporarily change `already exists.` to `already exists` in the expected
literal and run:
`dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~Create_ExistingSecret_Conflict"`

Expected: FAIL with
`Expected: "...ict","subStatus":"","error":"Secret 'alpha' already exists"}"` /
`But was:  "...ict","subStatus":"","error":"Secret 'alpha' already exists."}"`.
Revert the change and re-run Step 2 (22 passed).

---

## Task 3: `ContentMetaContractTests` (`Run` post/patch/delete, `RunDrop`)

**Files:**
- Create: `SafeExchange.Tests\Contract\ContentMetaContractTests.cs`

Covers: create ok (image), 400 content id on create, 403, 400 missing content type, 400 control characters in
content type, update ok (omitted `fileName` → `""`), 400 missing content id, unknown content → 500
`internal_exception`, drop ok, drop while uploading 422, delete ok, delete main 400, delete while uploading 422.

- [ ] **Step 1: Write the fixture**

Create `SafeExchange.Tests\Contract\ContentMetaContractTests.cs`:

```csharp
/// <summary>
/// ContentMetaContractTests
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using NUnit.Framework;
    using SafeExchange.Core.Model.Dto.Input;
    using System.Net;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Pins the wire contract of SafeExchangeSecretContentMeta: Run (post/patch/delete) and
    /// RunDrop (patch). Route-version agnostic.
    /// </summary>
    [TestFixture]
    public class ContentMetaContractTests : ContractTestBase
    {
        [Test]
        public async Task Create_ReturnsNewContent()
        {
            await this.CreateSecretAsync("alpha");
            var input = new ContentMetadataCreationInput { ContentType = "image/png", FileName = "pic.png", IsImage = true };

            var response = await this.ContentMeta.Run(JsonRequest("post", input), "alpha", string.Empty, this.Owner, this.Logger);

            var attachment = ReadJson(response)["result"]!["contentName"]!.GetValue<string>();
            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"contentName":"<att>","isMain":false,"isImage":true,"contentType":"image/png","fileName":"pic.png","isReady":false,"chunks":[]}}
                """, Literals((attachment, "<att>")));
        }

        [Test]
        public async Task Create_WithContentId_BadRequest()
        {
            await this.CreateSecretAsync("alpha");
            var input = new ContentMetadataCreationInput { ContentType = "application/pdf", FileName = "report.pdf" };

            var response = await this.ContentMeta.Run(JsonRequest("post", input), "alpha", "named", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"Cannot specify content name on creation."}
                """);
        }

        [Test]
        public async Task Create_WithoutWritePermission_Forbidden()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, read: true);
            var input = new ContentMetadataCreationInput { ContentType = "application/pdf", FileName = "report.pdf" };

            var response = await this.ContentMeta.Run(JsonRequest("post", input), "alpha", string.Empty, this.Reader, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Insufficient permissions to do 'Write' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task Create_WithoutContentType_BadRequest()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.ContentMeta.Run(TextRequest("post", "{\"fileName\":\"report.pdf\"}"), "alpha", string.Empty, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"Content settings are not provided."}
                """);
        }

        [Test]
        public async Task Create_ContentTypeWithControlCharacters_BadRequest()
        {
            await this.CreateSecretAsync("alpha");
            var input = new ContentMetadataCreationInput { ContentType = "text/plain\r\nX-Injected: 1", FileName = "a.txt" };

            var response = await this.ContentMeta.Run(JsonRequest("post", input), "alpha", string.Empty, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"Content type is in incorrect format."}
                """);
        }

        [Test]
        public async Task Update_ReturnsUpdatedContent_NullFileNameBecomesEmpty()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            var input = new ContentMetadataUpdateInput { ContentType = "text/plain", FileName = null! };

            var response = await this.ContentMeta.Run(JsonRequest("patch", input), "alpha", attachment, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"contentName":"<att>","isMain":false,"isImage":false,"contentType":"text/plain","fileName":"","isReady":false,"chunks":[]}}
                """, Literals((attachment, "<att>")));
        }

        [Test]
        public async Task Update_WithoutContentId_BadRequest()
        {
            await this.CreateSecretAsync("alpha");
            var input = new ContentMetadataUpdateInput { ContentType = "text/plain", FileName = "a.txt" };

            var response = await this.ContentMeta.Run(JsonRequest("patch", input), "alpha", string.Empty, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"Must specify content name on update."}
                """);
        }

        [Test]
        public async Task Update_UnknownContent_InternalError()
        {
            await this.CreateSecretAsync("alpha");
            var input = new ContentMetadataUpdateInput { ContentType = "text/plain", FileName = "a.txt" };

            var response = await this.ContentMeta.Run(JsonRequest("patch", input), "alpha", "missing-content", this.Owner, this.Logger);

            // Quirk: content lookups here use First(), so an unknown content id is a 500, not a 404.
            ContractAssert.Json(response, HttpStatusCode.InternalServerError, """
                {"status":"error","subStatus":"internal_exception","error":"Internal error. Reference: <correlationId>"}
                """);
        }

        [Test]
        public async Task Drop_ClearsChunksAndReturnsBlankContent()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Encoding.UTF8.GetBytes("hello"));

            var response = await this.ContentMeta.RunDrop(Request("patch"), "alpha", attachment, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"contentName":"<att>","isMain":false,"isImage":false,"contentType":"application/pdf","fileName":"report.pdf","isReady":false,"chunks":[]}}
                """, Literals((attachment, "<att>")));
        }

        [Test]
        public async Task Drop_WhileUploadInProgress_Unprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Encoding.UTF8.GetBytes("hello"), interim: true);

            var response = await this.ContentMeta.RunDrop(Request("patch"), "alpha", attachment, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Content is being updated."}
                """);
        }

        [Test]
        public async Task Delete_ReturnsOk()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");

            var response = await this.ContentMeta.Run(Request("delete"), "alpha", attachment, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":"ok"}
                """);
        }

        [Test]
        public async Task Delete_MainContent_BadRequest()
        {
            await this.CreateSecretAsync("alpha");
            var main = await this.MainContentNameAsync("alpha");

            var response = await this.ContentMeta.Run(Request("delete"), "alpha", main, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"Cannot delete main content."}
                """);
        }

        [Test]
        public async Task Delete_WhileUploadInProgress_Unprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Encoding.UTF8.GetBytes("hello"), interim: true);

            var response = await this.ContentMeta.Run(Request("delete"), "alpha", attachment, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Content is being updated."}
                """);
        }
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.Contract.ContentMetaContractTests"`

Expected: `Passed!  - Failed:     0, Passed:    13, Skipped:     0, Total:    13`. On a literal mismatch apply
ground rule 2.

---

## Task 4: `StreamContractTests` (chunk upload legacy + hashed, chunk download, `/all` download)

**Files:**
- Create: `SafeExchange.Tests\Contract\StreamContractTests.cs`

Covers: legacy interim upload (ticket returned), legacy final upload (`"accessTicket":""` quirk), main content
sanitised upload + HTML chunk download (default content type), hashed upload (server hash + ticket), chunk hash
mismatch 422 with `result`, upload lock conflict 422, inconsistent upload mode 400, chunk id on upload 400,
unknown content 404, chunk download bytes + `Content-Type`/`Content-Length`, not ready 422, being updated 422,
missing chunk id 400, unknown chunk 404, read forbidden 403, `/all` download bytes + `Content-Disposition`,
`/all` unknown content 404 (`not exists.` quirk).

- [ ] **Step 1: Write the fixture**

Create `SafeExchange.Tests\Contract\StreamContractTests.cs`:

```csharp
/// <summary>
/// StreamContractTests
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using NUnit.Framework;
    using System.Net;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Pins the wire contract of SafeExchangeSecretStream: Run (chunk upload — legacy and
    /// hashed — and chunk download) and RunContentDownload (/all). Route-version agnostic.
    /// </summary>
    [TestFixture]
    public class StreamContractTests : ContractTestBase
    {
        private static readonly byte[] Hello = Encoding.UTF8.GetBytes("hello");

        private static readonly byte[] World = Encoding.UTF8.GetBytes("world");

        [Test]
        public async Task LegacyUpload_Interim_ReturnsAccessTicket()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");

            var response = await this.UploadChunkAsync("alpha", attachment, Hello, interim: true);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"chunkName":"<att>-00000000","hash":"","length":5,"accessTicket":"<accessTicket>"}}
                """, Literals((attachment, "<att>")), "accessTicket");
        }

        [Test]
        public async Task LegacyUpload_FinalChunk_ReturnsEmptyAccessTicket()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            var ticket = TicketOf(await this.UploadChunkAsync("alpha", attachment, Hello, interim: true));

            var response = await this.UploadChunkAsync("alpha", attachment, World, ticket: ticket);

            // Quirk: the final legacy chunk returns accessTicket "" (cleared), not null/omitted.
            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"chunkName":"<att>-00000001","hash":"","length":5,"accessTicket":""}}
                """, Literals((attachment, "<att>")), "accessTicket");
        }

        [Test]
        public async Task LegacyUpload_MainContent_IsSanitised_AndDownloadsAsHtml()
        {
            await this.CreateSecretAsync("alpha");
            var main = await this.MainContentNameAsync("alpha");

            var upload = await this.UploadChunkAsync("alpha", main, Encoding.UTF8.GetBytes("<p>hi</p><script>alert(1)</script>"));

            ContractAssert.Json(upload, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"chunkName":"<main>-00000000","hash":"","length":9,"accessTicket":""}}
                """, Literals((main, "<main>")), "accessTicket");

            var download = await this.SecretStream.Run(Request("get"), "alpha", main, $"{main}-00000000", this.Owner, this.Logger);

            ContractAssert.Bytes(download, HttpStatusCode.OK, Encoding.UTF8.GetBytes("<p>hi</p>"),
                "Content-Type: text/html; charset=utf-8",
                "Content-Length: 9");
        }

        [Test]
        public async Task HashedUpload_ReturnsServerHashAndAccessTicket()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");

            var response = await this.UploadChunkAsync("alpha", attachment, Hello, chunkHash: Sha256Hex(Hello));

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"chunkName":"<att>-00000000","hash":"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824","length":5,"accessTicket":"<accessTicket>"}}
                """, Literals((attachment, "<att>")), "accessTicket");
        }

        [Test]
        public async Task HashedUpload_ChunkHashMismatch_Unprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");

            var response = await this.UploadChunkAsync("alpha", attachment, Hello, chunkHash: Sha256Hex(World));

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"chunk_hash_mismatch","subStatus":"",
                 "result":{"expected":"486ea46224d1bb4fb680f34f7c9ad96a8f24ec88be73ea8e5a6c65260e9cb8a7","actual":"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"},
                 "error":"Received chunk bytes do not match the client-asserted hash."}
                """);
        }

        [Test]
        public async Task Upload_WithoutTicketWhileLocked_Unprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Hello, interim: true);

            var response = await this.UploadChunkAsync("alpha", attachment, World);

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Content is being updated."}
                """);
        }

        [Test]
        public async Task Upload_HashedAfterLegacyChunk_InconsistentUploadMode()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            var ticket = TicketOf(await this.UploadChunkAsync("alpha", attachment, Hello, interim: true));

            var response = await this.UploadChunkAsync("alpha", attachment, World, ticket: ticket, interim: true, chunkHash: Sha256Hex(World));

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"inconsistent_upload_mode","subStatus":"","error":"Chunk-hash header usage is inconsistent with the content's upload mode."}
                """);
        }

        [Test]
        public async Task Upload_WithChunkId_BadRequest()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");

            var response = await this.SecretStream.Run(BytesRequest("post", Hello), "alpha", attachment, "00000000", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"Cannot specify chunk id on upload."}
                """);
        }

        [Test]
        public async Task Upload_UnknownContent_NotFound()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.UploadChunkAsync("alpha", "missing-content", Hello);

            ContractAssert.Json(response, HttpStatusCode.NotFound, """
                {"status":"not_found","subStatus":"","error":"Content 'missing-content' does not exist."}
                """);
        }

        [Test]
        public async Task ChunkDownload_ReturnsBytesWithContentHeaders()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Hello);

            var response = await this.SecretStream.Run(Request("get"), "alpha", attachment, $"{attachment}-00000000", this.Owner, this.Logger);

            ContractAssert.Bytes(response, HttpStatusCode.OK, Hello,
                "Content-Type: application/pdf",
                "Content-Length: 5");
        }

        [Test]
        public async Task ChunkDownload_ContentNotReady_Unprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");

            var response = await this.SecretStream.Run(Request("get"), "alpha", attachment, $"{attachment}-00000000", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Content '<att>' is not ready."}
                """, Literals((attachment, "<att>")));
        }

        [Test]
        public async Task ChunkDownload_WhileUploadInProgress_Unprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Hello, interim: true);

            var response = await this.SecretStream.Run(Request("get"), "alpha", attachment, $"{attachment}-00000000", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Content '<att>' is being updated."}
                """, Literals((attachment, "<att>")));
        }

        [Test]
        public async Task ChunkDownload_WithoutChunkId_BadRequest()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Hello);

            var response = await this.SecretStream.Run(Request("get"), "alpha", attachment, string.Empty, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"Must specify chunk id on download."}
                """);
        }

        [Test]
        public async Task ChunkDownload_UnknownChunk_NotFound()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Hello);

            var response = await this.SecretStream.Run(Request("get"), "alpha", attachment, "missing-chunk", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.NotFound, """
                {"status":"not_found","subStatus":"","error":"Chunk 'missing-chunk' does not exist."}
                """);
        }

        [Test]
        public async Task ChunkDownload_WithoutReadPermission_Forbidden()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Hello);

            var response = await this.SecretStream.Run(Request("get"), "alpha", attachment, $"{attachment}-00000000", this.Stranger, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Insufficient permissions to do 'Read' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task ContentDownload_ReturnsAllChunksWithDisposition()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            var ticket = TicketOf(await this.UploadChunkAsync("alpha", attachment, Hello, interim: true));
            await this.UploadChunkAsync("alpha", attachment, World, ticket: ticket);

            var response = await this.SecretStream.RunContentDownload(Request("get"), "alpha", attachment, this.Owner, this.Logger);

            ContractAssert.Bytes(response, HttpStatusCode.OK, Encoding.UTF8.GetBytes("helloworld"),
                "Content-Type: application/pdf",
                "Content-Length: 10",
                "Content-Disposition: attachment; filename=report.pdf");
        }

        [Test]
        public async Task ContentDownload_UnknownContent_NotFoundWithNotExistsWording()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.SecretStream.RunContentDownload(Request("get"), "alpha", "missing-content", this.Owner, this.Logger);

            // Quirk: /all says "not exists." while the chunk routes say "does not exist."
            ContractAssert.Json(response, HttpStatusCode.NotFound, """
                {"status":"not_found","subStatus":"","error":"Content 'missing-content' not exists."}
                """);
        }
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.Contract.StreamContractTests"`

Expected: `Passed!  - Failed:     0, Passed:    17, Skipped:     0, Total:    17`. On a literal mismatch apply
ground rule 2. (If the sanitiser test fails on the byte content or length, the HtmlSanitizer package changed
its output — re-derive both literals from the actual output; the contract is "whatever the handler emits".)

---

## Task 5: `ContentCommitContractTests` (`PATCH .../commit`)

**Files:**
- Create: `SafeExchange.Tests\Contract\ContentCommitContractTests.cs`

Covers: ok (content hash), `hash_mismatch` 422 with `result`, wrong ticket 401 `unauthorized`,
`no_upload_state` 422 (legacy upload in progress), `bad_request` 400 (not hex), `bad_request` 400 (not JSON),
main content 422, previous-version secret 422 (commit-specific wording), unknown content 404.

- [ ] **Step 1: Write the fixture**

Create `SafeExchange.Tests\Contract\ContentCommitContractTests.cs`:

```csharp
/// <summary>
/// ContentCommitContractTests
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using Microsoft.Azure.Functions.Worker.Http;
    using NUnit.Framework;
    using SafeExchange.Core.Functions;
    using System.Net;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Pins the wire contract of SafeExchangeContentCommit (PATCH .../commit). Route-version agnostic.
    /// </summary>
    [TestFixture]
    public class ContentCommitContractTests : ContractTestBase
    {
        private const string HelloWorldSha256 = "936a185caaa266bb9cbe981e9e05cb78cd732b0b3280eb944412bb6f8f8f07af";

        private static readonly byte[] Hello = Encoding.UTF8.GetBytes("hello");

        private static readonly byte[] World = Encoding.UTF8.GetBytes("world");

        [Test]
        public async Task Commit_MatchingHash_ReturnsContentHash()
        {
            var (attachment, ticket) = await this.UploadHashedHelloWorldAsync();

            var response = await this.CommitAsync(attachment, ticket, $"{{\"hash\":\"{HelloWorldSha256}\"}}");

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"contentName":"<att>","hash":"936a185caaa266bb9cbe981e9e05cb78cd732b0b3280eb944412bb6f8f8f07af"}}
                """, Literals((attachment, "<att>")));
        }

        [Test]
        public async Task Commit_DifferentHash_HashMismatch()
        {
            var (attachment, ticket) = await this.UploadHashedHelloWorldAsync();

            var response = await this.CommitAsync(attachment, ticket, $"{{\"hash\":\"{Sha256Hex(Hello)}\"}}");

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"hash_mismatch","subStatus":"",
                 "result":{"expected":"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824","actual":"936a185caaa266bb9cbe981e9e05cb78cd732b0b3280eb944412bb6f8f8f07af"},
                 "error":"Client-asserted whole-content hash does not match server computation."}
                """);
        }

        [Test]
        public async Task Commit_WrongTicket_Unauthorized()
        {
            var (attachment, _) = await this.UploadHashedHelloWorldAsync();

            var response = await this.CommitAsync(attachment, "not-the-ticket", $"{{\"hash\":\"{HelloWorldSha256}\"}}");

            ContractAssert.Json(response, HttpStatusCode.Unauthorized, """
                {"status":"unauthorized","subStatus":"","error":"Access ticket missing or invalid."}
                """);
        }

        [Test]
        public async Task Commit_LegacyUploadInProgress_NoUploadState()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            var ticket = TicketOf(await this.UploadChunkAsync("alpha", attachment, Hello, interim: true));

            var response = await this.CommitAsync(attachment, ticket, $"{{\"hash\":\"{Sha256Hex(Hello)}\"}}");

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"no_upload_state","subStatus":"","error":"No hashed-mode upload in progress for this content."}
                """);
        }

        [Test]
        public async Task Commit_HashNotHex_BadRequest()
        {
            var (attachment, ticket) = await this.UploadHashedHelloWorldAsync();

            var response = await this.CommitAsync(attachment, ticket, "{\"hash\":\"xyz\"}");

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"hash must be 64 hex characters."}
                """);
        }

        [Test]
        public async Task Commit_BodyNotJson_BadRequest()
        {
            var (attachment, ticket) = await this.UploadHashedHelloWorldAsync();

            var response = await this.CommitAsync(attachment, ticket, "not json");

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"Body is not valid JSON."}
                """);
        }

        [Test]
        public async Task Commit_MainContent_Unprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var main = await this.MainContentNameAsync("alpha");

            var response = await this.CommitAsync(main, "any-ticket", $"{{\"hash\":\"{HelloWorldSha256}\"}}");

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Main content does not support explicit commit."}
                """);
        }

        [Test]
        public async Task Commit_PreviousVersionSecret_Unprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.MarkAsPreviousVersionAsync("alpha");

            var response = await this.CommitAsync(attachment, "any-ticket", $"{{\"hash\":\"{HelloWorldSha256}\"}}");

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Cannot commit data for previous-version secrets."}
                """);
        }

        [Test]
        public async Task Commit_UnknownContent_NotFound()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.CommitAsync("missing-content", "any-ticket", $"{{\"hash\":\"{HelloWorldSha256}\"}}");

            ContractAssert.Json(response, HttpStatusCode.NotFound, """
                {"status":"not_found","subStatus":"","error":"Content 'missing-content' does not exist."}
                """);
        }

        private async Task<(string Attachment, string Ticket)> UploadHashedHelloWorldAsync()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            var ticket = TicketOf(await this.UploadChunkAsync("alpha", attachment, Hello, chunkHash: Sha256Hex(Hello)));
            var second = await this.UploadChunkAsync("alpha", attachment, World, ticket: ticket, chunkHash: Sha256Hex(World));
            Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Arrange: second hashed chunk failed.");
            return (attachment, ticket);
        }

        private async Task<HttpResponseData> CommitAsync(string contentId, string ticket, string body)
        {
            var request = TextRequest("patch", body);
            request.Headers.Add(SafeExchangeSecretStream.AccessTicketHeaderName, ticket);
            return await this.ContentCommit.Run(request, "alpha", contentId, this.Owner, this.Logger);
        }
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.Contract.ContentCommitContractTests"`

Expected: `Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9`. On a literal mismatch apply
ground rule 2.

---

## Task 6: `AccessContractTests` (`Run` post/get v2/get v3/delete/patch)

**Files:**
- Create: `SafeExchange.Tests\Contract\AccessContractTests.cs`

Covers: grant ok, GET v2 plain list, GET v3 enriched list, revoke ok, patch ok, patch without add/remove 400
`error`, grant with empty list 400 `error`, missing secret 404 (`not exists`, no period), read forbidden 403,
`consent_required` subStatus, grant forbidden (`GrantAccess`), patch remove forbidden (`RevokeAccess`),
registered S2S app reading the v3 list of a secret it was granted.

- [ ] **Step 1: Write the fixture**

Create `SafeExchange.Tests\Contract\AccessContractTests.cs`:

```csharp
/// <summary>
/// AccessContractTests
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using NUnit.Framework;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using System.Collections.Generic;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// Pins the wire contract of SafeExchangeAccess (post/get/delete/patch). GET differs by
    /// route version: v2 passes enrichedAccessList=false, v3 passes true; the other methods
    /// are version-agnostic.
    /// </summary>
    [TestFixture]
    public class AccessContractTests : ContractTestBase
    {
        [Test]
        public async Task Grant_ReturnsOk()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.SecretAccess.Run(JsonRequest("post", ReaderInput(read: true)), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":"ok"}
                """);
        }

        [Test]
        public async Task GetV2_ReturnsPlainAccessList()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, read: true);

            var response = await this.SecretAccess.Run(Request("get"), "alpha", this.Owner, this.Logger, enrichedAccessList: false);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":[
                 {"objectName":"alpha","subjectType":0,"subjectName":"owner@contract.test","subjectId":"owner@contract.test","canRead":true,"canWrite":true,"canGrantAccess":true,"canRevokeAccess":true,"tags":[]},
                 {"objectName":"alpha","subjectType":0,"subjectName":"reader@contract.test","subjectId":"reader@contract.test","canRead":true,"canWrite":false,"canGrantAccess":false,"canRevokeAccess":false,"tags":[]}]}
                """);
        }

        [Test]
        public async Task GetV3_ReturnsAccessListWithCallerEffectivePermissions()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, read: true);

            var response = await this.SecretAccess.Run(Request("get"), "alpha", this.Reader, this.Logger, enrichedAccessList: true);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"accessList":[
                 {"objectName":"alpha","subjectType":0,"subjectName":"owner@contract.test","subjectId":"owner@contract.test","canRead":true,"canWrite":true,"canGrantAccess":true,"canRevokeAccess":true,"tags":[]},
                 {"objectName":"alpha","subjectType":0,"subjectName":"reader@contract.test","subjectId":"reader@contract.test","canRead":true,"canWrite":false,"canGrantAccess":false,"canRevokeAccess":false,"tags":[]}],
                 "callerEffectivePermissions":{"canRead":true,"canWrite":false,"canGrantAccess":false,"canRevokeAccess":false}}}
                """);
        }

        [Test]
        public async Task Revoke_ReturnsOk()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, read: true);

            var response = await this.SecretAccess.Run(JsonRequest("delete", ReaderInput(read: true)), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":"ok"}
                """);
        }

        [Test]
        public async Task Patch_ReturnsOk()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, read: true);
            var input = new AccessUpdateInput
            {
                Add = ReaderInput(read: true, write: true),
                Remove = ReaderInput(read: true),
            };

            var response = await this.SecretAccess.Run(JsonRequest("patch", input), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":"ok"}
                """);
        }

        [Test]
        public async Task Patch_WithoutAddOrRemove_ErrorStatus()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.SecretAccess.Run(TextRequest("patch", "{}"), "alpha", this.Owner, this.Logger);

            // Quirk: SafeExchangeAccess reports 400s with status "error", not "bad_request".
            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"error","subStatus":"","error":"Access update body must include at least one of 'add' or 'remove'."}
                """);
        }

        [Test]
        public async Task Grant_EmptyList_ErrorStatus()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.SecretAccess.Run(TextRequest("post", "[]"), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"error","subStatus":"","error":"Access settings are not provided."}
                """);
        }

        [Test]
        public async Task MissingSecret_NotFoundWithoutTrailingPeriod()
        {
            var response = await this.SecretAccess.Run(Request("get"), "missing", this.Owner, this.Logger, enrichedAccessList: true);

            // Quirk: "not exists" wording and no trailing period, unlike the other handlers.
            ContractAssert.Json(response, HttpStatusCode.NotFound, """
                {"status":"not_found","subStatus":"","error":"Secret 'missing' not exists"}
                """);
        }

        [Test]
        public async Task Get_WithoutReadPermission_Forbidden()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.SecretAccess.Run(Request("get"), "alpha", this.Stranger, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Insufficient permissions to do 'Read' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task Get_CallerRequiringConsent_ForbiddenWithConsentSubStatus()
        {
            await this.CreateSecretAsync("alpha");
            this.DbContext.Users.Add(new User("Stranger", "00000000-0000-0000-0000-0000000000a3", TenantId, StrangerUpn, StrangerUpn)
            {
                ConsentRequired = true,
            });
            await this.DbContext.SaveChangesAsync();

            var response = await this.SecretAccess.Run(Request("get"), "alpha", this.Stranger, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"consent_required","error":"Insufficient permissions to do 'Read' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task Grant_WithoutGrantAccessPermission_Forbidden()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, read: true);

            var response = await this.SecretAccess.Run(JsonRequest("post", ReaderInput(read: true, write: true)), "alpha", this.Reader, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Insufficient permissions to do 'GrantAccess' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task Patch_RemoveWithoutRevokeAccessPermission_Forbidden()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, read: true, grant: true);
            var input = new AccessUpdateInput { Remove = ReaderInput(read: true) };

            var response = await this.SecretAccess.Run(JsonRequest("patch", input), "alpha", this.Reader, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Insufficient permissions to do 'RevokeAccess' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task RegisteredApplication_GetV3_OnGrantedSecret()
        {
            await this.SeedApplicationAsync(AgentClientId, AgentDisplayName, enabled: true);
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", AgentDisplayName, read: true, subjectType: SubjectTypeInput.Application);

            var response = await this.SecretAccess.Run(Request("get"), "alpha", this.Agent, this.Logger, enrichedAccessList: true);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"accessList":[
                 {"objectName":"alpha","subjectType":0,"subjectName":"owner@contract.test","subjectId":"owner@contract.test","canRead":true,"canWrite":true,"canGrantAccess":true,"canRevokeAccess":true,"tags":[]},
                 {"objectName":"alpha","subjectType":100,"subjectName":"contract-agent","subjectId":"contract-agent","canRead":true,"canWrite":false,"canGrantAccess":false,"canRevokeAccess":false,"tags":[]}],
                 "callerEffectivePermissions":{"canRead":true,"canWrite":false,"canGrantAccess":false,"canRevokeAccess":false}}}
                """);
        }

        private static List<SubjectPermissionsInput> ReaderInput(bool read, bool write = false)
            => new()
            {
                new SubjectPermissionsInput
                {
                    SubjectType = SubjectTypeInput.User,
                    SubjectName = ReaderUpn,
                    CanRead = read,
                    CanWrite = write,
                },
            };
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.Contract.AccessContractTests"`

Expected: `Passed!  - Failed:     0, Passed:    13, Skipped:     0, Total:    13`. On a literal mismatch apply
ground rule 2.

---

## Task 7: `SecretAuditContractTests` (`GET .../audit`)

**Files:**
- Create: `SafeExchange.Tests\Contract\SecretAuditContractTests.cs`

Covers: audit disabled page (`events: []`, no `nextContinuation`), audit enabled page (`SecretCreated` +
`PermissionGranted`, payload shapes, actor, first `prevHash` is `""`), `pageSize=1` with
`"nextContinuation":"MQ=="`, read forbidden 403, missing secret 404.

- [ ] **Step 1: Write the fixture**

Create `SafeExchange.Tests\Contract\SecretAuditContractTests.cs`:

```csharp
/// <summary>
/// SecretAuditContractTests
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using NUnit.Framework;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// Pins the wire contract of SafeExchangeSecretAudit (GET .../audit). Route-version agnostic.
    /// Event hashes depend on the random AuditInstanceId, so non-empty hash/prevHash values are masked.
    /// </summary>
    [TestFixture]
    public class SecretAuditContractTests : ContractTestBase
    {
        [Test]
        public async Task AuditDisabled_ReturnsDisabledPage()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.SecretAudit.Run(Request("get"), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"auditEnabled":false,"events":[]}}
                """);
        }

        [Test]
        public async Task AuditEnabled_ReturnsEventPage()
        {
            await this.CreateSecretAsync("alpha", auditEnabled: true);
            await this.GrantAsync("alpha", ReaderUpn, read: true);

            var response = await this.SecretAudit.Run(Request("get"), "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"auditEnabled":true,"events":[
                 {"eventType":"SecretCreated","occurredAt":"2026-11-21T10:00:00Z","sequenceNumber":1,
                  "actor":{"subjectType":"User","subjectId":"owner@contract.test","subjectName":"owner@contract.test"},
                  "payload":{"tags":[],"expirationSettings":{"scheduleExpiration":false,"expireAt":"2027-01-01T00:00:00Z","expireOnIdleTime":false,"idleTimeToExpire":"180.00:00:00"},"auditEnabled":true},
                  "hash":"<hash>","prevHash":""},
                 {"eventType":"PermissionGranted","occurredAt":"2026-11-21T10:00:00Z","sequenceNumber":2,
                  "actor":{"subjectType":"User","subjectId":"owner@contract.test","subjectName":"owner@contract.test"},
                  "payload":{"target":{"subjectType":"User","subjectId":"reader@contract.test","subjectName":"reader@contract.test"},
                   "permissions":{"from":{"canRead":false,"canWrite":false,"canGrantAccess":false,"canRevokeAccess":false},
                                  "to":{"canRead":true,"canWrite":false,"canGrantAccess":false,"canRevokeAccess":false}}},
                  "hash":"<hash>","prevHash":"<prevHash>"}]}}
                """, null, "hash", "prevHash");
        }

        [Test]
        public async Task AuditEnabled_PageSizeOne_ReturnsContinuation()
        {
            await this.CreateSecretAsync("alpha", auditEnabled: true);
            await this.GrantAsync("alpha", ReaderUpn, read: true);
            var request = Request("get");
            request.SetQueryString("pageSize=1");

            var response = await this.SecretAudit.Run(request, "alpha", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"auditEnabled":true,"events":[
                 {"eventType":"SecretCreated","occurredAt":"2026-11-21T10:00:00Z","sequenceNumber":1,
                  "actor":{"subjectType":"User","subjectId":"owner@contract.test","subjectName":"owner@contract.test"},
                  "payload":{"tags":[],"expirationSettings":{"scheduleExpiration":false,"expireAt":"2027-01-01T00:00:00Z","expireOnIdleTime":false,"idleTimeToExpire":"180.00:00:00"},"auditEnabled":true},
                  "hash":"<hash>","prevHash":""}],
                 "nextContinuation":"MQ=="}}
                """, null, "hash", "prevHash");
        }

        [Test]
        public async Task WithoutReadPermission_Forbidden()
        {
            await this.CreateSecretAsync("alpha", auditEnabled: true);

            var response = await this.SecretAudit.Run(Request("get"), "alpha", this.Stranger, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Insufficient permissions to do 'Read' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task MissingSecret_NotFound()
        {
            var response = await this.SecretAudit.Run(Request("get"), "missing", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.NotFound, """
                {"status":"not_found","subStatus":"","error":"Secret 'missing' does not exist."}
                """);
        }
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.Contract.SecretAuditContractTests"`

Expected: `Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5`. On a literal mismatch apply
ground rule 2.

---

## Task 8: Run all contract tests together, check isolation, commit

**Files:** none new.

- [ ] **Step 1: Run the whole contract suite**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.Contract"`

Expected: `Passed!  - Failed:     0, Passed:    83, Skipped:     0, Total:    83`

- [ ] **Step 2: Prove the shared static state is restored for other fixtures**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.Contract|FullyQualifiedName~SecC_|FullyQualifiedName~SecE1_|FullyQualifiedName~SecE2_|FullyQualifiedName~SecF1_|FullyQualifiedName~SecF2_|FullyQualifiedName~SecB_|FullyQualifiedName~EffectivePermissionsTests|FullyQualifiedName~ActionResultsTests|FullyQualifiedName~ApplicationSearchTests"`

Expected: `Passed!  - Failed:     0, Passed:   152, Skipped:     0, Total:   152` (all InMemory; no emulator needed).

- [ ] **Step 3: Confirm no new warnings and no production changes**

Run: `dotnet build SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-incremental -v q "-clp:ForceNoAlign;NoSummary" 2>&1 | Select-String '\\Contract\\'`
Expected: no output.

Run: `git --no-pager status --short`
Expected: only `?? SafeExchange.Tests/Contract/` plus the untracked plan documents under
`docs/superpowers/plans/` — nothing under `SafeExchange.Core/`, `SafeExchange.Functions/` or existing test files.

- [ ] **Step 4: Commit**

```powershell
git add SafeExchange.Tests/Contract docs/superpowers/plans/2026-11-21-mcp-endpoint.md docs/superpowers/plans/2026-11-21-mcp-p1-contract-tests.md
git commit -m "test(contract): pin REST wire contract of the six MCP-relevant handlers" -m "Characterization tests for SecretMeta, ContentMeta, Stream, ContentCommit, Access and SecretAudit: exact status, JSON body (canonical, volatile values masked) and headers, v2 and v3 shapes. InMemory, no emulator. No production changes." -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

Expected: one commit with 10 new files under `SafeExchange.Tests/Contract/` (plus the two plan documents).

- [ ] **Step 5: Push (after the phase review, per the master plan)**

Run: `git push origin feat/mcp-endpoint`
Expected: the branch is updated on `origin`.

---

## Troubleshooting

- **`MSB3027`/`MSB3021` "file is locked by testhost (PID)"** when building: an earlier test host still holds
  `SafeExchange.Tests\bin\Debug\net10.0\SafeExchange.Tests.dll`. Wait for it to exit, or stop that specific
  process with `Stop-Process -Id <PID>` only if you started it.
- **A JSON test fails only on `Content-Type`/header lines:** the worker's `WriteAsJsonAsync` content type or
  the handler's header writes changed — that is a contract change; investigate, do not relax the assertion.
- **`IsWireCompact` fails:** the production serializer settings changed (encoder, indentation, converters) —
  that is a wire change; investigate `SafeExchangeStartup` / worker options.

## Risks

- **InMemory vs Cosmos.** The tests pin handler output, not provider behaviour. Multi-row lists (v2/v3 list,
  access list, audit page) rely on InMemory insertion order (stable in EF Core 10 InMemory for a fresh
  database without deletes); production Cosmos order is unspecified anyway. A provider-specific regression
  (e.g. a query that stops translating on Cosmos) is not caught here — the existing Cosmos fixtures and the
  final full-suite run cover that.
- **Process-wide statics.** `TestFactory.FunctionContext` and `DateTimeProvider` are shared; the base saves
  and restores them per test. NUnit fixtures in this project are not parallelised; if parallelism is ever
  enabled, mark the contract fixtures `[NonParallelizable]`.
- **Masked values.** `accessTicket`, audit `hash`/`prevHash` and the correlation id are compared only as
  "present and non-empty"; their formats are covered by `SecretStreamTests`, `AuditEventHasherTests` and
  `ActionResultsTests`.
- **Handler-level headers only.** The Functions host may add headers (e.g. `Date`); the contract pins what the
  handler writes.
- **Phase 2 plumbing.** After the move to `SafeExchange.Core.Functions` the test project needs a project
  reference to it; namespaces stay the same, so these files compile unchanged. Phase 4 constructor changes are
  absorbed in `ContractTestBase.ContractSetUp` only.
