# First-party MCP endpoint — master implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement
> this plan phase by phase. Each phase has its own detailed plan file (written just before the phase
> starts, against the code as it is then). Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose a first-party MCP endpoint (`POST /api/mcp`) from the SafeExchange backend, built on a
new host-agnostic service layer, while keeping every existing REST contract byte-identical.

**Spec:** `docs/superpowers/specs/2026-11-21-mcp-endpoint-design.md`

**Architecture:** `SafeExchange.Core` becomes free of Azure Functions types and gains `Services/`,
`Caller`/`ICallerResolver` and `Mcp/` tool definitions. All Functions-typed code moves to a new library
`SafeExchange.Core.Functions` (same namespaces, so consumers only add a reference). The six
MCP-relevant handlers become thin HTTP adapters over services. The MCP JSON-RPC dispatcher lives in
`SafeExchange.Core.Functions`; `SafeExchange.Functions` adds the triggers.

**Tech stack:** .NET 10, Azure Functions isolated worker (HttpRequestData), EF Core Cosmos, NUnit 4 +
Moq, `ModelContextProtocol.Core` (protocol types only).

---

## Phases (each = one detailed plan file, TDD per task, one review, then commit + push)

| # | Phase | Plan file |
|---|---|---|
| 1 | REST contract tests (characterization, byte-exact) | `2026-11-21-mcp-p1-contract-tests.md` |
| 2 | Project split: `SafeExchange.Core.Functions`; Core loses Functions packages; architecture test | `2026-11-21-mcp-p2-project-split.md` |
| 3 | `Caller`, `ICallerResolver`, host-agnostic global filter | `2026-11-21-mcp-p3-caller.md` |
| 4 | Services: `SecretService`, `ContentService`, `ChunkService`, `AccessService`, `AuditQueryService`; handlers become adapters | `2026-11-21-mcp-p4-services.md` |
| 5 | Audit attribution (`Channel`, `ClientLabel`) | `2026-11-21-mcp-p5-audit-attribution.md` |
| 6 | MCP endpoint (dispatcher, tools, triggers, protected-resource metadata, `WWW-Authenticate`, flag) | `2026-11-21-mcp-p6-mcp-endpoint.md` |
| 7 | Docs (`api-endpoints.md` fixes, `docs/mcp.md`, consumer migration note) | `2026-11-21-mcp-p7-docs.md` |
| F | Full suite, full code review + security review, fix all (incl. deferred), PR, merge | (this file, §Final) |

## Ground rules for every phase

- **Branch:** `feat/mcp-endpoint`. One PR at the end. After each phase: commit, `git push origin feat/mcp-endpoint`.
- **TDD:** write the failing test, run it and observe the expected failure, implement minimally, run green.
  Pure moves (phase 2) are guarded by the phase-1 contract tests being green before and after.
- **REST is frozen:** contract tests from phase 1 must never be edited to make a later phase pass
  (only `using`/namespace or construction-plumbing changes are allowed in them).
- **Review per phase:** one review subagent; fix all Critical and Important findings before the next
  phase; record Minor/Low findings in `docs/superpowers/plans/2026-11-21-mcp-deferred-findings.md`
  and fix them in the final phase.
- **Tests:** run only the tests related to the current phase (`--filter`), plus the phase-1 contract
  tests from phase 2 on. The full suite runs in the final phase.
- **Style (match the codebase):** file header `/// <summary>\n/// TypeName\n/// </summary>` before the
  namespace; block-scoped `namespace X { ... }` with `using` directives inside the namespace;
  `this.` prefix on members; constructor null-guards with `?? throw new ArgumentNullException(nameof(x))`;
  4-space indentation; NUnit `Assert.That(..., Is....)`. No new comments unless a line needs clarifying.
- **Zero new build warnings** in changed files.

## Commands

All from the repo root `C:\Users\yurio\Documents\github\safeexchange`.

- Build everything: `dotnet build SafeExchange.slnx --nologo -v q`
- Run a test subset: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~<Name>"`
- Run several fixtures: `--filter "FullyQualifiedName~A|FullyQualifiedName~B"`
- Full suite (final phase only): `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo`
- Cosmos-backed fixtures need the local emulator (HTTP gateway, like CI):
  `docker run --detach --name saex-test-cosmos --publish 127.0.0.1:8081:8081 --publish 127.0.0.1:8080:8080 mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview`
  and the `ConnectionStrings:CosmosDb` user-secret of `SafeExchange.Tests` set to the public emulator
  connection string used in `.github/workflows/ci.yml`.

## Cross-phase decisions (recommended choices, fixed here)

1. **Moved files keep their namespaces** (`SafeExchange.Core`, `SafeExchange.Core.Functions`,
   `SafeExchange.Core.Functions.Admin`, `SafeExchange.Core.Middleware`, `SafeExchange.Core.Filters`).
   Only the assembly changes, so consumers and tests just add a project reference.
2. **`ITokenHelper.GetAccountIdAndToken(HttpRequestData, ClaimsPrincipal)`** leaves the interface.
   `ITokenHelper` gains `string GetAccountId(ClaimsPrincipal)` and `SafeExchange.Core.Functions` adds an
   extension `TokenHelperHttpExtensions.GetAccountIdAndToken(this ITokenHelper, HttpRequestData,
   ClaimsPrincipal)` so call sites compile unchanged.
3. **`SafeExchangeStartup` is split**: Core keeps `IsDevMode`, `ConfigureWorkerLogging`,
   `ConfigureAppConfiguration`, `ConfigureServices` (minus Functions-typed registrations). New
   `SafeExchange.Core.Functions/SafeExchangeFunctionsStartup` holds `IsHttpTrigger`,
   `ConfigureWorkerDefaults` and `ConfigureFunctionsServices` (registers `ITokenMiddlewareCore`,
   `GlobalFilters`, the two middleware telemetry initializers, handlers, MCP).
4. **Internal Core types** used by moved code (`PermissionNetChange`, `IConfidentialClientProvider`,
   `MigrationsHelper`): `InternalsVisibleTo("SafeExchange.Core.Functions")` in Core.
5. **`scopes_supported`** for protected-resource metadata: new optional setting
   `Authentication:McpScopes` (comma-separated); when empty, `{resource}/user_impersonation` where
   `resource` is the first `api://` entry of `Authentication:ValidAudiences`.
   `authorization_servers` = `Authentication:Authority` normalised to end with `/v2.0`.

## Final phase (F)

- [ ] Run the full test suite; fix failures.
- [ ] Dispatch one full code review (whole branch vs `main`) and one security review; fix every
      finding plus all deferred Minor/Low findings; rerun affected tests; commit and push.
- [ ] Create the PR (`gh pr create`), wait for CI, merge (`gh pr merge --squash --delete-branch`),
      switch to `main`, pull, `dotnet build SafeExchange.slnx`.
