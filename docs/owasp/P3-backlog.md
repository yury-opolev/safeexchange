# P3 — Backlog

> See [`SUMMARY.md`](SUMMARY.md) for methodology and rubric.

These findings are real but low-urgency. Schedule for the appropriate quarter when touching the surrounding code; none require emergency action.

## [P3] [LOW] Kudu / SCM exposed to the internet

- **Category:** A02:2025
- **CWE:** CWE-284
- **File:** `deployment/current/arm/services-template.arm.json:1928-1946`, `deployment/legacy/arm/services-template.arm.json:1596-1613`
- **Severity:** Low (defense-in-depth; Kudu still requires AAD) · **Exploitability:** Moderate · **Exposure:** Internet · **Confidence:** Confirmed · **Priority:** P3

Both ARM templates set `ipSecurityRestrictions` and `scmIpSecurityRestrictions` to a single `"Allow all"` rule, meaning the Kudu SCM site (`*.scm.azurewebsites.net`) and the app front door are reachable from any IP. Kudu console and its `/api/zipdeploy`, `/DebugConsole`, `/api/vfs/` endpoints are high-value targets. They are protected by AAD, but a credential compromise or a Kudu CVE (there have been several) would allow full site takeover. For a secrets-management backend specifically, restricting SCM to a bastion or admin IP list would materially reduce blast radius.

**Recommendation:** Restrict `scmIpSecurityRestrictions` to the administrator public IP range or VNet; consider restricting `ipSecurityRestrictions` to the Azure Front Door service tag (`AzureFrontDoor.Backend`).

---

## [P3] [LOW] No NuGet lockfile (`packages.lock.json`)

- **Category:** A03:2025
- **CWE:** CWE-1357
- **File:** absent at `SafeExchange.Core/packages.lock.json`, `SafeExchange.Functions/packages.lock.json`, `SafeExchange.Tests/packages.lock.json`
- **Severity:** Medium · **Exploitability:** Hard · **Exposure:** Internet · **Confidence:** Confirmed · **Priority:** P3

Central Package Management pins direct package versions, but transitive dependency versions are still resolved at restore time without hash verification. Without a lockfile NuGet does not perform reproducible-restore enforcement (`--locked-mode`) and there is no SHA-512 hash check on downloaded packages, leaving a (small) window for upstream package substitution if a maintainer account is compromised.

**Recommendation:** In each `.csproj`, add `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>` under the existing `PropertyGroup`. Run `dotnet restore` once to generate `packages.lock.json`, commit it, and add `--locked-mode` to all CI restore calls.

---

## [P3] [LOW] No `NuGet.config` restricting package sources

- **Category:** A03:2025
- **CWE:** CWE-1357
- **File:** absent at `NuGet.config` (repo root)
- **Severity:** Medium · **Exploitability:** Hard · **Exposure:** Build/CI · **Confidence:** Confirmed · **Priority:** P3

No project-local `NuGet.config` exists. Restore therefore inherits whatever feeds the developer's or CI runner's machine-wide configuration provides. If a developer has added a private/secondary feed (or a malicious feed) to their global config, packages can be resolved from there — and per NuGet's source-resolution rules, the "best version" is picked across all configured feeds, which is the classic dependency-confusion vector (even though no clearly-internal package names are present in this repo, missing source pinning is best-practice debt for a security-sensitive service).

**Recommendation:** Commit a project-local `NuGet.config` that explicitly clears inherited sources and pins to nuget.org only, with a `packageSourceMapping`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

---

## [P3] [LOW] No `global.json` SDK pinning

- **Category:** A03:2025
- **CWE:** CWE-1357
- **File:** absent at `global.json` (repo root)
- **Severity:** Low · **Exploitability:** Hard · **Exposure:** Build/CI · **Confidence:** Confirmed · **Priority:** P3

No `global.json` is present. The .NET SDK selected for build is whatever is installed (latest available), with default roll-forward. While .NET 10 is the targeted framework in every csproj, builds across developer machines and any future CI runner will not be reproducible at the SDK level. SDK reproducibility is a defense-in-depth posture for build environments.

**Recommendation:** Add a `global.json` pinning the SDK version with `latestPatch` roll-forward:

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestPatch",
    "allowPrerelease": false
  }
}
```

---

## [P3] [MEDIUM] No SBOM generation

- **Category:** A03:2025
- **CWE:** CWE-1357
- **Severity:** Medium · **Exploitability:** N/A (process gap) · **Exposure:** Internet (production service) · **Confidence:** Confirmed · **Priority:** P3

No Software Bill of Materials is generated as part of the build. For a credential-sharing service, the absence of an SBOM means there is no asset inventory to consult when a new CVE is announced — the team cannot quickly answer "are we affected by Foo 1.2.3?" without manually re-inspecting every csproj and the resolved transitive graph.

**Recommendation:** Add a CI step that runs `dotnet CycloneDX` (<https://github.com/CycloneDX/cyclonedx-dotnet>) or the `Microsoft.Sbom.Targets` MSBuild integration, and uploads the resulting `bom.xml` / `_manifest/spdx_2.2/` as a build artifact.

---

## [P3] [LOW] Non-CSPRNG `Random.Shared.NextInt64()` in access-ticket token generation

- **Category:** A04:2025 — Cryptographic Failures
- **CWE:** CWE-338 (Use of Cryptographically Weak PRNG), CWE-330
- **Files:**
  - `SafeExchange.Core/Functions/SafeExchangeSecretStream.cs:216`
  - `SafeExchange.Core/Functions/SafeExchangeSecretContentMeta.cs:394`
- **Severity:** Low · **Exploitability:** Hard · **Exposure:** Authenticated (same tenant, write permission on the secret) · **Confidence:** High · **Priority:** P3

### Evidence

```csharp
// SafeExchangeSecretStream.cs:216
accessTicket = $"{Guid.NewGuid()}-{Random.Shared.NextInt64():00000000}";

