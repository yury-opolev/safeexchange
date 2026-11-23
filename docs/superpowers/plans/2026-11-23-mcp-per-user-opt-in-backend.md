# Per-user MCP Opt-in (Backend) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A user's identity can be used through the MCP endpoint (`/api/v1/mcp`) only after the user opted in (`McpAllowed`), an administrator can block MCP per user (`McpBlockedByAdmin`, wins), with REST endpoints for the user's own settings and the admin block, a backfill for existing users, and docs.

**Architecture:** Two `bool` properties on the `User` Cosmos document (backfilled by migration `00013`, run through the existing `admops/run_dbmigration` admin operation). A new Core policy `McpUserAccessPolicy` runs in `McpHandler` right after caller resolution on every MCP message and turns a not-opted-in or blocked **user** caller into a REST-shaped `403` (`CallerDenial`). A new `SafeExchangeMeSettings` handler serves `GET/PATCH /me/settings`, `SafeExchangeAdminUsers` gains the fields and `PATCH admin/users/{upn}/mcp-blocked`; all user-document writes share one etag retry helper (`UserUpdate.SaveWithRetryAsync`).

**Tech Stack:** .NET 10, Azure Functions isolated worker, EF Core 10.0.10 (Cosmos provider; InMemory in MCP tests), Microsoft.Azure.Cosmos SDK, NUnit 4, Moq, Cosmos DB Linux emulator (vNext preview).

**Spec:** `docs/superpowers/specs/2026-11-23-mcp-per-user-opt-in-design.md` (backend sections: Data, Enforcement, API, Concurrency, Delivery item 1, Testing/Backend). The frontend (Delivery item 2) is a separate plan.

---

## Decisions this plan pins down (spec ambiguities resolved)

1. **Which Users row is "the caller".** Enforcement and `/me/settings` find the row by the token's tenant id + object id (`AadTenantId`/`AadObjectId`), exactly how `TokenMiddlewareCore.GetOrCreateUserAsync` finds or creates it on every authenticated request. Admin endpoints keep addressing users by `{upn}` like the existing `enabled` toggle.
2. **No Users row.** MCP: treated as "not allowed" (`403 mcp_not_allowed`), consistent with default-off. `/me/settings`: `404` `{ "status": "not_found", "error": "User not found." }`. In production the token middleware always creates the row before any handler runs, so this is an edge case.
3. **Cost per MCP message.** `CallerResolver` does not load the `User` (the `Caller` record only carries ids), and `TokenFilterMiddleware` loads it in its **own** DI scope (`context.InstanceServices.CreateScope()`), i.e. a different `DbContext`, so there is no tracked entity to reuse. The policy therefore reads the Users row once per MCP message for user callers (applications: no read). No `WithPartitionKey` (same as `GlobalAccessPolicy` and the admin user handlers; the Users container has a single logical partition `USER`).
4. **Missing properties on existing documents.** Verified against the emulator while writing this plan: EF Core Cosmos 10.0.10 materializes a user document that has no `McpAllowed`/`McpBlockedByAdmin` as `false` (Task 1 keeps this as a test: `LegacyUserDocument_WithoutTheFlags_ReadsAsFalse`). So deploying before the backfill is safe (everyone reads as off). The backfill is still implemented because the spec requires it and Cosmos SQL filters on a missing property evaluate to undefined (e.g. `c.McpAllowed = false` would not match legacy documents). It follows the existing pattern (pure `*Backfill` helper + `MigrationItem000NN_*` DTO + `RunMigration000NNAsync` dispatched from `RunMigrationAsync`, triggered by an admin with `POST /api/{v}/admops/run_dbmigration` body `{ "migrationId": "00013" }`) with two improvements: the replace is conditional on the read etag (like migration `00010`) and retried on `412`, so a concurrent opt-in is never overwritten; and the container loop is an `internal static` method so the emulator test runs the production code instead of a copy (as `TelemetryIdBackfillMigrationTests` does).
5. **Strict body parsing.** Inputs use `bool?` and the existing `DefaultJsonSerializer` (camelCase, case-sensitive). Missing, `null`, string, number, non-object or malformed JSON, and an empty body all yield `400`. (A PascalCase `McpAllowed` key is ignored and so also yields `400`, consistent with every other `DefaultJsonSerializer` body.)
6. **`PATCH admin/users/{upn}/mcp-blocked`** mirrors `enabled`: admin filter, then `404` for an unknown user, then body validation (`400` `Body must be { "blocked": bool }.`), then save with retry, returns the updated `UserOverviewOutput`. It is stricter than `enabled` about a missing field (the existing `enabled` toggle treats a missing `enabled` as `false`; left unchanged).
7. **S2S check on `/me/settings`** uses `ITokenHelper.IsUserToken` (no DB read) after the global filter, error `Settings are available to users only.`
8. **One retry helper** `UserUpdate.SaveWithRetryAsync(dbContext, user, apply)` (4 attempts, the existing constant) replaces the inline loop of `RunToggleEnabled` and is used by `mcp-blocked` and `/me/settings`. `apply` returning `false` abandons the save; `/me/settings` uses that to re-check `McpBlockedByAdmin` on every attempt, so a concurrently saved block wins.
9. **Logging** of denials and of both writes uses the telemetry id only (`tid {TelemetryId}`), never the UPN. (The existing `enabled` toggle log line is not touched.)
10. **Function names / routes:** `SafeExchange-Me-Settings-Get` (`get`), `SafeExchange-Me-Settings-Update` (`patch`), both `api/{apiVersion}/me/settings`; `SafeExchange-Admin-Users-ToggleMcpBlocked` (`patch`, `api/{apiVersion}/admin/users/{upn}/mcp-blocked`). They are authenticated automatically (`SafeExchangeFunctionsStartup.IsAuthenticatedHttpTrigger` exempts only `McpRoutes.AnonymousFunctionNames`), so no startup change; `McpChallengeTests` pins that.
11. Both writes set `ModifiedAt = DateTimeProvider.UtcNow`, like the `enabled` toggle.

## File structure

| File | Action | Responsibility |
|---|---|---|
| `SafeExchange.Core\Model\User.cs` | Modify | `McpAllowed`, `McpBlockedByAdmin` (both `false` for new users) |
| `SafeExchange.Core\Migrations\McpFlagsBackfill.cs` | Create | Pure JSON rewrite for migration 00013 |
| `SafeExchange.Core\Migrations\Model\MigrationItem00013_User.cs` | Create | `id` + `PartitionKey` projection DTO |
| `SafeExchange.Core\Migrations\MigrationsHelper.cs` | Modify | Dispatch `00013`; `BackfillMcpFlagsAsync(Container, ILogger)` |
| `SafeExchange.Core\Utilities\UserLookup.cs` | Create | Find the token's Users row (tid + oid) |
| `SafeExchange.Core\Utilities\UserUpdate.cs` | Create | Etag-conflict retry for user-document writes |
| `SafeExchange.Core\Mcp\McpUserAccessPolicy.cs` | Create | Opt-in/block decision + denial constants |
| `SafeExchange.Core\Mcp\McpServiceRegistration.cs` | Modify | Register the policy (scoped) |
| `SafeExchange.Core.Functions\Mcp\McpHandler.cs` | Modify | Call the policy after caller resolution |
| `SafeExchange.Core\Model\Dto\Input\UserSettingsInput.cs` | Create | `{ mcpAllowed: bool? }` |
| `SafeExchange.Core\Model\Dto\Output\UserSettingsOutput.cs` | Create | `{ mcpAllowed, mcpBlockedByAdmin, mcpEffective }` |
| `SafeExchange.Core.Functions\Functions\SafeExchangeMeSettings.cs` | Create | `GET/PATCH /me/settings` handler |
| `SafeExchange.Core\Model\Dto\Input\McpBlockedToggleInput.cs` | Create | `{ blocked: bool? }` |
| `SafeExchange.Core\Model\Dto\Output\UserOverviewOutput.cs` | Modify | + `McpAllowed`, `McpBlockedByAdmin` |
| `SafeExchange.Core\Model\Dto\Output\UserDetailOutput.cs` | Modify | + `McpAllowed`, `McpBlockedByAdmin` |
| `SafeExchange.Core.Functions\Functions\Admin\SafeExchangeAdminUsers.cs` | Modify | List/detail fields, `RunToggleMcpBlocked`, `enabled` uses `UserUpdate` |
| `SafeExchange.Functions\Functions\SafeMeSettings.cs` | Create | HTTP triggers for `/me/settings` |
| `SafeExchange.Functions\Functions\SafeAdminUsers.cs` | Modify | HTTP trigger for `mcp-blocked` |
| `SafeExchange.Tests\Tests\McpFlagsBackfillTests.cs` | Create | Backfill unit + emulator tests |
| `SafeExchange.Tests\Mcp\McpHttpTestBase.cs` | Modify | `UserObjectId`, `SeedMcpUserAsync` |
| `SafeExchange.Tests\Mcp\McpUserOptInTests.cs` | Create | Enforcement tests (EF InMemory) |
| `SafeExchange.Tests\Mcp\McpHandlerTests.cs`, `McpLoggingTests.cs` | Modify | Opt the owner in (`[SetUp]`) |
| `SafeExchange.Tests\Utilities\ConcurrentUserWriteInterceptor.cs` | Create | EF interceptor that forces one etag conflict |
| `SafeExchange.Tests\Tests\MeSettingsTests.cs` | Create | `/me/settings` tests (emulator) |
| `SafeExchange.Tests\Tests\AdminUsersMcpTests.cs` | Create | Admin list fields, `mcp-blocked`, retry tests (emulator) |
| `SafeExchange.Tests\Tests\AdminUserDetailTests.cs` | Modify | Detail returns the flags |
| `SafeExchange.Tests\Contract\FunctionRouteTests.cs` | Modify | Pin the three new routes |
| `SafeExchange.Tests\Mcp\McpChallengeTests.cs` | Modify | New triggers require authentication |
| `docs\api-endpoints.md`, `docs\mcp.md`, `README.md` | Modify | Document the feature |

Test infrastructure choice: MCP enforcement tests derive from `McpHttpTestBase` (EF InMemory, real `CallerResolver`, real DI via `AddSafeExchangeMcp`, production camelCase response serializer, log capture). Settings and admin tests run against the Cosmos emulator like `AdminUserDetailTests`, because only Cosmos enforces the `_etag` concurrency token the retry logic depends on.

## Before you start

All commands run from the worktree root `C:\Users\yurio\Documents\github\safeexchange-wt-optin` (branch `feat/mcp-user-opt-in`). Never touch `C:\Users\yurio\Documents\github\safeexchange`.

- [ ] **Start the emulators**

```powershell
docker run --detach --name saex-test-cosmos --publish 127.0.0.1:8081:8081 --publish 127.0.0.1:8080:8080 mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-EN20260907pre
docker run --detach --name saex-oauth-azurite --publish 127.0.0.1:10000:10000 --publish 127.0.0.1:10001:10001 --publish 127.0.0.1:10002:10002 mcr.microsoft.com/azure-storage/azurite
for ($i = 0; $i -lt 60; $i++) { try { if ((Invoke-WebRequest -UseBasicParsing http://localhost:8080/ready -TimeoutSec 5).StatusCode -eq 200) { 'ready'; break } } catch { Start-Sleep 5 } }
```

Expected: two container ids, then `ready`.

- [ ] **Point the tests at the emulator (http endpoint)**

```powershell
dotnet user-secrets set "ConnectionStrings:CosmosDb" "AccountEndpoint=http://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==" --project SafeExchange.Tests
```

Expected: `Successfully saved ConnectionStrings:CosmosDb to the secret store.`

