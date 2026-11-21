# Deferred review findings (MCP endpoint branch)

Minor/Low findings deferred to the final phase. Each must be fixed (or explicitly justified) before the PR.

## Phase 2 (project split)

- **Low — middleware opt-in granularity.** `SafeExchange.Core.Functions/SafeExchangeFunctionsStartup.cs`
  `ConfigureWorkerDefaults` always adds `DefaultAuthenticationMiddleware`; a consumer with its own auth
  cannot reuse it for TokenFilter/SessionCorrelation/ApiVersion. Fix: split into
  `UseSafeExchangeAuthentication(builder)` and `UseSafeExchangeMiddleware(builder)`; Program.cs calls both;
  document `DefaultAuthenticationMiddleware.ClaimsPrincipalKey` and the
  `ConfigureFunctionsServices`-before-`ConfigureServices` requirement in the consumer migration note (phase 7).
  **Fixed in 4fd86d2** (`UseSafeExchangeAuthentication` / `UseSafeExchangeMiddleware`; `ConfigureWorkerDefaults` calls
  both; documented in `docs/consumer-migration.md` section 3).

## Phase 5 (audit attribution)

- **Low — ambiguous hash suffix.** `SafeExchange.Core/Audit/AuditOrigin.cs` `Sanitize` keeps `|`, and
  `AuditEventHasher` joins `|channel|clientLabel`, so `("mcp","a|b")` and `("mcp|a","b")` hash the same, and at
  sequence 1 with an empty label the new form can equal an old-format payload ending in `||mcp`. Fix: drop `|` in
  `Sanitize` (+ test), and add a version marker to the suffix (e.g. `|v2|{channel}|{clientLabel}`); old/REST hashes
  are unaffected because the suffix is only appended when channel or label is non-empty. Update the hash rule in
  `docs/superpowers/specs/2026-05-14-secret-audit-design.md` and hasher tests.
  **Fixed in 5cb21ce** (`Sanitize` drops `|`; suffix is `|v2|{channel}|{clientLabel}`; hash rule documented in the
  secret-audit spec and `docs/data-model.md`).

## Phase 6 (MCP endpoint)

- **Minor — no MCP-specific size cap.** **Accepted — documented** (owner decision): the trade-off is described in
  `docs/mcp.md` (Security notes: existing backend limits apply, `/api/mcp` buffers the JSON body, keep chunks at most
  256 KB and use `file_transfer_info` for large files).
