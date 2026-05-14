# OWASP Top 10:2025 Security Review — SafeExchange

**Date:** 2026-04-13
**Reviewer:** Automated OWASP Top 10:2025 review (orchestrator + parallel sub-agents)
**Methodology:** [OWASP Top 10:2025](https://owasp.org/Top10/2025/) — 10 category sub-agents, four-axis ranking (Severity × Exploitability × Exposure × Confidence → Priority)

## Scope

- **Mode:** Codebase audit
- **Repo:** `SafeExchange` (this repository)
- **Codebase:** ~177 C# files under `SafeExchange.Core/` and `SafeExchange.Functions/` plus `deployment/` ARM templates
- **Language / platform:** C# / .NET 10 Azure Functions (isolated worker) · Azure AD (Entra ID) · Cosmos DB · Azure Blob Client-Side Encryption v2 · Service Bus queues · Microsoft Graph (OBO)
- **Excluded:** `SafeExchange.Tests/`, `.vs/`, `bin/`, `obj/`

## Result counts

- **Total findings:** 22
- **By priority:** P0:1 · P1:3 · P2:9 · P3:8 · P4:1
- **By severity:** Critical:1 · High:3 · Medium:11 · Low:6 · Info:1
- **By category:** A01:4 · A02:6 · A03:5 · A04:5 · A05:1 · A06:2 · A07:2 · A08:0 · A09:2 · A10:2 (overlaps produce 29 category hits across 22 unique findings)

## Files

| File | Contents |
|------|----------|
| [`P0-critical.md`](P0-critical.md) | **Fix immediately, before next deploy** |
| [`P1-high.md`](P1-high.md) | Fix this sprint |
| [`P2-medium.md`](P2-medium.md) | Fix this quarter |
| [`P3-backlog.md`](P3-backlog.md) | Backlog |
| [`P4-informational.md`](P4-informational.md) | Document and close |

## Reviewer's Top 3 Priorities

1. **Fix the P0 missing-return bug in `SafeExchangeExternalNotificationDetails.Run` today.** This is a Critical access-control bypass plus a destructive side effect reachable by any authenticated user. A one-line fix. Add an integration test that asserts a user token receives 403 before any database access.
2. **Ship the audience/issuer validation hardening (P1).** Default both to `true`, add a startup assertion that refuses to boot when `ValidIssuers` is empty with `ValidateIssuer=true`. Migrate via a loud configuration error, not a silent accept.
3. **Replace UPN-as-identity with `oid` (P1).** This is the foundational design fix that keeps the authorization model correct under tenant lifecycle events (rename, offboarding, reuse). Schedule a DB migration and release notes.

## Systemic Observations

- **One-line correctness bug repeated across the codebase.** The "build response, forget to return" pattern exists in 10+ locations, with the P0 instance being the one where it flips the access control decision. This is a pattern that a Roslyn analyzer can catch at build time. Add one before the next refactor.
- **Verbose exception responses are a monoculture.** Every handler's `TryCatch` emits `{ex.GetType()}: {ex.Message}`. A single helper (`ActionResults.InternalError(correlationId)`) swapped in would fix all sites at once and enable future log-to-response correlation.
- **Identity is built on mutable fields.** UPN for users, DisplayName for apps. `oid` and `(AadTenantId, AadClientId)` are available but only used for admin lists. This shows up as multiple findings (A01 IDOR-by-rename, A06 design flaw, A07 scope-claim classification) and is the single design change with the biggest blast-radius reduction.
- **Good news — crypto is not rolled in-house.** `CryptoHelper` only provisions RSA-4096 keys in Azure Key Vault; actual encrypt/decrypt is delegated to Azure Storage Client-Side Encryption V2 (AES-256-GCM + RSA-OAEP-256 key wrap). No MD5/SHA-1/DES/RC4/ECB anywhere. TLS enforcement on all Azure resources. This should not be undone during remediation.
- **No CI/CD at all.** The `.github/workflows/` directory is empty. Dependency scanning, lint-level checks, SBOM, and smoke tests could all live in one workflow and would catch at least half of the P2 findings before PR merge. This is the highest-leverage engineering investment the team can make.

## Categories With No Findings

- **A08 — Software or Data Integrity Failures** — no direct findings beyond cross-links. `BinaryFormatter` / `Newtonsoft.Json TypeNameHandling` / dynamic assembly load patterns are absent. Queue messages are trusted because queue write is gated by storage credentials; design decision is acceptable for the current threat model.

## Prioritization Rubric

Every finding is scored on four independent axes:

- **Severity** (Critical / High / Medium / Low / Info) — intrinsic impact if fully exploited
- **Exploitability** (Trivial / Easy / Moderate / Hard / Theoretical) — how hard to actually trigger
- **Exposure** (Internet / Authenticated-user / Privileged-user / Internal-network / Local-only) — who can reach the vulnerable code
- **Confidence** (Confirmed / High / Medium / Low) — reviewer's certainty

Combined into a **Priority** tier:

| Priority | Typical combination | When to fix |
|----------|---------------------|-------------|
| **P0** | (Critical or High) × (Trivial or Easy) × Internet × (Confirmed or High) | Stop the deploy; fix immediately |
| **P1** | High × Any × (Internet or Auth) × (Confirmed or High), **or** Critical × (Moderate/Hard) × Internet | Current sprint |
| **P2** | Medium × (Trivial/Easy) × (Internet/Auth), **or** High × (Moderate/Hard) × (Auth/Internal) | Current quarter |
| **P3** | Low × Any, Medium × (Moderate/Hard) × Internal | Backlog |
| **P4** | Info × Any | Document, close |

## Review Gaps / Caveats

The review was run as 10 parallel OWASP category sub-agents. 3 completed cleanly (A02, A03, A04) before the agent platform rate-limited the remaining batch. The orchestrator completed A01, A05, A06, A07, A08, A09, A10 inline using direct file reads and structured grep sweeps across the critical-path files (auth middleware, permissions manager, subject helpers, global filters, Graph data provider, startup).

**Known coverage gap:** A09 (security logging) and A10 (exceptional conditions) received inline coverage rather than dedicated sub-agent depth. Re-running this review will likely surface additional **Medium** findings in those categories — specifically:

- A09: conversion of `$"..."` interpolated log messages to structured `{Placeholder}` parameters (roughly dozens of sites)
- A10: `HttpClient` timeout hygiene on all outbound calls (webhook delivery in `SafeExchangeProcessExternalNotification`, Graph calls in `OnBehalfOfAuthProvider`), `catch { }` / `catch { /* no-op */ }` sweeps

Treat this report as a **strong baseline**, not a complete pass. Re-run after the rate limit resets to close the gap.