- [ ] **Record the warning baseline** (`artifacts\` is git-ignored)

```powershell
New-Item -ItemType Directory -Force artifacts | Out-Null
dotnet build SafeExchange.slnx --nologo -v q --no-incremental 2>&1 | Select-String ": warning " | ForEach-Object { $_.Line -replace '\(\d+,\d+\)', '' } | Sort-Object -Unique | Set-Content artifacts\warnings-baseline.txt
```

Expected: the build succeeds (`0 Error(s)`); the file has roughly 620–630 lines (all pre-existing warnings, line numbers stripped so later line shifts do not count as new warnings).

- [ ] **Confirm the suite is green before changing anything**

```powershell
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build
```

Expected: `Passed!  - Failed:     0, Passed:  1462, Skipped:     0, Total:  1462`.

---

### Task 1: User flags and migration 00013 (backfill)

**Files:**
- Modify: `SafeExchange.Core\Model\User.cs` (constructor ≈line 17-20; properties after `ConsentRequired` ≈line 60)
- Create: `SafeExchange.Core\Migrations\McpFlagsBackfill.cs`
- Create: `SafeExchange.Core\Migrations\Model\MigrationItem00013_User.cs`
- Modify: `SafeExchange.Core\Migrations\MigrationsHelper.cs` (dispatch ≈line 108-112; new methods after `RunMigration00012Async`, end of class)
- Test: `SafeExchange.Tests\Tests\McpFlagsBackfillTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `SafeExchange.Tests\Tests\McpFlagsBackfillTests.cs`:

```csharp
/// <summary>
/// McpFlagsBackfillTests — the McpAllowed / McpBlockedByAdmin backfill (migration 00013): the pure
/// document rewrite, and the migration against the Cosmos DB emulator.
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.Azure.Cosmos;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging.Abstractions;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Migrations;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json.Nodes;
    using System.Threading.Tasks;
    using CoreUser = SafeExchange.Core.Model.User;

    [TestFixture]
    public class McpFlagsBackfillTests
    {
        [Test]
        public void BackfillIfMissing_AddsBothFlagsFalse_WhenMissing()
        {
            var result = McpFlagsBackfill.BackfillIfMissing("""{"id":"u1","PartitionKey":"USER"}""");

            Assert.That(result, Is.EqualTo("""{"id":"u1","PartitionKey":"USER","McpAllowed":false,"McpBlockedByAdmin":false}"""));
        }

        [Test]
        public void BackfillIfMissing_TreatsNullAsMissing_AndKeepsAnExistingFlag()
        {
            var result = McpFlagsBackfill.BackfillIfMissing("""{"id":"u1","McpAllowed":true,"McpBlockedByAdmin":null}""");

            Assert.That(result, Is.EqualTo("""{"id":"u1","McpAllowed":true,"McpBlockedByAdmin":false}"""));
        }

        [TestCase("""{"id":"u1","McpAllowed":false,"McpBlockedByAdmin":false}""")]
        [TestCase("""{"id":"u1","McpAllowed":true,"McpBlockedByAdmin":true}""")]
        [TestCase("not json")]
        [TestCase("[]")]
        public void BackfillIfMissing_ReturnsNull_WhenNothingToDo(string document)
        {
            Assert.That(McpFlagsBackfill.BackfillIfMissing(document), Is.Null);
        }
    }

    [TestFixture]
    public class McpFlagsBackfillMigrationTests
    {
        private const string DatabaseName = $"{nameof(McpFlagsBackfillMigrationTests)}Database";

        private SafeExchangeDbContext dbContext = null!;

        private CosmosClient cosmosClient = null!;

        private Container users = null!;

        [OneTimeSetUp]
        public async Task OneTimeSetup()
        {
            var connectionString = new ConfigurationBuilder().AddUserSecrets<McpFlagsBackfillMigrationTests>().Build().GetConnectionString("CosmosDb");
            var options = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(connectionString!, DatabaseName, CosmosTestOptions.UseGateway)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CosmosEventId.SyncNotSupported))
                .Options;

            this.dbContext = new SafeExchangeDbContext(options);
            await this.dbContext.Database.EnsureCreatedAsync();

            this.cosmosClient = CosmosTestOptions.CreateClient(connectionString!);
            this.users = this.cosmosClient.GetDatabase(DatabaseName).GetContainer("Users");
        }

        [OneTimeTearDown]
        public void OneTimeCleanup()
        {
            TestDatabase.EnsureDeletedTolerant(this.dbContext.Database);
            this.dbContext.Dispose();
            this.cosmosClient.Dispose();
        }

        [TearDown]
        public void Cleanup()
        {
            this.dbContext.ChangeTracker.Clear();
            this.dbContext.Users.RemoveRange(this.dbContext.Users.ToList());
            this.dbContext.SaveChanges();
            this.dbContext.ChangeTracker.Clear();
        }

        [Test]
        public async Task LegacyUserDocument_WithoutTheFlags_ReadsAsFalse()
        {
            var legacy = await this.SeedLegacyUserAsync("legacy-read@test.test");

            var user = await this.dbContext.Users.AsNoTracking().SingleAsync(u => u.Id == legacy.Id);

            Assert.That((await this.ReadRawAsync(legacy.Id)).ContainsKey("McpAllowed"), Is.False, "Arrange: the document has no McpAllowed.");
            Assert.That(user.McpAllowed, Is.False);
            Assert.That(user.McpBlockedByAdmin, Is.False);
        }

        [Test]
        public async Task Migration00013_WritesBothFlagsFalse_OntoLegacyUsers_AndIsIdempotent()
        {
            var legacy = await this.SeedLegacyUserAsync("legacy@test.test");

            var first = await MigrationsHelper.BackfillMcpFlagsAsync(this.users, NullLogger.Instance);
            var second = await MigrationsHelper.BackfillMcpFlagsAsync(this.users, NullLogger.Instance);

            var raw = await this.ReadRawAsync(legacy.Id);
            Assert.That(first, Is.EqualTo(1));
            Assert.That(second, Is.EqualTo(0));
            Assert.That(raw["McpAllowed"]!.GetValue<bool>(), Is.False);
            Assert.That(raw["McpBlockedByAdmin"]!.GetValue<bool>(), Is.False);
            Assert.That(raw["AadUpn"]!.GetValue<string>(), Is.EqualTo("legacy@test.test"));
        }

        [Test]
        public async Task Migration00013_LeavesUsersThatHaveTheFlags_Unchanged()
        {
            var current = new CoreUser("Current", "oid-current", "tid-test", "current@test.test", "current@test.test")
            {
                McpAllowed = true,
                McpBlockedByAdmin = true,
            };
            this.dbContext.Users.Add(current);
            await this.dbContext.SaveChangesAsync();
            var etagBefore = (await this.ReadRawAsync(current.Id))["_etag"]!.GetValue<string>();

            var backfilled = await MigrationsHelper.BackfillMcpFlagsAsync(this.users, NullLogger.Instance);

            var raw = await this.ReadRawAsync(current.Id);
            Assert.That(backfilled, Is.EqualTo(0));
            Assert.That(raw["_etag"]!.GetValue<string>(), Is.EqualTo(etagBefore));
            Assert.That(raw["McpAllowed"]!.GetValue<bool>(), Is.True);
            Assert.That(raw["McpBlockedByAdmin"]!.GetValue<bool>(), Is.True);
        }

        // A user written before the flags existed: saved through EF, then the two properties removed from the stored document.
        private async Task<CoreUser> SeedLegacyUserAsync(string upn)
        {
            var user = new CoreUser(upn, $"oid-{upn}", "tid-test", upn, upn);
            this.dbContext.Users.Add(user);
            await this.dbContext.SaveChangesAsync();
            this.dbContext.ChangeTracker.Clear();

            var document = await this.ReadRawAsync(user.Id);
            document.Remove("McpAllowed");
            document.Remove("McpBlockedByAdmin");
            using var content = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString()));
            using var replaced = await this.users.ReplaceItemStreamAsync(content, user.Id, new PartitionKey(CoreUser.DefaultPartitionKey));
            replaced.EnsureSuccessStatusCode();
            return user;
        }

        private async Task<JsonObject> ReadRawAsync(string id)
        {
            using var response = await this.users.ReadItemStreamAsync(id, new PartitionKey(CoreUser.DefaultPartitionKey));
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(response.Content);
            return JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
        }
    }
}
```

- [ ] **Step 2: Build to verify the tests fail**

Run: `dotnet build SafeExchange.slnx --nologo -v q 2>&1 | Select-String " error " | Select-Object -First 5`
Expected: FAIL — errors in `McpFlagsBackfillTests.cs` such as `CS0103: The name 'McpFlagsBackfill' does not exist in the current context`, `CS0117: 'User' does not contain a definition for 'McpAllowed'` and `CS0117: 'MigrationsHelper' does not contain a definition for 'BackfillMcpFlagsAsync'`.

- [ ] **Step 3: Add the flags to `User`**

In `SafeExchange.Core\Model\User.cs`, constructor — replace:

```csharp
            this.Enabled = true;
            this.ReceiveExternalNotifications = true;
```

with:

```csharp
            this.Enabled = true;
            this.ReceiveExternalNotifications = true;
            this.McpAllowed = false;
            this.McpBlockedByAdmin = false;
```

and the properties — replace:

```csharp
        public bool ConsentRequired { get; set; }

        public DateTime CreatedAt { get; set; }
```

with:

```csharp
        public bool ConsentRequired { get; set; }

        /// <summary>The user's own choice to allow their account to be used through the MCP endpoint
        /// (opt-in, off by default). Changed only through <c>PATCH /me/settings</c>.</summary>
        public bool McpAllowed { get; set; }

        /// <summary>An administrator blocked MCP for this user; overrides <see cref="McpAllowed"/>.
        /// Changed only through <c>PATCH /admin/users/{upn}/mcp-blocked</c>.</summary>
        public bool McpBlockedByAdmin { get; set; }

        public DateTime CreatedAt { get; set; }
```

No `SafeExchangeDbContext` change is needed: the `User` entity maps every public property (EF Cosmos stores them under the property name, e.g. `"McpAllowed"`).

- [ ] **Step 4: Add the pure backfill helper**

Create `SafeExchange.Core\Migrations\McpFlagsBackfill.cs`:

```csharp
/// <summary>
/// McpFlagsBackfill — pure helper for the McpAllowed / McpBlockedByAdmin backfill migration (00013).
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using System.Text.Json;
    using System.Text.Json.Nodes;

    public static class McpFlagsBackfill
    {
        private static readonly string[] FlagNames = { "McpAllowed", "McpBlockedByAdmin" };

        /// <summary>
        /// Returns the rewritten user document JSON with <c>McpAllowed = false</c> and/or
        /// <c>McpBlockedByAdmin = false</c> added where the field is missing or <c>null</c>.
        /// Existing values (including <c>true</c>) are never changed. Returns <c>null</c> when
        /// both fields are already present or the JSON is not an object.
        /// </summary>
        public static string? BackfillIfMissing(string documentJson)
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(documentJson);
            }
            catch (JsonException)
            {
                return null;
            }

            if (node is not JsonObject document)
            {
                return null;
            }

            var changed = false;
            foreach (var name in FlagNames)
            {
                // A JSON null and a missing property both read as a null node.
                if (document[name] is null)
                {
                    document[name] = false;
                    changed = true;
                }
            }

            return changed ? document.ToJsonString() : null;
        }
    }
}
```

Create `SafeExchange.Core\Migrations\Model\MigrationItem00013_User.cs`:

```csharp
/// <summary>
/// MigrationItem00013_User
/// </summary>

namespace SafeExchange.Core.Migrations
{
    /// <summary>
    /// Marker DTO used by the MCP flags backfill migration: only the fields needed to
    /// read and replace a document of the Users container.
    /// </summary>
    public class MigrationItem00013_User
    {
        public string PartitionKey { get; set; } = string.Empty;

        public string id { get; set; } = string.Empty;
    }
}
```

- [ ] **Step 5: Register migration 00013**

In `SafeExchange.Core\Migrations\MigrationsHelper.cs`, `RunMigrationAsync` — replace:

```csharp
                if ("00012".Equals(migrationId, StringComparison.InvariantCultureIgnoreCase))
                {
                    await this.RunMigration00012Async();
                    return;
                }
```

with:

```csharp
                if ("00012".Equals(migrationId, StringComparison.InvariantCultureIgnoreCase))
                {
                    await this.RunMigration00012Async();
                    return;
                }

                if ("00013".Equals(migrationId, StringComparison.InvariantCultureIgnoreCase))
                {
                    await this.RunMigration00013Async();
                    return;
                }
```

At the end of `RunMigration00012Async` — replace:

```csharp
            this.log.LogInformation($"Migration 00012 complete. Backfilled {backfilled} document(s).");
        }
```

with:

```csharp
            this.log.LogInformation($"Migration 00012 complete. Backfilled {backfilled} document(s).");
        }

        private async Task RunMigration00013Async()
        {
            using CosmosClient client = new CosmosClient(this.dbConfiguration.CosmosDbEndpoint, this.tokenCredential);
            var database = client.GetDatabase(this.dbConfiguration.DatabaseName);
            var container = database.GetContainer("Users");

            var backfilled = await BackfillMcpFlagsAsync(container, this.log);
            this.log.LogInformation($"Migration 00013 complete. Backfilled {backfilled} document(s).");
        }

        /// <summary>
        /// Writes <c>McpAllowed = false</c> / <c>McpBlockedByAdmin = false</c> onto every user document
        /// that lacks them and returns how many documents were rewritten. Idempotent.
        /// </summary>
        internal static async Task<int> BackfillMcpFlagsAsync(Container container, ILogger log)
        {
            // Already-backfilled users are filtered server-side so re-runs are cheap.
            var query = new QueryDefinition(
                "SELECT c.id, c.PartitionKey FROM c WHERE NOT IS_DEFINED(c.McpAllowed) OR IS_NULL(c.McpAllowed) OR NOT IS_DEFINED(c.McpBlockedByAdmin) OR IS_NULL(c.McpBlockedByAdmin)");

            using FeedIterator<MigrationItem00013_User> feed =
                container.GetItemQueryIterator<MigrationItem00013_User>(queryDefinition: query);

            var backfilled = 0;
            while (feed.HasMoreResults)
            {
                FeedResponse<MigrationItem00013_User> response = await feed.ReadNextAsync();
                foreach (MigrationItem00013_User item in response)
                {
                    if (await BackfillMcpFlagsOnUserAsync(container, item, log))
                    {
                        backfilled++;
                    }
                }
            }

            return backfilled;
        }

        private static async Task<bool> BackfillMcpFlagsOnUserAsync(Container container, MigrationItem00013_User item, ILogger log)
        {
            // The replace is conditional on the etag of the read: the user document is written
            // concurrently (the user's own opt-in, telemetry-id rotation, group sync), and an
            // unconditional replace could overwrite such a write. On 412 read again and re-check.
            const int maxAttempts = 3;
            var partitionKey = new PartitionKey(item.PartitionKey);
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                using var readResponse = await container.ReadItemStreamAsync(item.id, partitionKey);
                if (!readResponse.IsSuccessStatusCode)
                {
                    return false;
                }

                var etag = readResponse.Headers.ETag;
                string original;
                using (var reader = new StreamReader(readResponse.Content))
                {
                    original = await reader.ReadToEndAsync();
                }

                var rewritten = McpFlagsBackfill.BackfillIfMissing(original);
                if (rewritten is null)
                {
                    return false;
                }

                using var content = new MemoryStream(Encoding.UTF8.GetBytes(rewritten));
                using var replaceResponse = await container.ReplaceItemStreamAsync(
                    content, item.id, partitionKey, new ItemRequestOptions { IfMatchEtag = etag });
                if (replaceResponse.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
                {
                    log.LogInformation($"User '{item.id}' changed during the MCP flags backfill, reading it again.");
                    continue;
                }

                replaceResponse.EnsureSuccessStatusCode();
                log.LogInformation($"Backfilled McpAllowed/McpBlockedByAdmin on User '{item.id}'.");
                return true;
            }

            log.LogWarning($"Skipped User '{item.id}' after {maxAttempts} concurrent changes; run migration 00013 again.");
            return false;
        }
```

(All types used are already imported by `MigrationsHelper.cs`: `Microsoft.Azure.Cosmos`, `Microsoft.Extensions.Logging`, `System.Text`. `MigrationsHelper` is `internal`; `SafeExchange.Core.csproj` already has `InternalsVisibleTo SafeExchange.Tests`. Logging the document id follows migrations 00011/00012; it is a random GUID, not personal data.)

- [ ] **Step 6: Build and run the tests**

Run:

```powershell
dotnet build SafeExchange.slnx --nologo -v q 2>&1 | Select-String " error "
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~McpFlagsBackfill"
```

Expected: no build errors; `Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9`.

- [ ] **Step 7: Commit**

```powershell
git add SafeExchange.Core\Model\User.cs SafeExchange.Core\Migrations\McpFlagsBackfill.cs SafeExchange.Core\Migrations\Model\MigrationItem00013_User.cs SafeExchange.Core\Migrations\MigrationsHelper.cs SafeExchange.Tests\Tests\McpFlagsBackfillTests.cs
git commit -m "feat(users): McpAllowed/McpBlockedByAdmin flags and migration 00013 backfill" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 2: Enforce the opt-in on the MCP endpoint

**Files:**
- Create: `SafeExchange.Core\Utilities\UserLookup.cs`
- Create: `SafeExchange.Core\Mcp\McpUserAccessPolicy.cs`
- Modify: `SafeExchange.Core\Mcp\McpServiceRegistration.cs` (≈line 17-19 doc comment, ≈line 41-42 registrations)
- Modify: `SafeExchange.Core.Functions\Mcp\McpHandler.cs` (class comment ≈line 21-24, fields/ctor ≈line 27-38, `Run` ≈line 57-62)
- Modify: `SafeExchange.Tests\Mcp\McpHttpTestBase.cs`
- Modify: `SafeExchange.Tests\Mcp\McpHandlerTests.cs`, `SafeExchange.Tests\Mcp\McpLoggingTests.cs`
- Test: `SafeExchange.Tests\Mcp\McpUserOptInTests.cs`

- [ ] **Step 1: Give the MCP HTTP test base a way to seed the caller's Users row**

In `SafeExchange.Tests\Mcp\McpHttpTestBase.cs` replace:

```csharp
        protected const string AgentClientId = "00000000-0000-0000-0000-0000000000c1";
```

with:

```csharp
        protected const string AgentClientId = "00000000-0000-0000-0000-0000000000c1";

        protected const string UserObjectId = "00000000-0000-0000-0000-0000000000a1";
```

replace (in `UserPrincipal`):

```csharp
                new("oid", "00000000-0000-0000-0000-0000000000a1"),
```

with:

```csharp
                new("oid", UserObjectId),
```

and replace:

```csharp
        protected static string BodyOf(HttpResponseData response) => ((TestHttpResponseData)response).ReadBodyAsString();
```

with:

```csharp
        protected static string BodyOf(HttpResponseData response) => ((TestHttpResponseData)response).ReadBodyAsString();

        /// <summary>
        /// Adds the Users row that <see cref="UserPrincipal"/> resolves to (one per test: every user principal
        /// carries <see cref="UserObjectId"/>), with the given MCP settings.
        /// </summary>
        protected async Task<User> SeedMcpUserAsync(string upn, bool mcpAllowed, bool mcpBlockedByAdmin = false)
        {
            var user = new User(upn, UserObjectId, TenantId, upn, upn)
            {
                McpAllowed = mcpAllowed,
                McpBlockedByAdmin = mcpBlockedByAdmin,
            };
            this.DbContext.Users.Add(user);
            await this.DbContext.SaveChangesAsync();
            return user;
        }
```

(`SafeExchange.Core.Model` and `System.Threading.Tasks` are already imported there.)

- [ ] **Step 2: Write the failing enforcement tests**

Create `SafeExchange.Tests\Mcp\McpUserOptInTests.cs`:

```csharp
/// <summary>
/// McpUserOptInTests
/// </summary>

namespace SafeExchange.Tests.Mcp
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using NUnit.Framework;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Services;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Text.Json.Nodes;
    using System.Threading.Tasks;

    /// <summary>
    /// A user may use MCP only after opting in (<c>McpAllowed</c>), and an administrator block
    /// (<c>McpBlockedByAdmin</c>) wins. Applications and REST are not affected.
    /// </summary>
    [TestFixture]
    public class McpUserOptInTests : McpHttpTestBase
    {
        private const string UserTelemetryId = "0123456789abcdef0123456789abcdef";

        private static readonly string WhoAmI = Rpc("tools/call", new { name = "whoami", arguments = new { } });

        [Test]
        public async Task UserWithoutAUsersRow_IsRefused_McpNotAllowed()
        {
            var response = await this.CallAsync(UserPrincipal(OwnerUpn));

            AssertDenied(response, McpUserAccessPolicy.NotAllowedSubStatus, McpUserAccessPolicy.NotAllowedError);
            Assert.That(this.Logs.Entries.Any(e => e.Contains("MCP tool")), Is.False);
        }

        [Test]
        public async Task UserWhoDidNotOptIn_IsRefused_McpNotAllowed()
        {
            await this.SeedMcpUserAsync(OwnerUpn, mcpAllowed: false);

            var response = await this.CallAsync(UserPrincipal(OwnerUpn));

            AssertDenied(response, McpUserAccessPolicy.NotAllowedSubStatus, McpUserAccessPolicy.NotAllowedError);
        }

        [Test]
        public async Task UserWhoOptedIn_CallsTools()
        {
            await this.SeedMcpUserAsync(OwnerUpn, mcpAllowed: true);

            var response = await this.CallAsync(UserPrincipal(OwnerUpn));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var data = JsonNode.Parse(BodyOf(response))!["result"]!["structuredContent"]!;
            Assert.That(data["subjectType"]!.GetValue<string>(), Is.EqualTo("User"));
            Assert.That(data["subjectId"]!.GetValue<string>(), Is.EqualTo(OwnerUpn));
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task BlockedUser_IsRefused_McpBlockedByAdmin_WhateverTheirChoice(bool mcpAllowed)
        {
            await this.SeedMcpUserAsync(OwnerUpn, mcpAllowed, mcpBlockedByAdmin: true);

            var response = await this.CallAsync(UserPrincipal(OwnerUpn));

            AssertDenied(response, McpUserAccessPolicy.BlockedByAdminSubStatus, McpUserAccessPolicy.BlockedByAdminError);
        }

        [Test]
        public async Task ChangedSetting_AppliesToTheNextMessage()
        {
            var user = await this.SeedMcpUserAsync(OwnerUpn, mcpAllowed: true);
            var allowed = await this.CallAsync(UserPrincipal(OwnerUpn));

            user.McpBlockedByAdmin = true;
            await this.DbContext.SaveChangesAsync();
            var refused = await this.CallAsync(UserPrincipal(OwnerUpn));

            Assert.That(allowed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            AssertDenied(refused, McpUserAccessPolicy.BlockedByAdminSubStatus, McpUserAccessPolicy.BlockedByAdminError);
        }

        [Test]
        public async Task Application_IsNotSubjectToTheUserSetting()
        {
            await this.SeedApplicationAsync(AgentClientId, "mcp-agent", enabled: true);

            var response = await this.CallAsync(AppPrincipal(AgentClientId));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public async Task Rest_IsNotSubjectToTheUserSetting()
        {
            await this.SeedMcpUserAsync(OwnerUpn, mcpAllowed: false, mcpBlockedByAdmin: true);
            var secretMeta = new SafeExchangeSecretMeta(this.Services.GetRequiredService<ICallerResolver>(), this.Services.GetRequiredService<SecretService>());

            var response = await secretMeta.RunListV3(HttpRequest("get"), UserPrincipal(OwnerUpn), this.Logger());

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public async Task Refusal_IsLoggedWithTheTelemetryId_NotTheUpn()
        {
            var user = await this.SeedMcpUserAsync(OwnerUpn, mcpAllowed: false);
            user.TelemetryId = UserTelemetryId;
            await this.DbContext.SaveChangesAsync();

            await this.CallAsync(UserPrincipal(OwnerUpn));

            Assert.That(this.Logs.Entries, Has.Some.Contains($"MCP refused for user (tid {UserTelemetryId}): mcp_not_allowed."));
            Assert.That(this.Logs.Entries, Has.None.Contains(OwnerUpn));
        }

        private static void AssertDenied(HttpResponseData response, string subStatus, string error)
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            var body = JsonNode.Parse(BodyOf(response))!.AsObject();
            Assert.That(body["status"]!.GetValue<string>(), Is.EqualTo("forbidden"));
            Assert.That(body["subStatus"]!.GetValue<string>(), Is.EqualTo(subStatus));
            Assert.That(body["error"]!.GetValue<string>(), Is.EqualTo(error));
        }

        private Task<HttpResponseData> CallAsync(ClaimsPrincipal principal)
            => this.Handler.Run(HttpRequest("post", WhoAmI), principal, this.Logger());

        private ILogger Logger() => this.Logs.CreateLogger("SafeExchange.Functions.SafeMcp");
    }
}
```

(The body is compared through `JsonNode` because the production serializer escapes `'` as `\u0027`.)

- [ ] **Step 3: Build to verify the tests fail**

Run: `dotnet build SafeExchange.slnx --nologo -v q 2>&1 | Select-String " error " | Select-Object -First 3`
Expected: FAIL — `CS0103: The name 'McpUserAccessPolicy' does not exist in the current context` in `McpUserOptInTests.cs`.

- [ ] **Step 4: Add the user lookup**

Create `SafeExchange.Core\Utilities\UserLookup.cs`:

```csharp
/// <summary>
/// UserLookup
/// </summary>

namespace SafeExchange.Core.Utilities
{
    using Microsoft.EntityFrameworkCore;
    using SafeExchange.Core.Model;
    using System.Security.Claims;
    using System.Threading;
    using System.Threading.Tasks;

    public static class UserLookup
    {
        /// <summary>
        /// The Users row of a user token's principal, matched on tenant id and object id the way the
        /// token middleware finds or creates it; <c>null</c> when the token lacks them or no row exists.
        /// The entity is tracked, so callers can change and save it.
        /// </summary>
        public static async Task<User?> FindByTokenAsync(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, ClaimsPrincipal principal, CancellationToken cancellationToken = default)
        {
            var tenantId = tokenHelper.GetTenantId(principal);
            var objectId = tokenHelper.GetObjectId(principal);
            if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(objectId))
            {
                return null;
            }

            return await dbContext.Users.FirstOrDefaultAsync(u => u.AadTenantId == tenantId && u.AadObjectId == objectId, cancellationToken);
        }
    }
}
```

- [ ] **Step 5: Add the policy and register it**

Create `SafeExchange.Core\Mcp\McpUserAccessPolicy.cs`:

```csharp
/// <summary>
/// McpUserAccessPolicy
/// </summary>

namespace SafeExchange.Core.Mcp
{
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Telemetry;
    using SafeExchange.Core.Utilities;
    using System;
    using System.Security.Claims;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Per-user MCP opt-in. A user caller may use MCP only when they allowed it (<see cref="User.McpAllowed"/>)
    /// and no administrator blocked it (<see cref="User.McpBlockedByAdmin"/>, which wins); a user without a
    /// Users row has not allowed it. Applications are not subject to it. Evaluated on every MCP message.
    /// </summary>
    public sealed class McpUserAccessPolicy
    {
        public const string NotAllowedSubStatus = "mcp_not_allowed";

        public const string BlockedByAdminSubStatus = "mcp_blocked_by_admin";

        public const string NotAllowedError = "MCP access is not enabled for this account. Turn on 'Allow MCP' in SafeExchange settings.";

        public const string BlockedByAdminError = "MCP access is blocked for this account by an administrator.";

        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly ILogger log;

        public McpUserAccessPolicy(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, ILogger<McpUserAccessPolicy> log)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// Returns <c>null</c> when the caller may use MCP, otherwise the denial.
        /// </summary>
        public async Task<CallerDenial?> EvaluateAsync(ClaimsPrincipal principal, Caller caller, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(principal);
            ArgumentNullException.ThrowIfNull(caller);

            if (!SubjectType.User.Equals(caller.SubjectType))
            {
                return null;
            }

            var user = await UserLookup.FindByTokenAsync(this.dbContext, this.tokenHelper, principal, cancellationToken);
            if (user is { McpBlockedByAdmin: true })
            {
                return this.Deny(user, BlockedByAdminError, BlockedByAdminSubStatus);
            }

            if (user is not { McpAllowed: true })
            {
                return this.Deny(user, NotAllowedError, NotAllowedSubStatus);
            }

            return null;
        }

        private CallerDenial Deny(User? user, string error, string subStatus)
        {
            this.log.LogInformation("MCP refused for user (tid {TelemetryId}): {SubStatus}.", user?.TelemetryId ?? TelemetryContext.Current, subStatus);
            return CallerDenial.Forbidden(error, subStatus);
        }
    }
}
```

In `SafeExchange.Core\Mcp\McpServiceRegistration.cs` replace:

```csharp
        /// Registers the MCP tools (scoped, like the services they call), their registry, the dispatcher, the
        /// protected-resource facts and the OAuth broker with its Entra token client, Entra call gate and rate limits.
```

with:

```csharp
        /// Registers the MCP tools (scoped, like the services they call), their registry, the dispatcher, the
        /// per-user opt-in policy, the protected-resource facts and the OAuth broker with its Entra token client,
        /// Entra call gate and rate limits.
```

and replace:

```csharp
            services.AddScoped<McpToolRegistry>();
            services.AddScoped<McpRequestDispatcher>();
```

with:

```csharp
            services.AddScoped<McpToolRegistry>();
            services.AddScoped<McpRequestDispatcher>();
            services.AddScoped<McpUserAccessPolicy>();
```

(`AddSafeExchangeMcp` is called by `SafeExchangeStartup.ConfigureServices` and by `McpTestBase`; both already register `SafeExchangeDbContext`, `ITokenHelper` and logging.)

- [ ] **Step 6: Run the new tests to see them fail for the right reason**

Run:

```powershell
dotnet build SafeExchange.slnx --nologo -v q 2>&1 | Select-String " error "
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~McpUserOptInTests"
```

Expected: builds; `Failed!  - Failed:     6, Passed:     3` — the five refusal tests fail with `Expected: Forbidden But was: OK` and `Refusal_IsLoggedWithTheTelemetryId_NotTheUpn` finds no refusal log line (the policy exists but `McpHandler` does not call it yet); `UserWhoOptedIn_CallsTools`, `Application_IsNotSubjectToTheUserSetting` and `Rest_IsNotSubjectToTheUserSetting` pass.

- [ ] **Step 7: Call the policy from `McpHandler`**

In `SafeExchange.Core.Functions\Mcp\McpHandler.cs` replace the class comment lines:

```csharp
    /// Functions adapter for <c>/api/v1/mcp</c>: feature flag, method check and caller resolution (a denial is
    /// answered exactly like REST, before any MCP processing), then <see cref="McpRequestDispatcher"/>.
```

with:

```csharp
    /// Functions adapter for <c>/api/v1/mcp</c>: feature flag, method check, caller resolution and the per-user
    /// opt-in (<see cref="McpUserAccessPolicy"/>); a denial is answered exactly like REST, before any MCP
    /// processing. Then <see cref="McpRequestDispatcher"/>.
```

replace:

```csharp
        private readonly ICallerResolver callerResolver;

        private readonly McpRequestDispatcher dispatcher;

        private readonly IOptionsMonitor<Features> features;

        public McpHandler(ICallerResolver callerResolver, McpRequestDispatcher dispatcher, IOptionsMonitor<Features> features)
        {
            this.callerResolver = callerResolver ?? throw new ArgumentNullException(nameof(callerResolver));
```

with:

```csharp
        private readonly ICallerResolver callerResolver;

        private readonly McpUserAccessPolicy userAccessPolicy;

        private readonly McpRequestDispatcher dispatcher;

        private readonly IOptionsMonitor<Features> features;

        public McpHandler(ICallerResolver callerResolver, McpUserAccessPolicy userAccessPolicy, McpRequestDispatcher dispatcher, IOptionsMonitor<Features> features)
        {
            this.callerResolver = callerResolver ?? throw new ArgumentNullException(nameof(callerResolver));
            this.userAccessPolicy = userAccessPolicy ?? throw new ArgumentNullException(nameof(userAccessPolicy));
```

and in `Run` replace:

```csharp
                return await resolution.Denial.ToResponseAsync(request);
            }

            log.LogInformation(
```

with:

```csharp
                return await resolution.Denial.ToResponseAsync(request);
            }

            var optInDenial = await this.userAccessPolicy.EvaluateAsync(principal, resolution.Caller);
            if (optInDenial is not null)
            {
                return await optInDenial.ToResponseAsync(request);
            }

            log.LogInformation(
```

(`SafeExchange.Core.Mcp` and `SafeExchange.Core.Callers` are already imported. `McpHandler` is resolved from DI (`AddScoped<McpHandler>` in `SafeExchangeFunctionsStartup` and `McpHttpTestBase`), so no registration changes.)

- [ ] **Step 8: Run the new tests**

Run:

```powershell
dotnet build SafeExchange.slnx --nologo -v q 2>&1 | Select-String " error "
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~McpUserOptInTests"
```

Expected: `Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9`.

- [ ] **Step 9: Run the MCP regression tests to find callers that must now opt in**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchange.Tests.Mcp|FullyQualifiedName~ServiceRegistrationTests"`
Expected: `Failed!  - Failed:     4` — exactly `McpHandlerTests.SessionIdFromInitialize_LabelsTheAuditEventsOfLaterCalls`, `McpHandlerTests.MalformedSessionId_IsIgnored`, `McpHandlerTests.Notification_IsAcceptedWithoutABody` and `McpLoggingTests.AFullSession_LogsNoSecretValueChunkDataTokenOrSessionId` (they call the handler as a user who has no Users row). `ServiceRegistrationTests.HandlersFromDi_UseTheRegisteredCallerResolver` passes, proving `McpHandler` still resolves from the production registrations.

- [ ] **Step 10: Opt the owner in for those fixtures**

In `SafeExchange.Tests\Mcp\McpHandlerTests.cs` replace:

```csharp
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"copilot-cli","version":"1.0.114"}}}
            """;
```

with:

```csharp
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"copilot-cli","version":"1.0.114"}}}
            """;

        [SetUp]
        public Task OptInOwnerAsync() => this.SeedMcpUserAsync(OwnerUpn, mcpAllowed: true);
```

In `SafeExchange.Tests\Mcp\McpLoggingTests.cs` replace:

```csharp
        private const string Token = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJsb2ctdGVzdCJ9.c2lnbmF0dXJl";
```

with:

```csharp
        private const string Token = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJsb2ctdGVzdCJ9.c2lnbmF0dXJl";

        [SetUp]
        public Task OptInOwnerAsync() => this.SeedMcpUserAsync(OwnerUpn, mcpAllowed: true);
```

(NUnit runs the base-class `[SetUp]`s first, so `DbContext` exists.)

- [ ] **Step 11: Re-run the MCP regression tests**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchange.Tests.Mcp|FullyQualifiedName~ServiceRegistrationTests"` (after `dotnet build SafeExchange.slnx --nologo -v q`)
Expected: `Passed!  - Failed:     0, Passed:   496, Skipped:     0, Total:   496`.

- [ ] **Step 12: Commit**

```powershell
git add SafeExchange.Core\Utilities\UserLookup.cs SafeExchange.Core\Mcp\McpUserAccessPolicy.cs SafeExchange.Core\Mcp\McpServiceRegistration.cs SafeExchange.Core.Functions\Mcp\McpHandler.cs SafeExchange.Tests\Mcp\McpHttpTestBase.cs SafeExchange.Tests\Mcp\McpUserOptInTests.cs SafeExchange.Tests\Mcp\McpHandlerTests.cs SafeExchange.Tests\Mcp\McpLoggingTests.cs
git commit -m "feat(mcp): refuse MCP for users who have not opted in or are blocked by an admin" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 3: `GET`/`PATCH /me/settings` handler and the shared retry helper

**Files:**
- Create: `SafeExchange.Core\Utilities\UserUpdate.cs`
- Create: `SafeExchange.Core\Model\Dto\Input\UserSettingsInput.cs`
- Create: `SafeExchange.Core\Model\Dto\Output\UserSettingsOutput.cs`
- Create: `SafeExchange.Core.Functions\Functions\SafeExchangeMeSettings.cs`
- Create: `SafeExchange.Tests\Utilities\ConcurrentUserWriteInterceptor.cs`
- Test: `SafeExchange.Tests\Tests\MeSettingsTests.cs`

- [ ] **Step 1: Add the concurrent-writer test utility**

Create `SafeExchange.Tests\Utilities\ConcurrentUserWriteInterceptor.cs`:

```csharp
/// <summary>
/// ConcurrentUserWriteInterceptor
/// </summary>

namespace SafeExchange.Tests.Utilities
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.EntityFrameworkCore.Diagnostics;
    using SafeExchange.Core;
    using SafeExchange.Core.Model;
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Simulates a concurrent writer: right before the first save of the intercepted context, saves the given
    /// change to the same user through a separate context, so the intercepted save carries a stale etag and
    /// Cosmos answers 412 (a <see cref="DbUpdateConcurrencyException"/>).
    /// </summary>
    public sealed class ConcurrentUserWriteInterceptor : SaveChangesInterceptor
    {
        private readonly DbContextOptions<SafeExchangeDbContext> writerOptions;

        private readonly string userId;

        private readonly Action<User> concurrentChange;

        public ConcurrentUserWriteInterceptor(DbContextOptions<SafeExchangeDbContext> writerOptions, string userId, Action<User> concurrentChange)
        {
            this.writerOptions = writerOptions ?? throw new ArgumentNullException(nameof(writerOptions));
            this.userId = userId ?? throw new ArgumentNullException(nameof(userId));
            this.concurrentChange = concurrentChange ?? throw new ArgumentNullException(nameof(concurrentChange));
        }

        /// <summary>How many concurrent writes were made (at most one).</summary>
        public int Writes { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (this.Writes == 0)
            {
                this.Writes++;
                await using var writer = new SafeExchangeDbContext(this.writerOptions);
                var user = await writer.Users.SingleAsync(u => u.Id == this.userId, cancellationToken);
                this.concurrentChange(user);
                await writer.SaveChangesAsync(cancellationToken);
            }

            return result;
        }
    }
}
```

`writerOptions` must be options **without** this interceptor (the tests pass the fixture's plain options), otherwise the writer would intercept itself.

- [ ] **Step 2: Write the failing tests**

Create `SafeExchange.Tests\Tests\MeSettingsTests.cs`:

```csharp
/// <summary>
/// MeSettingsTests — GET and PATCH /me/settings against the Cosmos DB emulator (the etag
/// concurrency of the user document is only real there).
/// </summary>

namespace SafeExchange.Tests
{
    using Azure.Core.Serialization;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;
    using Microsoft.IdentityModel.Tokens;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Model;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;

    [TestFixture]
    public class MeSettingsTests
    {
        private const string TenantId = "00000000-0000-0000-0000-000000000001";

        private const string UserObjectId = "00000000-0000-0000-0000-0000000000d1";

        private const string UserUpn = "me@test.test";

        private DbContextOptions<SafeExchangeDbContext> options = null!;

        private SafeExchangeDbContext dbContext = null!;

        private ITokenHelper tokenHelper = null!;

        private GlobalFilters globalFilters = null!;

        private IServiceProvider previousInstanceServices = null!;

        [OneTimeSetUp]
        public async Task OneTimeSetup()
        {
            var connectionString = new ConfigurationBuilder().AddUserSecrets<MeSettingsTests>().Build().GetConnectionString("CosmosDb");
            this.options = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(connectionString!, $"{nameof(MeSettingsTests)}Database", CosmosTestOptions.UseGateway)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CosmosEventId.SyncNotSupported))
                .Options;
            this.dbContext = new SafeExchangeDbContext(this.options);
            await this.dbContext.Database.EnsureCreatedAsync();

            this.tokenHelper = new TokenHelper(new ConfigurationBuilder().Build(), NullLogger<TokenHelper>.Instance);
            this.globalFilters = new GlobalFilters(
                Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(x => x.CurrentValue == new GloballyAllowedGroupsConfiguration()),
                Mock.Of<IOptionsMonitor<AdminConfiguration>>(x => x.CurrentValue == new AdminConfiguration()),
                this.tokenHelper,
                NullLogger<GlobalFilters>.Instance);
        }

        [OneTimeTearDown]
        public void OneTimeCleanup()
        {
            TestDatabase.EnsureDeletedTolerant(this.dbContext.Database);
            this.dbContext.Dispose();
        }

        [SetUp]
        public void Setup()
        {
            // Production response serializer settings (camelCase, nulls omitted).
            this.previousInstanceServices = TestFactory.FunctionContext.InstanceServices;
            var serializer = new JsonObjectSerializer(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNameCaseInsensitive = true,
            });
            var workerOptions = Options.Create(new WorkerOptions { Serializer = serializer });
            TestFactory.FunctionContext.InstanceServices = Mock.Of<IServiceProvider>(x => x.GetService(typeof(IOptions<WorkerOptions>)) == workerOptions);
        }

        [TearDown]
        public void Cleanup()
        {
            TestFactory.FunctionContext.InstanceServices = this.previousInstanceServices;
            using var cleanup = new SafeExchangeDbContext(this.options);
            cleanup.Users.RemoveRange(cleanup.Users.ToList());
            cleanup.SaveChanges();
        }

        [Test]
        public async Task Get_ReturnsTheDefaults_OfANewUser()
        {
            await this.SeedUserAsync();

            var response = await this.Handler().RunGet(Request("get"), UserPrincipal(), NullLogger.Instance);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(BodyOf(response), Is.EqualTo("""{"status":"ok","subStatus":"","result":{"mcpAllowed":false,"mcpBlockedByAdmin":false,"mcpEffective":false}}"""));
        }

        [Test]
        public async Task Get_OfAUserWithoutAUsersRow_IsNotFound()
        {
            var response = await this.Handler().RunGet(Request("get"), UserPrincipal(), NullLogger.Instance);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }

        [Test]
        public async Task Patch_OptsInAndOut()
        {
            var user = await this.SeedUserAsync();

            var optIn = await this.Handler().RunUpdate(Request("patch", """{"mcpAllowed":true}"""), UserPrincipal(), NullLogger.Instance);
            var storedAfterOptIn = await this.StoredAsync(user.Id);
            var optOut = await this.Handler().RunUpdate(Request("patch", """{"mcpAllowed":false}"""), UserPrincipal(), NullLogger.Instance);
            var storedAfterOptOut = await this.StoredAsync(user.Id);

            Assert.That(optIn.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(BodyOf(optIn), Is.EqualTo("""{"status":"ok","subStatus":"","result":{"mcpAllowed":true,"mcpBlockedByAdmin":false,"mcpEffective":true}}"""));
            Assert.That(storedAfterOptIn.McpAllowed, Is.True);
            Assert.That(optOut.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(BodyOf(optOut), Is.EqualTo("""{"status":"ok","subStatus":"","result":{"mcpAllowed":false,"mcpBlockedByAdmin":false,"mcpEffective":false}}"""));
            Assert.That(storedAfterOptOut.McpAllowed, Is.False);
        }

        [Test]
        public async Task Patch_OptInWhileBlocked_IsForbidden_AndStoresNothing()
        {
            var user = await this.SeedUserAsync(mcpBlockedByAdmin: true);

            var response = await this.Handler().RunUpdate(Request("patch", """{"mcpAllowed":true}"""), UserPrincipal(), NullLogger.Instance);

            AssertBlocked(response);
            Assert.That((await this.StoredAsync(user.Id)).McpAllowed, Is.False);
        }

        [Test]
        public async Task Patch_OptOutWhileBlocked_IsAccepted()
        {
            var user = await this.SeedUserAsync(mcpAllowed: true, mcpBlockedByAdmin: true);

            var response = await this.Handler().RunUpdate(Request("patch", """{"mcpAllowed":false}"""), UserPrincipal(), NullLogger.Instance);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(BodyOf(response), Is.EqualTo("""{"status":"ok","subStatus":"","result":{"mcpAllowed":false,"mcpBlockedByAdmin":true,"mcpEffective":false}}"""));
            Assert.That((await this.StoredAsync(user.Id)).McpAllowed, Is.False);
        }

        [TestCase("")]
        [TestCase("not json")]
        [TestCase("[]")]
        [TestCase("{}")]
        [TestCase("""{"mcpAllowed":null}""")]
        [TestCase("""{"mcpAllowed":"true"}""")]
        [TestCase("""{"mcpAllowed":1}""")]
        public async Task Patch_WithoutABooleanMcpAllowed_IsBadRequest(string body)
        {
            var user = await this.SeedUserAsync();

            var response = await this.Handler().RunUpdate(Request("patch", body), UserPrincipal(), NullLogger.Instance);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            var json = JsonNode.Parse(BodyOf(response))!;
            Assert.That(json["status"]!.GetValue<string>(), Is.EqualTo("error"));
            Assert.That(json["error"]!.GetValue<string>(), Is.EqualTo(SafeExchangeMeSettings.InvalidBodyError));
            Assert.That((await this.StoredAsync(user.Id)).McpAllowed, Is.False);
        }

        [TestCase("get")]
        [TestCase("patch")]
        public async Task Application_IsForbidden(string method)
        {
            var handler = this.Handler();
            var request = Request(method, """{"mcpAllowed":true}""");

            var response = method == "get"
                ? await handler.RunGet(request, AppPrincipal(), NullLogger.Instance)
                : await handler.RunUpdate(request, AppPrincipal(), NullLogger.Instance);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            var json = JsonNode.Parse(BodyOf(response))!;
            Assert.That(json["status"]!.GetValue<string>(), Is.EqualTo("forbidden"));
            Assert.That(json["error"]!.GetValue<string>(), Is.EqualTo(SafeExchangeMeSettings.UsersOnlyError));
        }

        [Test]
        public async Task Patch_ConcurrentUpdate_IsRetried_AndKeepsBothChanges()
        {
            var user = await this.SeedUserAsync();
            var concurrentWriter = new ConcurrentUserWriteInterceptor(this.options, user.Id, u => u.DisplayName = "Renamed concurrently");

            var response = await this.Handler(concurrentWriter).RunUpdate(Request("patch", """{"mcpAllowed":true}"""), UserPrincipal(), NullLogger.Instance);

            var stored = await this.StoredAsync(user.Id);
            Assert.That(concurrentWriter.Writes, Is.EqualTo(1));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(stored.McpAllowed, Is.True);
            Assert.That(stored.DisplayName, Is.EqualTo("Renamed concurrently"));
        }

        [Test]
        public async Task Patch_OptIn_LosesToAConcurrentBlock()
        {
            var user = await this.SeedUserAsync();
            var concurrentWriter = new ConcurrentUserWriteInterceptor(this.options, user.Id, u => u.McpBlockedByAdmin = true);

            var response = await this.Handler(concurrentWriter).RunUpdate(Request("patch", """{"mcpAllowed":true}"""), UserPrincipal(), NullLogger.Instance);

            var stored = await this.StoredAsync(user.Id);
            AssertBlocked(response);
            Assert.That(stored.McpAllowed, Is.False);
            Assert.That(stored.McpBlockedByAdmin, Is.True);
        }

        private static void AssertBlocked(HttpResponseData response)
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            var json = JsonNode.Parse(BodyOf(response))!;
            Assert.That(json["status"]!.GetValue<string>(), Is.EqualTo("forbidden"));
            Assert.That(json["subStatus"]!.GetValue<string>(), Is.EqualTo(McpUserAccessPolicy.BlockedByAdminSubStatus));
            Assert.That(json["error"]!.GetValue<string>(), Is.EqualTo(SafeExchangeMeSettings.EnableWhileBlockedError));
        }

        private SafeExchangeMeSettings Handler(ConcurrentUserWriteInterceptor? concurrentWriter = null)
        {
            var handlerOptions = concurrentWriter is null
                ? this.options
                : new DbContextOptionsBuilder<SafeExchangeDbContext>(this.options).AddInterceptors(concurrentWriter).Options;
            return new SafeExchangeMeSettings(new SafeExchangeDbContext(handlerOptions), this.tokenHelper, this.globalFilters);
        }

        private async Task<User> SeedUserAsync(bool mcpAllowed = false, bool mcpBlockedByAdmin = false)
        {
            await using var seed = new SafeExchangeDbContext(this.options);
            var user = new User("Me", UserObjectId, TenantId, UserUpn, UserUpn)
            {
                McpAllowed = mcpAllowed,
                McpBlockedByAdmin = mcpBlockedByAdmin,
            };
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
            return user;
        }

        private async Task<User> StoredAsync(string id)
        {
            await using var read = new SafeExchangeDbContext(this.options);
            return await read.Users.AsNoTracking().SingleAsync(u => u.Id == id);
        }

        private static ClaimsPrincipal UserPrincipal()
            => new(new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new("upn", UserUpn),
                new("oid", UserObjectId),
                new("tid", TenantId),
            }));

        // App-only (client-credentials) v2 token shape: azp + azpacr and no scope claim.
        private static ClaimsPrincipal AppPrincipal()
            => new(new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new("azp", "00000000-0000-0000-0000-0000000000c1"),
                new("azpacr", "1"),
                new("oid", "00000000-0000-0000-0000-0000000000b1"),
                new("tid", TenantId),
            }));

        private static HttpRequestData Request(string method, string body = "")
        {
            var request = TestFactory.CreateHttpRequestData(method);
            request.SetBodyAsStream(new MemoryStream(Encoding.UTF8.GetBytes(body)));
            return request;
        }

        private static string BodyOf(HttpResponseData response) => ((TestHttpResponseData)response).ReadBodyAsString();
    }
}
```

Notes for the implementer: the real `TokenHelper` classifies `azp` + `azpacr` without a scope claim as an app-only token, which is how the S2S case is produced. The two concurrency tests are meaningful only on Cosmos: without the etag retry the first would overwrite `DisplayName` (EF Cosmos replaces the whole document) and the second would store `McpAllowed = true` despite the block.

- [ ] **Step 3: Build to verify the tests fail**

Run: `dotnet build SafeExchange.slnx --nologo -v q 2>&1 | Select-String " error " | Select-Object -First 3`
Expected: FAIL — `CS0246: The type or namespace name 'SafeExchangeMeSettings' could not be found` in `MeSettingsTests.cs`.

- [ ] **Step 4: Add the shared retry helper**

Create `SafeExchange.Core\Utilities\UserUpdate.cs`:

```csharp
/// <summary>
/// UserUpdate
/// </summary>