// SafeExchangeSecretContentMeta.cs:394
content.AccessTicket = $"{Guid.NewGuid()}-{Random.Shared.NextInt64():00000000}";
```

### Description

The "access ticket" is a concurrency/session token that gates mutation of an in-progress upload: once content is in `Updating` status, a second caller who does not present the matching ticket is rejected. It concatenates `Guid.NewGuid()` (CSPRNG-backed in modern .NET, ~122 bits of entropy) with `Random.Shared.NextInt64()` (Xoshiro-based, predictable after small observation). The GUID half dominates the total unpredictability of the ticket, and the `Random.Shared` suffix adds neither meaningful entropy nor weakness — but mixing a CSPRNG source with a non-CSPRNG source in a security-sensitive token is a code smell that can propagate. The ACTUAL access is still gated by `permissionsManager.IsAuthorizedAsync(..., PermissionType.Write)`, so a guessed ticket gains nothing unless the attacker already holds write permission.

### Recommendation

```csharp
accessTicket = RandomNumberGenerator.GetHexString(32);
```

Drop the GUID+Random mix entirely.

---

## [P3] [LOW] Non-constant-time access-ticket comparison (timing oracle)

- **Category:** A04:2025
- **CWE:** CWE-208 (Observable Timing Discrepancy)
- **File:** `SafeExchange.Core/Functions/SafeExchangeSecretStream.cs:205`
- **Severity:** Low · **Exploitability:** Hard (high-precision network timing against a Function App) · **Exposure:** Authenticated (caller must already hold write permission) · **Confidence:** Medium · **Priority:** P3 (P4 if fixed together with the Random finding above)

```csharp
if (!string.IsNullOrEmpty(existingAccessTicket) && !existingAccessTicket.Equals(accessTicket))
```

`String.Equals` short-circuits on the first differing byte. Combined with the CSPRNG/PRNG mix above, this gives a theoretical byte-by-byte recovery oracle for the ticket. Practically very hard to exploit across cloud function cold/warm paths, the ticket resets after a short timeout, and the caller already needs write permission.

**Recommendation:** Use a constant-time comparison:

```csharp
var a = Encoding.UTF8.GetBytes(existingAccessTicket);
var b = Encoding.UTF8.GetBytes(accessTicket ?? string.Empty);
if (a.Length != b.Length || !CryptographicOperations.FixedTimeEquals(a, b)) { ... }
```

---

## [P3] [LOW] `ChunkMetadata.Hash` declared but never populated (dormant integrity field)

- **Category:** A04:2025 / A08:2025
- **CWE:** CWE-353 (Missing Support for Integrity Check), CWE-345
- **Files:**
  - `SafeExchange.Core/Model/ChunkMetadata.cs:14` (field)
  - `SafeExchange.Core/Functions/SafeExchangeSecretStream.cs:247-252` (always writes `Hash = string.Empty`)
  - `SafeExchange.Core/Model/ContentMetadata.cs:65` (public setter, never called in request path)
- **Severity:** Low · **Exploitability:** Hard (requires secondary compromise granting blob write without DB write) · **Exposure:** Internal (Azure Storage data-plane) · **Confidence:** High · **Priority:** P3

### Description

Each uploaded blob chunk is individually encrypted by Azure Storage Client-Side Encryption V2 (AES-256-GCM content encryption, RSA-OAEP-256 key wrap from Azure Key Vault). Chunk-level confidentiality and per-chunk integrity are therefore provided by the Azure SDK — an attacker cannot forge a valid ciphertext without the KEK.

**However**:

1. `ChunkMetadata.Hash` is declared in the model and has a setter (`ContentMetadata.SetChunkProperties`), but no code path in the request handlers ever populates it. All producer sites write `Hash = string.Empty`.
2. Chunk **ordering** during download is driven entirely by the Entity Framework list order of `existingContent.Chunks` (`SafeExchangeSecretStream.cs:430-434`), which is authoritative from the metadata database. There is no cryptographic binding between sibling chunks and their parent content: an attacker with write access to the blob container (but not the database) could delete a chunk, truncate a multi-chunk secret, or replace a chunk's bytes with an older AEAD-valid ciphertext from the same key version, and the download path would silently serve the altered concatenation without any detectable error (the per-chunk AEAD still verifies).
3. There is no Merkle / MAC covering the full content, no `Length` verification on read, and the `Hash` field that could serve as such is unused.

Not directly exploitable in the primary threat model (SafeExchange assumes the blob store is trusted), but it removes a layer of defense against scenarios where storage credentials are compromised independently of the Cosmos database.

### Recommendation

Either populate `ChunkMetadata.Hash` with a SHA-256 of the plaintext (or of the ciphertext, if chosen for performance) at upload time and verify it on the download path, or adopt a content-level AEAD that covers the concatenation of all chunks with an Additional Authenticated Data field containing `(ContentName, ChunkIndex, TotalChunks)`. Remove the `Hash` field entirely if no integrity check is intended — dormant security fields invite the false assumption that they are enforced.
