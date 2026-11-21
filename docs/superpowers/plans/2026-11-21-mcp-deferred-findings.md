# Deferred review findings (MCP endpoint branch)

Minor/Low findings deferred to the final phase. Each must be fixed (or explicitly justified) before the PR.

## Phase 2 (project split)

- **Low — middleware opt-in granularity.** `SafeExchange.Core.Functions/SafeExchangeFunctionsStartup.cs`
  `ConfigureWorkerDefaults` always adds `DefaultAuthenticationMiddleware`; a consumer with its own auth
  cannot reuse it for TokenFilter/SessionCorrelation/ApiVersion. Fix: split into
  `UseSafeExchangeAuthentication(builder)` and `UseSafeExchangeMiddleware(builder)`; Program.cs calls both;
  document `DefaultAuthenticationMiddleware.ClaimsPrincipalKey` and the
  `ConfigureFunctionsServices`-before-`ConfigureServices` requirement in the consumer migration note (phase 7).