namespace SafeExchange.Core.Utilities
{
    using Microsoft.EntityFrameworkCore;
    using SafeExchange.Core.Model;
    using System;
    using System.Threading.Tasks;

    public static class UserUpdate
    {
        public const int MaxAttempts = 4;

        /// <summary>
        /// Applies <paramref name="apply"/> to the tracked <paramref name="user"/> and saves it. The user document is
        /// updated concurrently (e.g. the user's own request rotating its telemetry id under the etag concurrency
        /// token), so on a conflict the latest document is reloaded and <paramref name="apply"/> runs again, for up to
        /// <see cref="MaxAttempts"/> saves; the last conflict propagates. <paramref name="apply"/> returns <c>false</c>
        /// to give up without saving (it must leave the user unchanged then), which this method returns.
        /// </summary>
        public static async Task<bool> SaveWithRetryAsync(SafeExchangeDbContext dbContext, User user, Func<User, bool> apply)
        {
            ArgumentNullException.ThrowIfNull(dbContext);
            ArgumentNullException.ThrowIfNull(user);
            ArgumentNullException.ThrowIfNull(apply);

            for (var attempt = 1; ; attempt++)
            {
                if (!apply(user))
                {
                    return false;
                }

                try
                {
                    await dbContext.SaveChangesAsync();
                    return true;
                }
                catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
                {
                    await dbContext.Entry(user).ReloadAsync();
                }
            }
        }
    }
}
```

- [ ] **Step 5: Add the DTOs**

Create `SafeExchange.Core\Model\Dto\Input\UserSettingsInput.cs`:

```csharp
/// <summary>
/// UserSettingsInput — body of PATCH /me/settings.
/// </summary>

namespace SafeExchange.Core.Model.Dto.Input
{
    public class UserSettingsInput
    {
        /// <summary>Required; null when the body omits it or sets it to null.</summary>
        public bool? McpAllowed { get; set; }
    }
}
```

Create `SafeExchange.Core\Model\Dto\Output\UserSettingsOutput.cs`:

```csharp
/// <summary>
/// UserSettingsOutput — result of GET and PATCH /me/settings.
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    public class UserSettingsOutput
    {
        public bool McpAllowed { get; set; }

        public bool McpBlockedByAdmin { get; set; }

        /// <summary>Whether MCP calls of the user are served: allowed and not blocked.</summary>
        public bool McpEffective { get; set; }

        public static UserSettingsOutput From(User user) => new()
        {
            McpAllowed = user.McpAllowed,
            McpBlockedByAdmin = user.McpBlockedByAdmin,
            McpEffective = user.McpAllowed && !user.McpBlockedByAdmin,
        };
    }
}
```

- [ ] **Step 6: Add the handler**

Create `SafeExchange.Core.Functions\Functions\SafeExchangeMeSettings.cs`:

```csharp
/// <summary>
/// SafeExchangeMeSettings — the caller's own settings (GET and PATCH /me/settings). Users only.
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Utilities;
    using System;
    using System.IO;
    using System.Net;
    using System.Security.Claims;
    using System.Text.Json;
    using System.Threading.Tasks;

    public class SafeExchangeMeSettings
    {
        public const string UsersOnlyError = "Settings are available to users only.";

        public const string EnableWhileBlockedError = "MCP is blocked for this account by an administrator.";

        public const string InvalidBodyError = "Body must be { \"mcpAllowed\": bool }.";

        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        public SafeExchangeMeSettings(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
        }

        /// <summary>GET /me/settings.</summary>
        public async Task<HttpResponseData> RunGet(HttpRequestData request, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            if (!this.tokenHelper.IsUserToken(principal))
            {
                return await ActionResults.ForbiddenAsync(request, UsersOnlyError);
            }

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var user = await UserLookup.FindByTokenAsync(this.dbContext, this.tokenHelper, principal);
                if (user is null)
                {
                    return await UserNotFoundAsync(request);
                }

                return await SettingsAsync(request, user);
            }, nameof(RunGet), log);
        }

        /// <summary>PATCH /me/settings, body <c>{ "mcpAllowed": bool }</c>.</summary>
        public async Task<HttpResponseData> RunUpdate(HttpRequestData request, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            if (!this.tokenHelper.IsUserToken(principal))
            {
                return await ActionResults.ForbiddenAsync(request, UsersOnlyError);
            }

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var body = await new StreamReader(request.Body).ReadToEndAsync();
                UserSettingsInput? input;
                try
                {
                    input = DefaultJsonSerializer.Deserialize<UserSettingsInput>(body);
                }
                catch (JsonException)
                {
                    input = null;
                }

                if (input?.McpAllowed is not bool mcpAllowed)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = InvalidBodyError });
                }

                var user = await UserLookup.FindByTokenAsync(this.dbContext, this.tokenHelper, principal);
                if (user is null)
                {
                    return await UserNotFoundAsync(request);
                }

                // Re-checked on every attempt, so a block saved concurrently wins over enabling.
                var saved = await UserUpdate.SaveWithRetryAsync(this.dbContext, user, u =>
                {
                    if (mcpAllowed && u.McpBlockedByAdmin)
                    {
                        return false;
                    }

                    u.McpAllowed = mcpAllowed;
                    u.ModifiedAt = DateTimeProvider.UtcNow;
                    return true;
                });

                if (!saved)
                {
                    log.LogInformation("User (tid {TelemetryId}) cannot allow MCP: blocked by an administrator.", user.TelemetryId);
                    return await ActionResults.ForbiddenAsync(request, EnableWhileBlockedError, McpUserAccessPolicy.BlockedByAdminSubStatus);
                }

                log.LogInformation("User (tid {TelemetryId}) set McpAllowed to {McpAllowed}.", user.TelemetryId, mcpAllowed);
                return await SettingsAsync(request, user);
            }, nameof(RunUpdate), log);
        }

        private static Task<HttpResponseData> SettingsAsync(HttpRequestData request, User user)
            => ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                new BaseResponseObject<UserSettingsOutput> { Status = "ok", Result = UserSettingsOutput.From(user) });

        private static Task<HttpResponseData> UserNotFoundAsync(HttpRequestData request)
            => ActionResults.CreateResponseAsync(request, HttpStatusCode.NotFound,
                new BaseResponseObject<object> { Status = "not_found", Error = "User not found." });
    }
}
```

(`ActionResults`, `BaseResponseObject<T>`, `DefaultJsonSerializer`, `DateTimeProvider` and `ITokenHelper` live in the enclosing `SafeExchange.Core` namespace. `System.Text.Json.JsonException` covers malformed JSON, non-objects, wrong value kinds and an empty body.)

- [ ] **Step 7: Build and run the tests**

Run:

```powershell
dotnet build SafeExchange.slnx --nologo -v q 2>&1 | Select-String " error "
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~MeSettingsTests"
```

Expected: no build errors; `Passed!  - Failed:     0, Passed:    16, Skipped:     0, Total:    16`.

- [ ] **Step 8: Commit**

```powershell
git add SafeExchange.Core\Utilities\UserUpdate.cs SafeExchange.Core\Model\Dto\Input\UserSettingsInput.cs SafeExchange.Core\Model\Dto\Output\UserSettingsOutput.cs SafeExchange.Core.Functions\Functions\SafeExchangeMeSettings.cs SafeExchange.Tests\Utilities\ConcurrentUserWriteInterceptor.cs SafeExchange.Tests\Tests\MeSettingsTests.cs
git commit -m "feat(users): GET/PATCH me/settings to opt in to MCP" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 4: Admin — MCP fields on list/detail and `PATCH admin/users/{upn}/mcp-blocked`

**Files:**
- Modify: `SafeExchange.Core\Model\Dto\Output\UserOverviewOutput.cs` (`UserOverviewOutput` class, ≈line 3-9)
- Modify: `SafeExchange.Core\Model\Dto\Output\UserDetailOutput.cs` (after `ConsentRequired`, ≈line 47)
- Create: `SafeExchange.Core\Model\Dto\Input\McpBlockedToggleInput.cs`
- Modify: `SafeExchange.Core.Functions\Functions\Admin\SafeExchangeAdminUsers.cs` (header, usings, `RunList` projection ≈line 75-80, `BuildUserDetailAsync` ≈line 176, `RunToggleEnabled` ≈line 220-255, new `RunToggleMcpBlocked`)
- Test: `SafeExchange.Tests\Tests\AdminUsersMcpTests.cs` (create), `SafeExchange.Tests\Tests\AdminUserDetailTests.cs` (modify)

- [ ] **Step 1: Pin the current `enabled` retry behaviour before refactoring it**

There is no test for the existing etag retry loop of `RunToggleEnabled`. Create `SafeExchange.Tests\Tests\AdminUsersMcpTests.cs` with the fixture and only that characterization test:

```csharp
/// <summary>
/// AdminUsersMcpTests — the MCP fields of the admin user list and PATCH admin/users/{upn}/mcp-blocked,
/// against the Cosmos DB emulator (the etag concurrency of the user document is only real there).
/// </summary>

namespace SafeExchange.Tests
{
    using Azure.Core.Serialization;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;
    using Microsoft.IdentityModel.Tokens;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions.Admin;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Text;
    using System.Threading.Tasks;

    [TestFixture]
    public class AdminUsersMcpTests
    {
        private const string AdminObjectId = "00000321-0000-0000-0000-000000000321";

        private DbContextOptions<SafeExchangeDbContext> options = null!;

        private SafeExchangeDbContext dbContext = null!;

        private GlobalFilters globalFilters = null!;

        private IOptionsMonitor<Limits> limits = null!;

        private IServiceProvider previousInstanceServices = null!;

        [OneTimeSetUp]
        public async Task OneTimeSetup()
        {
            var connectionString = new ConfigurationBuilder().AddUserSecrets<AdminUsersMcpTests>().Build().GetConnectionString("CosmosDb");
            this.options = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(connectionString!, $"{nameof(AdminUsersMcpTests)}Database", CosmosTestOptions.UseGateway)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CosmosEventId.SyncNotSupported))
                .Options;
            this.dbContext = new SafeExchangeDbContext(this.options);
            await this.dbContext.Database.EnsureCreatedAsync();

            this.globalFilters = new GlobalFilters(
                Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(x => x.CurrentValue == new GloballyAllowedGroupsConfiguration()),
                Mock.Of<IOptionsMonitor<AdminConfiguration>>(x => x.CurrentValue == new AdminConfiguration { AdminUsers = AdminObjectId }),
                new TestTokenHelper(),
                NullLogger<GlobalFilters>.Instance);
            var limitValues = new Limits { AdminListDefaultPageSize = 25, AdminListMaxPageSize = 100 };
            this.limits = Mock.Of<IOptionsMonitor<Limits>>(x => x.CurrentValue == limitValues);
        }

        [OneTimeTearDown]
        public void OneTimeCleanup()
        {
            TestDatabase.EnsureDeletedTolerant(this.dbContext.Database);
            this.dbContext.Dispose();
        }

        [SetUp]
        public void Setup()
        {
            this.previousInstanceServices = TestFactory.FunctionContext.InstanceServices;
            var workerOptions = Options.Create(new WorkerOptions { Serializer = new JsonObjectSerializer() });
            TestFactory.FunctionContext.InstanceServices = Mock.Of<IServiceProvider>(x => x.GetService(typeof(IOptions<WorkerOptions>)) == workerOptions);
        }

        [TearDown]
        public void Cleanup()
        {
            TestFactory.FunctionContext.InstanceServices = this.previousInstanceServices;
            using var cleanup = new SafeExchangeDbContext(this.options);
            cleanup.Users.RemoveRange(cleanup.Users.ToList());
            cleanup.SaveChanges();
        }

        [Test]
        public async Task RunToggleEnabled_ConcurrentUpdate_IsRetried_AndKeepsBothChanges()
        {
            var user = await this.SeedUserAsync("target@test.test");
            var concurrentWriter = new ConcurrentUserWriteInterceptor(this.options, user.Id, u => u.DisplayName = "Renamed concurrently");

            var response = await this.Handler(concurrentWriter).RunToggleEnabled(Request("patch", """{"enabled":false}"""), user.AadUpn, Admin(), NullLogger.Instance);

            var stored = await this.StoredAsync(user.Id);
            Assert.That(concurrentWriter.Writes, Is.EqualTo(1));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(stored.Enabled, Is.False);
            Assert.That(stored.DisplayName, Is.EqualTo("Renamed concurrently"));
        }

        private SafeExchangeAdminUsers Handler(ConcurrentUserWriteInterceptor? concurrentWriter = null)
        {
            var handlerOptions = concurrentWriter is null
                ? this.options
                : new DbContextOptionsBuilder<SafeExchangeDbContext>(this.options).AddInterceptors(concurrentWriter).Options;
            return new SafeExchangeAdminUsers(new SafeExchangeDbContext(handlerOptions), new TestTokenHelper(), this.globalFilters, this.limits);
        }

        private async Task<User> SeedUserAsync(string upn, bool mcpAllowed = false, bool mcpBlockedByAdmin = false)
        {
            await using var seed = new SafeExchangeDbContext(this.options);
            var user = new User(upn, Guid.NewGuid().ToString(), "bbbbbbbb-0000-0000-0000-000000000001", upn, upn)
            {
                McpAllowed = mcpAllowed,
                McpBlockedByAdmin = mcpBlockedByAdmin,
            };
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
            return user;
        }

        private async Task<User> StoredAsync(string id)
        {
            await using var read = new SafeExchangeDbContext(this.options);
            return await read.Users.AsNoTracking().SingleAsync(u => u.Id == id);
        }

        private static ClaimsPrincipal Admin()
            => new(new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new("upn", "admin@test.test"),
                new("oid", AdminObjectId),
                new("tid", "00000000-0000-0000-0000-000000000001"),
            }));

        private static TestHttpRequestData Request(string method, string body = "")
        {
            var request = TestFactory.CreateHttpRequestData(method);
            request.SetBodyAsStream(new MemoryStream(Encoding.UTF8.GetBytes(body)));
            return request;
        }
    }
}
```

(Same admin identity and `TestTokenHelper` as `AdminUserDetailTests`; the default `JsonObjectSerializer` writes PascalCase, which `TestHttpResponseData.ReadBodyAsJson<T>` reads back.)

- [ ] **Step 2: Run it against the current code**

Run:

```powershell
dotnet build SafeExchange.slnx --nologo -v q 2>&1 | Select-String " error "
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~AdminUsersMcpTests"
```

Expected: `Passed!  - Failed:     0, Passed:     1` (the existing inline loop already retries; this test must keep passing after the refactor in Step 6).

- [ ] **Step 3: Write the failing tests for the new behaviour**

In `SafeExchange.Tests\Tests\AdminUsersMcpTests.cs`, insert these tests directly above `RunToggleEnabled_ConcurrentUpdate_IsRetried_AndKeepsBothChanges`:

```csharp
        [Test]
        public async Task RunList_ReturnsTheMcpFlags()
        {
            await this.SeedUserAsync("allowed@test.test", mcpAllowed: true);
            await this.SeedUserAsync("blocked@test.test", mcpAllowed: true, mcpBlockedByAdmin: true);

            var response = (TestHttpResponseData)await this.Handler().RunList(Request("get"), Admin(), NullLogger.Instance);

            var items = response.ReadBodyAsJson<BaseResponseObject<PaginatedResult<UserOverviewOutput>>>()!.Result!.Items;
            Assert.That(items.Select(i => (i.AadUpn, i.McpAllowed, i.McpBlockedByAdmin)), Is.EqualTo(new[]
            {
                ("allowed@test.test", true, false),
                ("blocked@test.test", true, true),
            }));
        }

        [Test]
        public async Task RunToggleMcpBlocked_BlocksAndUnblocks_KeepingTheUsersChoice()
        {
            var user = await this.SeedUserAsync("target@test.test", mcpAllowed: true);

            var blocked = (TestHttpResponseData)await this.Handler().RunToggleMcpBlocked(Request("patch", """{"blocked":true}"""), user.AadUpn, Admin(), NullLogger.Instance);
            var storedBlocked = await this.StoredAsync(user.Id);
            var unblocked = (TestHttpResponseData)await this.Handler().RunToggleMcpBlocked(Request("patch", """{"blocked":false}"""), user.AadUpn, Admin(), NullLogger.Instance);
            var storedUnblocked = await this.StoredAsync(user.Id);

            Assert.That(blocked.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var blockedResult = blocked.ReadBodyAsJson<BaseResponseObject<UserOverviewOutput>>()!.Result!;
            Assert.That((blockedResult.AadUpn, blockedResult.McpAllowed, blockedResult.McpBlockedByAdmin), Is.EqualTo(("target@test.test", true, true)));
            Assert.That((storedBlocked.McpAllowed, storedBlocked.McpBlockedByAdmin), Is.EqualTo((true, true)));
            Assert.That(unblocked.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(unblocked.ReadBodyAsJson<BaseResponseObject<UserOverviewOutput>>()!.Result!.McpBlockedByAdmin, Is.False);
            Assert.That((storedUnblocked.McpAllowed, storedUnblocked.McpBlockedByAdmin), Is.EqualTo((true, false)));
        }

        [Test]
        public async Task RunToggleMcpBlocked_UnknownUser_IsNotFound()
        {
            var response = (TestHttpResponseData)await this.Handler().RunToggleMcpBlocked(Request("patch", """{"blocked":true}"""), "nobody@test.test", Admin(), NullLogger.Instance);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(response.ReadBodyAsJson<BaseResponseObject<object>>()!.Status, Is.EqualTo("not_found"));
        }

        [TestCase("")]
        [TestCase("not json")]
        [TestCase("{}")]
        [TestCase("""{"blocked":null}""")]
        [TestCase("""{"blocked":"true"}""")]
        public async Task RunToggleMcpBlocked_WithoutABooleanBlocked_IsBadRequest(string body)
        {
            var user = await this.SeedUserAsync("target@test.test");

            var response = (TestHttpResponseData)await this.Handler().RunToggleMcpBlocked(Request("patch", body), user.AadUpn, Admin(), NullLogger.Instance);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            var result = response.ReadBodyAsJson<BaseResponseObject<object>>()!;
            Assert.That(result.Status, Is.EqualTo("error"));
            Assert.That(result.Error, Is.EqualTo("Body must be { \"blocked\": bool }."));
            Assert.That((await this.StoredAsync(user.Id)).McpBlockedByAdmin, Is.False);
        }

        [Test]
        public async Task RunToggleMcpBlocked_NonAdmin_IsRejected()
        {
            var user = await this.SeedUserAsync("target@test.test");

            var response = await this.Handler().RunToggleMcpBlocked(Request("patch", """{"blocked":true}"""), user.AadUpn, NonAdmin(), NullLogger.Instance);

            Assert.That(response.StatusCode, Is.Not.EqualTo(HttpStatusCode.OK));
            Assert.That((await this.StoredAsync(user.Id)).McpBlockedByAdmin, Is.False);
        }

        [Test]
        public async Task RunToggleMcpBlocked_ConcurrentUpdate_IsRetried_AndKeepsBothChanges()
        {
            var user = await this.SeedUserAsync("target@test.test", mcpAllowed: true);
            var concurrentWriter = new ConcurrentUserWriteInterceptor(this.options, user.Id, u => u.DisplayName = "Renamed concurrently");

            var response = await this.Handler(concurrentWriter).RunToggleMcpBlocked(Request("patch", """{"blocked":true}"""), user.AadUpn, Admin(), NullLogger.Instance);

            var stored = await this.StoredAsync(user.Id);
            Assert.That(concurrentWriter.Writes, Is.EqualTo(1));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(stored.McpBlockedByAdmin, Is.True);
            Assert.That(stored.DisplayName, Is.EqualTo("Renamed concurrently"));
        }
```

and directly below the `Admin()` helper add:

```csharp
        private static ClaimsPrincipal NonAdmin()
            => new(new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new("upn", "plain@test.test"),
                new("oid", "00000000-0000-0000-0000-000000000099"),
                new("tid", "00000000-0000-0000-0000-000000000001"),
            }));
```

In `SafeExchange.Tests\Tests\AdminUserDetailTests.cs` insert, directly above `RunDetail_ListsRetiredTelemetryIds_NewestFirst`:

```csharp
        [Test]
        public async Task RunDetail_ReturnsTheMcpFlags()
        {
            // [GIVEN] a user who allowed MCP and was blocked by an admin
            var user = await this.SeedUserAsync("detail-mcp@test.test");
            user.McpAllowed = true;
            user.McpBlockedByAdmin = true;
            await this.dbContext.SaveChangesAsync();

            // [WHEN] admin fetches detail
            var detail = await this.GetDetailAsync(user.AadUpn);

            // [THEN] both flags are returned
            Assert.That(detail.McpAllowed, Is.True);
            Assert.That(detail.McpBlockedByAdmin, Is.True);
        }

```

- [ ] **Step 4: Build to verify the tests fail**

Run: `dotnet build SafeExchange.slnx --nologo -v q 2>&1 | Select-String " error " | Select-Object -First 5`
Expected: FAIL — `CS1061: 'UserOverviewOutput' does not contain a definition for 'McpAllowed'`, `CS1061: 'SafeExchangeAdminUsers' does not contain a definition for 'RunToggleMcpBlocked'` and `CS1061: 'UserDetailOutput' does not contain a definition for 'McpAllowed'`.

- [ ] **Step 5: Extend the DTOs and add the input**

In `SafeExchange.Core\Model\Dto\Output\UserOverviewOutput.cs` replace:

```csharp
    public class UserOverviewOutput
    {
        public string AadUpn { get; set; }
        public string DisplayName { get; set; }
        public string ContactEmail { get; set; }
        public bool Enabled { get; set; }
    }
```

with:

```csharp
    public class UserOverviewOutput
    {
        public string AadUpn { get; set; }
        public string DisplayName { get; set; }
        public string ContactEmail { get; set; }
        public bool Enabled { get; set; }
        public bool McpAllowed { get; set; }
        public bool McpBlockedByAdmin { get; set; }
    }
```

In `SafeExchange.Core\Model\Dto\Output\UserDetailOutput.cs` replace:

```csharp
        /// <summary>Gets or sets a value indicating whether the user is required to consent in AAD.</summary>
        public bool ConsentRequired { get; set; }
```

with:

```csharp
        /// <summary>Gets or sets a value indicating whether the user is required to consent in AAD.</summary>
        public bool ConsentRequired { get; set; }

        /// <summary>The user's own choice to allow MCP for their account.</summary>
        public bool McpAllowed { get; set; }

        /// <summary>An administrator blocked MCP for the user (overrides <see cref="McpAllowed"/>).</summary>
        public bool McpBlockedByAdmin { get; set; }
```

Create `SafeExchange.Core\Model\Dto\Input\McpBlockedToggleInput.cs`:

```csharp
/// <summary>
/// McpBlockedToggleInput — body of PATCH /admin/users/{upn}/mcp-blocked.
/// </summary>

namespace SafeExchange.Core.Model.Dto.Input
{
    public class McpBlockedToggleInput
    {
        /// <summary>Required; null when the body omits it or sets it to null.</summary>
        public bool? Blocked { get; set; }
    }
}
```

- [ ] **Step 6: Update `SafeExchangeAdminUsers`**

In `SafeExchange.Core.Functions\Functions\Admin\SafeExchangeAdminUsers.cs`:

Replace the file header:

```csharp
/// <summary>
/// SafeExchangeAdminUsers — admin-only paginated user listing + enable/disable
/// toggle. Search is case-insensitive substring against AadUpn and DisplayName.
/// </summary>
```

with:

```csharp
/// <summary>
/// SafeExchangeAdminUsers — admin-only paginated user listing, user detail, the enable/disable
/// toggle and the MCP block toggle. Search is case-insensitive substring against AadUpn and DisplayName.
/// </summary>
```

Replace the usings:

```csharp
    using SafeExchange.Core.Model.Dto.Output;
    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;
```

with:

```csharp
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Utilities;
    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Text.Json;
    using System.Threading.Tasks;
```

In `RunList` replace:

```csharp
                        ContactEmail = u.ContactEmail,
                        Enabled = u.Enabled,
                    })
                    .ToListAsync();
```

with:

```csharp
                        ContactEmail = u.ContactEmail,
                        Enabled = u.Enabled,
                        McpAllowed = u.McpAllowed,
                        McpBlockedByAdmin = u.McpBlockedByAdmin,
                    })
                    .ToListAsync();
```

In `BuildUserDetailAsync` replace:

```csharp
                ConsentRequired = user.ConsentRequired,
                CurrentTelemetryId = user.TelemetryId,
```

with:

```csharp
                ConsentRequired = user.ConsentRequired,
                McpAllowed = user.McpAllowed,
                McpBlockedByAdmin = user.McpBlockedByAdmin,
                CurrentTelemetryId = user.TelemetryId,
```

Replace the end of `RunToggleEnabled` through the end of the file — from:

```csharp
                user.Enabled = input.Enabled;
                user.ModifiedAt = DateTimeProvider.UtcNow;

                // The user document may be updated concurrently (e.g. the user's own request
                // rotating its telemetry id under the etag concurrency token). The admin intent
                // is authoritative, so on a conflict reload the latest document, re-apply the
                // enabled flag, and save again.
                const int maxAttempts = 4;
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        await this.dbContext.SaveChangesAsync();
                        break;
                    }
                    catch (DbUpdateConcurrencyException) when (attempt < maxAttempts)
                    {
                        await this.dbContext.Entry(user).ReloadAsync();
                        user.Enabled = input.Enabled;
                        user.ModifiedAt = DateTimeProvider.UtcNow;
                    }
                }

                log.LogInformation("Admin toggled User '{Upn}' Enabled to {Enabled}.", upn, input.Enabled);
                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<UserOverviewOutput>
                    {
                        Status = "ok",
                        Result = new UserOverviewOutput
                        {
                            AadUpn = user.AadUpn, DisplayName = user.DisplayName,
                            ContactEmail = user.ContactEmail, Enabled = user.Enabled,
                        },
                    });
            }, nameof(RunToggleEnabled), log);
        }
    }
}
```

to:

```csharp
                // The admin intent is authoritative: re-applied on top of concurrent updates.
                await UserUpdate.SaveWithRetryAsync(this.dbContext, user, u =>
                {
                    u.Enabled = input.Enabled;
                    u.ModifiedAt = DateTimeProvider.UtcNow;
                    return true;
                });

                log.LogInformation("Admin toggled User '{Upn}' Enabled to {Enabled}.", upn, input.Enabled);
                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<UserOverviewOutput> { Status = "ok", Result = ToOverview(user) });
            }, nameof(RunToggleEnabled), log);
        }

        public async Task<HttpResponseData> RunToggleMcpBlocked(HttpRequestData request, string upn, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetAdminFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var user = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn == upn);
                if (user is null)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = $"User '{upn}' not found." });
                }

                var body = await new StreamReader(request.Body).ReadToEndAsync();
                McpBlockedToggleInput? input;
                try
                {
                    input = DefaultJsonSerializer.Deserialize<McpBlockedToggleInput>(body);
                }
                catch (JsonException)
                {
                    input = null;
                }

                if (input?.Blocked is not bool blocked)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Body must be { \"blocked\": bool }." });
                }

                // The admin intent is authoritative: re-applied on top of concurrent updates.
                await UserUpdate.SaveWithRetryAsync(this.dbContext, user, u =>
                {
                    u.McpBlockedByAdmin = blocked;
                    u.ModifiedAt = DateTimeProvider.UtcNow;
                    return true;
                });

                log.LogInformation("Admin set McpBlockedByAdmin to {Blocked} for user (tid {TelemetryId}).", blocked, user.TelemetryId);
                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<UserOverviewOutput> { Status = "ok", Result = ToOverview(user) });
            }, nameof(RunToggleMcpBlocked), log);
        }

        private static UserOverviewOutput ToOverview(User user) => new()
        {
            AadUpn = user.AadUpn,
            DisplayName = user.DisplayName,
            ContactEmail = user.ContactEmail,
            Enabled = user.Enabled,
            McpAllowed = user.McpAllowed,
            McpBlockedByAdmin = user.McpBlockedByAdmin,
        };
    }
}
```

(The `enabled` toggle keeps its body parsing, 4 attempts and log line; only the loop moves into `UserUpdate.SaveWithRetryAsync` and its response now also carries the two MCP fields. `Microsoft.EntityFrameworkCore` stays imported for `FirstOrDefaultAsync`/`ToListAsync`.)

- [ ] **Step 7: Build and run the admin tests**

Run:

```powershell
dotnet build SafeExchange.slnx --nologo -v q 2>&1 | Select-String " error "
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~AdminUsersMcpTests|FullyQualifiedName~AdminUserDetailTests"
```

Expected: `Passed!  - Failed:     0, Passed:    21, Skipped:     0, Total:    21` (11 in `AdminUsersMcpTests`, 10 in `AdminUserDetailTests`).

- [ ] **Step 8: Commit**

```powershell
git add SafeExchange.Core\Model\Dto\Output\UserOverviewOutput.cs SafeExchange.Core\Model\Dto\Output\UserDetailOutput.cs SafeExchange.Core\Model\Dto\Input\McpBlockedToggleInput.cs SafeExchange.Core.Functions\Functions\Admin\SafeExchangeAdminUsers.cs SafeExchange.Tests\Tests\AdminUsersMcpTests.cs SafeExchange.Tests\Tests\AdminUserDetailTests.cs
git commit -m "feat(admin): show users' MCP settings and let admins block MCP per user" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 5: HTTP triggers and the route guard

**Files:**
- Modify: `SafeExchange.Tests\Contract\FunctionRouteTests.cs` (`ExpectedFunctions`)
- Modify: `SafeExchange.Tests\Mcp\McpChallengeTests.cs` (`OnlyTheMetadataAndBrokerTriggers_SkipAuthentication_ByExactName` cases)
- Create: `SafeExchange.Functions\Functions\SafeMeSettings.cs`
- Modify: `SafeExchange.Functions\Functions\SafeAdminUsers.cs` (after `RunToggleEnabled`)

- [ ] **Step 1: Pin the new public routes (failing)**

In `SafeExchange.Tests\Contract\FunctionRouteTests.cs` replace:

```csharp
            "SafeExchange-Admin-Users-ToggleEnabled|patch|api/{apiVersion}/admin/users/{upn}/enabled",
```

with:

```csharp
            "SafeExchange-Admin-Users-ToggleEnabled|patch|api/{apiVersion}/admin/users/{upn}/enabled",
            "SafeExchange-Admin-Users-ToggleMcpBlocked|patch|api/{apiVersion}/admin/users/{upn}/mcp-blocked",
```

and replace:

```csharp
            "SafeExchange-Mcp|delete,get,post|api/v1/mcp",
```

with:

```csharp
            "SafeExchange-Mcp|delete,get,post|api/v1/mcp",
            "SafeExchange-Me-Settings-Get|get|api/{apiVersion}/me/settings",
            "SafeExchange-Me-Settings-Update|patch|api/{apiVersion}/me/settings",
```

In `SafeExchange.Tests\Mcp\McpChallengeTests.cs` replace:

```csharp
        [TestCase("SafeExchange-SecretMeta", "httpTrigger", true)]
```

with:

```csharp
        [TestCase("SafeExchange-SecretMeta", "httpTrigger", true)]
        [TestCase("SafeExchange-Me-Settings-Get", "httpTrigger", true)]
        [TestCase("SafeExchange-Me-Settings-Update", "httpTrigger", true)]
```

(The settings endpoints must never be anonymous; `IsAuthenticatedHttpTrigger` exempts only `McpRoutes.AnonymousFunctionNames`, so these two cases pass already and guard against a future regression.)

- [ ] **Step 2: Run the route tests to verify the guard fails**

Run:

```powershell
dotnet build SafeExchange.slnx --nologo -v q 2>&1 | Select-String " error "
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~FunctionRouteTests|FullyQualifiedName~McpChallengeTests"
```

Expected: `Failed!  - Failed:     1, Passed:    30` — `EveryHttpTrigger_IsServedAtItsPinnedPublicPath` reports the three expected entries as missing.

- [ ] **Step 3: Add the triggers**

Create `SafeExchange.Functions\Functions\SafeMeSettings.cs`:

```csharp
/// <summary>
/// SafeMeSettings — HTTP trigger entry points for the caller's own settings (GET and PATCH /me/settings).
/// </summary>

namespace SafeExchange.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using System;
    using System.Threading.Tasks;

    public class SafeMeSettings
    {
        private const string Version = "api/{apiVersion}";

        private readonly SafeExchangeMeSettings handler;

        private readonly ILogger log;

        public SafeMeSettings(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, ILogger<SafeMeSettings> log)
        {
            this.handler = new SafeExchangeMeSettings(dbContext, tokenHelper, globalFilters);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-Me-Settings-Get")]
        public async Task<HttpResponseData> RunGet(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/me/settings")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunGet(request, principal, this.log);
        }

        [Function("SafeExchange-Me-Settings-Update")]
        public async Task<HttpResponseData> RunUpdate(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = $"{Version}/me/settings")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunUpdate(request, principal, this.log);
        }
    }
}
```

(`AuthorizationLevel.Anonymous` is the convention of every trigger here: the bearer token is validated by `DefaultAuthenticationMiddleware`/`TokenFilterMiddleware`, which create the caller's Users row before the handler runs. `GetPrincipal()` is in `SafeExchange.Core`.)

In `SafeExchange.Functions\Functions\SafeAdminUsers.cs` replace:

```csharp
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunToggleEnabled(request, upn, principal, this.log);
        }
```

with:

```csharp
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunToggleEnabled(request, upn, principal, this.log);
        }

        [Function("SafeExchange-Admin-Users-ToggleMcpBlocked")]
        public async Task<HttpResponseData> RunToggleMcpBlocked(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = $"{Version}/admin/users/{{upn}}/mcp-blocked")]
            HttpRequestData request,
            string upn)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunToggleMcpBlocked(request, upn, principal, this.log);
        }
```

- [ ] **Step 4: Run the route tests**

Run:

```powershell
dotnet build SafeExchange.slnx --nologo -v q 2>&1 | Select-String " error "
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~FunctionRouteTests|FullyQualifiedName~McpChallengeTests"
```

Expected: `Passed!  - Failed:     0, Passed:    31, Skipped:     0, Total:    31`.

- [ ] **Step 5: Commit**

```powershell
git add SafeExchange.Tests\Contract\FunctionRouteTests.cs SafeExchange.Tests\Mcp\McpChallengeTests.cs SafeExchange.Functions\Functions\SafeMeSettings.cs SafeExchange.Functions\Functions\SafeAdminUsers.cs
git commit -m "feat(api): expose me/settings and admin/users/{upn}/mcp-blocked" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 6: Documentation and full verification

**Files:**
- Modify: `docs\api-endpoints.md` (after the "Search and Lookup" table ≈line 118; Admin table ≈line 155-162; MCP intro ≈line 171)
- Modify: `docs\mcp.md` ("Authentication" ≈line 52; "Security notes" ≈line 327; "Client configuration" ≈line 331; "Staging validation" step 3 ≈line 392)
- Modify: `README.md` ("MCP for AI Agents" ≈line 66)

- [ ] **Step 1: `docs\api-endpoints.md`**

Replace:

```markdown
| GET | `/{v}/telemetry/config` | Client telemetry configuration |
```

with:

```markdown
| GET | `/{v}/telemetry/config` | Client telemetry configuration |

## Personal Settings

Users only: an application (S2S) caller gets `403` (`Settings are available to users only.`).

| Method | Path | Description |
|--------|------|-------------|
| GET | `/{v}/me/settings` | The caller's settings: `{ "mcpAllowed": bool, "mcpBlockedByAdmin": bool, "mcpEffective": bool }` (`mcpEffective` = allowed and not blocked) |
| PATCH | `/{v}/me/settings` | Change the caller's settings. Body `{ "mcpAllowed": bool }`; a missing or non-boolean `mcpAllowed` answers `400`. Turning MCP on while an administrator blocked it answers `403` with `subStatus = "mcp_blocked_by_admin"` and stores nothing; turning it off is always accepted. Returns the same body as `GET` |
```

Replace:

```markdown
| GET | `/{v}/admin/users` | List users |
| GET | `/{v}/admin/users/{upn}` | User detail |
| GET | `/{v}/admin/users/by-telemetry-id/{telemetryId}` | Look up a user by telemetry id |
| PATCH | `/{v}/admin/users/{upn}/enabled` | Enable or disable a user |
```

with:

```markdown
| GET | `/{v}/admin/users` | List users (each with `mcpAllowed` and `mcpBlockedByAdmin`) |
| GET | `/{v}/admin/users/{upn}` | User detail (including `mcpAllowed` and `mcpBlockedByAdmin`) |
| GET | `/{v}/admin/users/by-telemetry-id/{telemetryId}` | Look up a user by telemetry id |
| PATCH | `/{v}/admin/users/{upn}/enabled` | Enable or disable a user |
| PATCH | `/{v}/admin/users/{upn}/mcp-blocked` | Block or unblock MCP for a user. Body `{ "blocked": bool }` (otherwise `400`); `404` for an unknown user; returns the updated user. A block overrides the user's own `mcpAllowed` |
```

Replace:

```markdown
| POST | `/{v}/admops/{operationName}` | Run an admin operation (`add_kek_version`, `run_dbmigration`) |
```

with:

```markdown
| POST | `/{v}/admops/{operationName}` | Run an admin operation (`add_kek_version`, `run_dbmigration` with body `{ "migrationId": "<id>" }`) |
```

In the `## MCP` intro paragraph replace the sentence:

```markdown
See [mcp.md](mcp.md) for setup, authentication, the OAuth broker, the tool list and the file transfer flows.
```

with:

```markdown
See [mcp.md](mcp.md) for setup, authentication, the OAuth broker, the tool list and the file transfer flows. A user can use MCP only after opting in (`PATCH /{v}/me/settings` with `{ "mcpAllowed": true }`, see [Personal Settings](#personal-settings)); otherwise `/api/v1/mcp` answers `403` with `subStatus = "mcp_not_allowed"`, or `"mcp_blocked_by_admin"` when an administrator blocked MCP for the user. Applications are not affected.
```

- [ ] **Step 2: `docs\mcp.md`**

Replace the first paragraph of `## Authentication`:

```markdown
Every call needs `Authorization: Bearer <token>`, validated exactly like REST (issuer, audience, signature, expiry). The caller is then resolved once per MCP request, before dispatch; a denial (not in the allowed groups, an unregistered or disabled application) returns the same status and body as REST (for example `403`).
```

with:

````markdown
Every call needs `Authorization: Bearer <token>`, validated exactly like REST (issuer, audience, signature, expiry). The caller is then resolved once per MCP request, before dispatch; a denial (not in the allowed groups, an unregistered or disabled application, a user who has not opted in to MCP) returns the same status and body shape as REST (for example `403`).

### User opt-in

A signed-in user can use MCP only after turning it on for their own account. The default is off, for new and existing users.

- **Turn it on or off:** the **Allow MCP** switch in the web app's settings, or `PATCH /api/v2/me/settings` with `{ "mcpAllowed": true }` (or `false`); `GET /api/v2/me/settings` shows the current state. See [api-endpoints.md](api-endpoints.md#personal-settings).
- **Administrators can block it:** `PATCH /api/v2/admin/users/{upn}/mcp-blocked` with `{ "blocked": true }`. A block overrides the user's choice; while blocked, the user cannot turn MCP on (they can still turn it off). Administrators cannot turn MCP on for a user.
- **No MCP tool reads or changes the setting**, so an agent cannot grant itself access through MCP.
- **It applies to users only.** S2S applications are unaffected; disabling the application remains their kill switch.

The setting is read on every MCP message, so a change applies to the next call. Refusals (logged with the user's telemetry id only):

| State | Response |
|---|---|
| not turned on | `403`, `{ "status": "forbidden", "subStatus": "mcp_not_allowed", "error": "MCP access is not enabled for this account. Turn on 'Allow MCP' in SafeExchange settings." }` |
| blocked by an administrator (whatever the user chose) | `403`, `{ "status": "forbidden", "subStatus": "mcp_blocked_by_admin", "error": "MCP access is blocked for this account by an administrator." }` |

Sign-in through the OAuth broker still completes (the broker does not inspect tokens); the first MCP message is refused with the reason.

**Known limitation:** the setting controls use of SafeExchange **through MCP** only. A token obtained through the broker is an ordinary Entra token for the SafeExchange API (in production the broker uses the web app's registration), so software holding it can still call REST, including `PATCH /me/settings`.

**Existing users:** after deploying, run migration `00013` once (`POST /api/v2/admops/run_dbmigration` with `{ "migrationId": "00013" }`, as an administrator). It writes `McpAllowed = false` and `McpBlockedByAdmin = false` onto user documents that predate the setting; it is idempotent. Until it runs, such users already read as off.
````

In `## Security notes` replace:

```markdown
- **Caller resolution costs one set of database lookups per MCP message**, the same as one REST call; that is what lets the kill switch apply on the next call.
```

with:

```markdown
- **Caller resolution costs one set of database lookups per MCP message**, the same as one REST call, plus one read of the Users row for a user caller (the opt-in); that is what lets the kill switch and the opt-in apply on the next call.
```

Replace:

```markdown
## Client configuration

### Copilot CLI
```

with:

```markdown
## Client configuration

A user must first turn on MCP for their account (see [User opt-in](#user-opt-in)); until then every call answers `403` with `subStatus = "mcp_not_allowed"`.

### Copilot CLI
```

In `## Staging validation` replace step 3:

```markdown
3. **MCP as a user from Copilot CLI** via OAuth discovery through the broker (after the [Entra setup](#entra-setup-once-per-environment)): `/mcp auth safeexchange` opens the browser on the SafeExchange consent page; **Continue** signs in with Entra without an Entra consent prompt and the next tool call succeeds (**Cancel** makes the client report `access_denied`); repeat after the access token expires to see the refresh. If a client cannot complete OAuth, use the `az account get-access-token` header fallback and record the outcome per client.
```

with:

```markdown
3. **MCP as a user from Copilot CLI** via OAuth discovery through the broker (after the [Entra setup](#entra-setup-once-per-environment)): with MCP not yet allowed for the account, sign-in completes and the first tool call returns `403` `mcp_not_allowed`; allow it (`PATCH /api/v2/me/settings` with `{ "mcpAllowed": true }`) and the next call succeeds; block the user as an administrator (`PATCH /api/v2/admin/users/{upn}/mcp-blocked` with `{ "blocked": true }`) and the next call returns `403` `mcp_blocked_by_admin`; unblock again. `/mcp auth safeexchange` opens the browser on the SafeExchange consent page; **Continue** signs in with Entra without an Entra consent prompt and the next tool call succeeds (**Cancel** makes the client report `access_denied`); repeat after the access token expires to see the refresh. If a client cannot complete OAuth, use the `az account get-access-token` header fallback and record the outcome per client.
```

- [ ] **Step 3: `README.md`**

In `## MCP for AI Agents` replace:

```markdown
It is off by default (`Features.McpEndpoint`). See [docs/mcp.md](docs/mcp.md) for setup, authentication (signed-in users and S2S agent apps), the tool list and client configuration.
```

with:

```markdown
It is off by default (`Features.McpEndpoint`), and each user must opt in before their account can be used through MCP (**Allow MCP** in the web app's settings, or `PATCH /api/v2/me/settings`); administrators can block it per user. See [docs/mcp.md](docs/mcp.md) for setup, authentication (signed-in users and S2S agent apps), the tool list and client configuration.
```

- [ ] **Step 4: Full build — no new warnings**

Run:

```powershell
dotnet build SafeExchange.slnx --nologo -v q --no-incremental 2>&1 | Select-String ": warning " | ForEach-Object { $_.Line -replace '\(\d+,\d+\)', '' } | Sort-Object -Unique | Set-Content artifacts\warnings-after.txt
Compare-Object (Get-Content artifacts\warnings-baseline.txt) (Get-Content artifacts\warnings-after.txt) | Where-Object SideIndicator -eq '=>'
```

Expected: the build succeeds and the `Compare-Object` prints nothing (no warning that is not in the baseline; the new files are warning-free and the touched files only shifted lines). If anything prints, fix it in the file it names and repeat.

- [ ] **Step 5: Full test suite**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build`
Expected: `Passed!  - Failed:     0, Passed:  1510, Skipped:     0, Total:  1510` (1462 + 48 new: 9 backfill, 9 opt-in, 16 settings, 11 admin, 1 detail, 2 challenge cases).

- [ ] **Step 6: Commit**

```powershell
git add docs\api-endpoints.md docs\mcp.md README.md
git commit -m "docs(mcp): document the per-user MCP opt-in, admin block and migration 00013" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## After the last task: clean up

- [ ] **Stop the emulators, restore the user secret, remove the warning files**

```powershell
docker rm -f saex-test-cosmos saex-oauth-azurite
dotnet user-secrets set "ConnectionStrings:CosmosDb" "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==" --project SafeExchange.Tests
Remove-Item artifacts\warnings-baseline.txt, artifacts\warnings-after.txt
git status --short
```

Expected: both container names echoed; `Successfully saved ConnectionStrings:CosmosDb to the secret store.`; `git status --short` prints nothing (six new commits on `feat/mcp-user-opt-in`, one per task; working tree clean).

## Rollout reminder (not part of the PR)

Deploy to staging, run migration `00013` (`POST /api/v2/admops/run_dbmigration`, `{ "migrationId": "00013" }`), walk through `docs\mcp.md` "Staging validation" step 3, then production. From the production deploy on, MCP is refused for every user until they opt in (until the frontend ships, with `PATCH /api/v2/me/settings`).

---

## Self-review (spec → tasks)

| Spec requirement | Task |
|---|---|
| `McpAllowed`, `McpBlockedByAdmin` on `User`, default `false`, new users `false` | 1 |
| Existing documents get both `false` via the existing backfill mechanism, idempotent | 1 (migration `00013`, emulator tests) |
| Enforcement in `McpHandler` after caller resolution, user callers only | 2 |
| `403 forbidden / mcp_not_allowed` and `403 forbidden / mcp_blocked_by_admin` (block wins regardless of `McpAllowed`), `BaseResponseObject` shape, exact error strings | 2 (`McpUserAccessPolicy` constants, `CallerDenial.ToResponseAsync`) |
| Takes effect on the next message, no caching | 2 (`ChangedSetting_AppliesToTheNextMessage`) |
| Denials logged at Information with telemetry id only | 2 (`Refusal_IsLoggedWithTheTelemetryId_NotTheUpn`) |
| S2S unaffected; REST unaffected | 2 (`Application_IsNotSubjectToTheUserSetting`, `Rest_IsNotSubjectToTheUserSetting`) |
| OAuth broker unchanged | no change (broker files untouched; documented in Task 6) |
| No MCP tool reads/changes the settings | no tool added (16 tools unchanged, `ServiceRegistrationTests` still counts 16); documented in Task 6 |
| `GET /me/settings` body, S2S `403 Settings are available to users only.` | 3, 5 |
| `PATCH /me/settings`: required JSON boolean else `400 status=error`; enable while blocked `403 mcp_blocked_by_admin` + nothing stored; disable always accepted; same body as GET | 3 |
| Admin list/detail return `mcpAllowed`, `mcpBlockedByAdmin` | 4 |
| `PATCH admin/users/{upn}/mcp-blocked` `{ "blocked": bool }`: admin filter, `404`, `400`, returns updated user | 4, 5 |
| Concurrency: etag retry on both write paths; `/me/settings` re-checks the block every attempt | 3, 4 (`UserUpdate`, interceptor tests) |
| Route guard includes the new routes | 5 |
| Docs `docs/mcp.md`, `docs/api-endpoints.md` (+ README) | 6 |
| Zero new warnings; full suite green | 6 |

