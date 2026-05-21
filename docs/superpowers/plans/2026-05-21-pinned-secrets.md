# Pinned Secrets Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a per-user "pinned secrets" feature so the web UI can show favourite secrets on the main page. Mirrors the existing PinnedGroups pattern.

**Architecture:** New Cosmos container `PinnedSecrets` keyed on `{ UserId, SecretName }`. Four endpoints: `PUT/GET/DELETE /v2/pinnedsecrets/{secretId}` and `GET /v2/pinnedsecrets-list`. Configurable cap (default 5). Pin requires `Read` permission at PUT time; list always returns DTOs (with `exists` / `canRead` flags) so the UI can show deleted-secret and access-lost states.

**Tech Stack:** .NET 8, Azure Functions Worker v4, Entity Framework Core (Cosmos provider), NUnit 3 + Moq for tests.

**Reference spec:** `docs/superpowers/specs/2026-05-21-pinned-secrets-design.md`
**Reference pattern:** `SafeExchange.Core/Functions/SafeExchangePinnedGroups.cs`, `SafeExchange.Tests/Tests/PinnedGroupsTests.cs`
**Branch:** `features/pinned-secs` (already created)

---

## File Structure

### New files

| Path | Responsibility |
|---|---|
| `SafeExchange.Core/Configuration/PinnedSecretsConfiguration.cs` | Settings POCO — `MaxPinnedSecretsPerUser` (default 5) |
| `SafeExchange.Core/Model/PinnedSecret.cs` | Cosmos entity — `{ PartitionKey, UserId, SecretName, CreatedAt }` |
| `SafeExchange.Core/Model/Dto/Output/PinnedSecretOutput.cs` | Response DTO returned by all four endpoints |
| `SafeExchange.Core/Functions/SafeExchangePinnedSecrets.cs` | Handler — PUT/GET/DELETE on `{secretId}` |
| `SafeExchange.Core/Functions/SafeExchangePinnedSecretsList.cs` | Handler — GET list |
| `SafeExchange.Functions/Functions/SafePinnedSecrets.cs` | Azure Function HTTP triggers (route registration only) |
| `SafeExchange.Tests/Tests/PinnedSecretsTests.cs` | NUnit endpoint tests, modeled on `PinnedGroupsTests.cs` |

### Modified files

| Path | Change |
|---|---|
| `SafeExchange.Core/DatabaseContext/SafeExchangeDbContext.cs` | `+ DbSet<PinnedSecret> PinnedSecrets;` and EF model config |
| `SafeExchange.Core/SafeExchangeStartup.cs` | `+ services.Configure<PinnedSecretsConfiguration>(...)` |
| `deployment/current/arm/services-template.arm.json` | Add `PinnedSecrets` Cosmos container |
| `docs/api-endpoints.md` | Add Pinned Secrets section |
| `docs/data-model.md` | Add `PinnedSecret` entity row |

---

## Common conventions used throughout

- **C# style:** `this.` for instance members, `var` when type is obvious, PascalCase for properties / methods, camelCase for fields and locals, no field prefixes, one type per file, file-scoped namespaces are NOT used in this repo — use block namespaces (`namespace Foo { ... }`) to match surrounding files.
- **Test isolation:** Each test class uses a real Cosmos test database via emulator connection string. The `[TearDown]` empties tables; `[OneTimeTearDown]` drops the database. Follow the exact pattern in `PinnedGroupsTests.cs`.
- **Subject identifiers:** Tests use the `InvocationContextUserIdKey` context-items key to inject the caller's `UserId`, then a `CaseSensitiveClaimsIdentity` for the principal. See `PinnedGroupsTests.CreatePinnedGroupRegistrationRequest` for the exact pattern.
- **DateTime control:** `DateTimeProvider.SpecifiedDateTime` + `DateTimeProvider.UseSpecifiedDateTime = true` lets tests pin time. Bump `SpecifiedDateTime` between pin creations to make `CreatedAt DESC` ordering deterministic.
- **Run a single test:** `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~PinnedSecretsTests.TestName"`
- **Run all PinnedSecrets tests:** `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~PinnedSecretsTests"`
- **Build:** `dotnet build SafeExchange.sln`
- **Cosmos emulator must be running** for tests to pass (connection string is in user secrets — same as existing tests).
- **Commit cadence:** Every task ends with one commit. Use Conventional Commits prefixes (`feat:`, `test:`, `docs:`, `chore:`).

---

## Task 1: Create configuration class

**Files:**
- Create: `SafeExchange.Core/Configuration/PinnedSecretsConfiguration.cs`

- [ ] **Step 1: Create the configuration class**

```csharp
/// <summary>
/// PinnedSecretsConfiguration
/// </summary>

namespace SafeExchange.Core.Configuration
{
    public class PinnedSecretsConfiguration
    {
        public int MaxPinnedSecretsPerUser { get; set; } = 5;

        public PinnedSecretsConfiguration Clone() => new()
        {
            MaxPinnedSecretsPerUser = this.MaxPinnedSecretsPerUser
        };

        public override bool Equals(object obj)
        {
            if (obj is not PinnedSecretsConfiguration other)
            {
                return false;
            }

            return this.MaxPinnedSecretsPerUser.Equals(other.MaxPinnedSecretsPerUser);
        }

        public override int GetHashCode() => this.MaxPinnedSecretsPerUser.GetHashCode();
    }
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build SafeExchange.sln`
Expected: build succeeds with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add SafeExchange.Core/Configuration/PinnedSecretsConfiguration.cs
git commit -m "feat(pinned-secrets): add PinnedSecretsConfiguration"
```

---

## Task 2: Create PinnedSecret entity

**Files:**
- Create: `SafeExchange.Core/Model/PinnedSecret.cs`

- [ ] **Step 1: Create the entity**

```csharp
/// <summary>
/// PinnedSecret
/// </summary>

namespace SafeExchange.Core.Model
{
    using System;

    public class PinnedSecret
    {
        public const string DefaultPartitionKey = "PSEC";

        public PinnedSecret() { }

        public PinnedSecret(string userId, string secretName)
        {
            this.PartitionKey = PinnedSecret.DefaultPartitionKey;
            this.UserId = userId ?? throw new ArgumentNullException(nameof(userId));
            this.SecretName = secretName ?? throw new ArgumentNullException(nameof(secretName));
            this.CreatedAt = DateTimeProvider.UtcNow;
        }

        public string PartitionKey { get; set; }

        public string UserId { get; set; }

        public string SecretName { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build SafeExchange.sln`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add SafeExchange.Core/Model/PinnedSecret.cs
git commit -m "feat(pinned-secrets): add PinnedSecret entity"
```

---

## Task 3: Register PinnedSecret in DbContext

**Files:**
- Modify: `SafeExchange.Core/DatabaseContext/SafeExchangeDbContext.cs`

- [ ] **Step 1: Add the DbSet**

After `public DbSet<PinnedGroup> PinnedGroups { get; set; }` add a sibling line:

```csharp
public DbSet<PinnedSecret> PinnedSecrets { get; set; }
```

- [ ] **Step 2: Add the EF model configuration**

After the existing `PinnedGroup` configuration block in `OnModelCreating`:

```csharp
modelBuilder.Entity<PinnedGroup>()
    .HasKey(pg => new { pg.UserId, pg.GroupItemId });
```

…add:

```csharp
modelBuilder.Entity<PinnedSecret>()
    .ToContainer("PinnedSecrets")
    .HasNoDiscriminator()
    .HasPartitionKey(ps => ps.PartitionKey);

modelBuilder.Entity<PinnedSecret>()
    .HasKey(ps => new { ps.UserId, ps.SecretName });
```

- [ ] **Step 3: Verify it compiles**

Run: `dotnet build SafeExchange.sln`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add SafeExchange.Core/DatabaseContext/SafeExchangeDbContext.cs
git commit -m "feat(pinned-secrets): register PinnedSecrets DbSet + EF model"
```

---

## Task 4: Create PinnedSecretOutput DTO

**Files:**
- Create: `SafeExchange.Core/Model/Dto/Output/PinnedSecretOutput.cs`

- [ ] **Step 1: Create the DTO**

```csharp
/// <summary>
/// PinnedSecretOutput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System.Collections.Generic;

    public class PinnedSecretOutput
    {
        public string SecretName { get; set; }

        public bool Exists { get; set; }

        public bool CanRead { get; set; }

        public bool CanWrite { get; set; }

        public bool CanGrantAccess { get; set; }

        public bool CanRevokeAccess { get; set; }

        public List<string> Tags { get; set; } = new List<string>();
    }
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build SafeExchange.sln`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add SafeExchange.Core/Model/Dto/Output/PinnedSecretOutput.cs
git commit -m "feat(pinned-secrets): add PinnedSecretOutput DTO"
```

---

## Task 5: Test fixture scaffolding

Builds the empty `PinnedSecretsTests` class with all shared setup so subsequent tests can be added one at a time. Mirrors `PinnedGroupsTests.cs` exactly. No behavior yet — the file compiles but contains zero `[Test]` methods.

**Files:**
- Create: `SafeExchange.Tests/Tests/PinnedSecretsTests.cs`

- [ ] **Step 1: Create the scaffolding**

```csharp
/// <summary>
/// PinnedSecretsTests
/// </summary>

namespace SafeExchange.Tests
{
    using Azure.Core.Serialization;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Middleware;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    [TestFixture]
    public class PinnedSecretsTests
    {
        private ILogger logger;

        private IConfiguration testConfiguration;

        private SafeExchangeDbContext dbContext;

        private ITokenHelper tokenHelper;

        private GlobalFilters globalFilters;

        private IPermissionsManager permissionsManager;

        private CaseSensitiveClaimsIdentity firstIdentity;
        private CaseSensitiveClaimsIdentity secondIdentity;

        private SafeExchangePinnedSecrets pinnedSecrets;
        private SafeExchangePinnedSecretsList pinnedSecretsList;

        private DbContextOptions<SafeExchangeDbContext> dbContextOptions;

        private PinnedSecretsConfiguration pinnedSecretsConfig;

        [OneTimeSetUp]
        public async Task OneTimeSetup()
        {
            var builder = new ConfigurationBuilder().AddUserSecrets<PinnedSecretsTests>();
            var secretConfiguration = builder.Build();

            this.logger = TestFactory.CreateLogger();

            var configurationValues = new Dictionary<string, string>
            {
                {"Features:UseNotifications", "False"},
                {"Features:UseGroupsAuthorization", "False"},
            };

            this.testConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(configurationValues)
                .Build();

            this.dbContextOptions = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseCosmos(secretConfiguration.GetConnectionString("CosmosDb"), databaseName: $"{nameof(PinnedSecretsTests)}Database", CosmosTestOptions.UseGateway)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CosmosEventId.SyncNotSupported))
                .Options;

            this.dbContext = new SafeExchangeDbContext(this.dbContextOptions);
            await this.dbContext.Database.EnsureCreatedAsync();

            this.tokenHelper = new TestTokenHelper();

            GloballyAllowedGroupsConfiguration gagc = new GloballyAllowedGroupsConfiguration();
            var groupsConfiguration = Mock.Of<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>(x => x.CurrentValue == gagc);

            AdminConfiguration ac = new AdminConfiguration();
            var adminConfiguration = Mock.Of<IOptionsMonitor<AdminConfiguration>>(x => x.CurrentValue == ac);

            this.globalFilters = new GlobalFilters(groupsConfiguration, adminConfiguration, this.tokenHelper, TestFactory.CreateLogger<GlobalFilters>());

            // Real PermissionsManager backed by the test DbContext. The handler asks it
            // "does subject S have Read on secret X" — the same code path used in prod.
            this.permissionsManager = new PermissionsManager(
                Mock.Of<IOptionsMonitor<Features>>(x => x.CurrentValue == new Features()),
                new TestGraphDataProvider(),
                this.dbContext,
                TestFactory.CreateLogger<PermissionsManager>());

            this.pinnedSecretsConfig = new PinnedSecretsConfiguration { MaxPinnedSecretsPerUser = 5 };

            this.firstIdentity = new CaseSensitiveClaimsIdentity(new List<Claim>()
                {
                    new Claim("upn", "first@test.test"),
                    new Claim("displayname", "First User"),
                    new Claim("oid", "00000000-0000-0000-0000-000000000001"),
                    new Claim("tid", "00000000-0000-0000-0000-000000000001"),
                }.AsEnumerable());

            this.secondIdentity = new CaseSensitiveClaimsIdentity(new List<Claim>()
                {
                    new Claim("upn", "second@test.test"),
                    new Claim("displayname", "Second User"),
                    new Claim("oid", "00000000-0000-0000-0000-000000000002"),
                    new Claim("tid", "00000000-0000-0000-0000-000000000001"),
                }.AsEnumerable());

            DateTimeProvider.SpecifiedDateTime = DateTime.UtcNow;
            DateTimeProvider.UseSpecifiedDateTime = true;

            var workerOptions = Options.Create(new WorkerOptions() { Serializer = new JsonObjectSerializer() });
            var serviceProviderMock = new Mock<IServiceProvider>();
            serviceProviderMock
                .Setup(x => x.GetService(typeof(IOptions<WorkerOptions>)))
                .Returns(workerOptions);
            TestFactory.FunctionContext.InstanceServices = serviceProviderMock.Object;
        }

        [OneTimeTearDown]
        public void OneTimeCleanup()
        {
            DateTimeProvider.UseSpecifiedDateTime = false;

            this.dbContext.Database.EnsureDeleted();
            this.dbContext.Dispose();
        }

        [SetUp]
        public void Setup()
        {
            DateTimeProvider.SpecifiedDateTime = DateTime.UtcNow;

            this.pinnedSecrets = new SafeExchangePinnedSecrets(
                this.dbContext, this.tokenHelper, this.globalFilters,
                this.permissionsManager,
                Options.Create(this.pinnedSecretsConfig));

            this.pinnedSecretsList = new SafeExchangePinnedSecretsList(
                this.dbContext, this.tokenHelper, this.globalFilters);
        }

        [TearDown]
        public void Cleanup()
        {
            this.dbContext.ChangeTracker.Clear();

            this.dbContext.Users.RemoveRange(this.dbContext.Users.ToList());
            this.dbContext.Objects.RemoveRange(this.dbContext.Objects.ToList());
            this.dbContext.Permissions.RemoveRange(this.dbContext.Permissions.ToList());
            this.dbContext.PinnedSecrets.RemoveRange(this.dbContext.PinnedSecrets.ToList());
            this.dbContext.SaveChanges();
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private async Task SeedSecretAsync(string secretName, string ownerUserId, params string[] tags)
        {
            var metadata = new ObjectMetadata(
                secretName,
                new SafeExchange.Core.Model.Dto.Input.MetadataCreationInput
                {
                    ExpirationSettings = new SafeExchange.Core.Model.Dto.Input.ExpirationMetadataInput
                    {
                        ScheduleExpiration = false,
                        ExpireAt = DateTime.UtcNow.AddYears(1),
                        ExpireOnIdleTime = false,
                        IdleTimeToExpire = TimeSpan.FromDays(30),
                    },
                    Tags = tags?.ToList() ?? new List<string>(),
                },
                $"User {ownerUserId}");

            this.dbContext.Objects.Add(metadata);

            this.dbContext.Permissions.Add(new SubjectPermissions
            {
                SecretName = secretName,
                SubjectType = SubjectType.User,
                SubjectId = ownerUserId,
                SubjectName = ownerUserId,
                CanRead = true,
                CanWrite = true,
                CanGrantAccess = true,
                CanRevokeAccess = true,
                PartitionKey = SubjectPermissions.DefaultPartitionKey,
            });

            await this.dbContext.SaveChangesAsync();
        }

        private async Task GrantReadAsync(string secretName, string userId)
        {
            this.dbContext.Permissions.Add(new SubjectPermissions
            {
                SecretName = secretName,
                SubjectType = SubjectType.User,
                SubjectId = userId,
                SubjectName = userId,
                CanRead = true,
                CanWrite = false,
                CanGrantAccess = false,
                CanRevokeAccess = false,
                PartitionKey = SubjectPermissions.DefaultPartitionKey,
            });
            await this.dbContext.SaveChangesAsync();
        }

        private TestHttpRequestData CreatePinRequest(string method, string userId)
        {
            var request = TestFactory.CreateHttpRequestData(method);
            request.FunctionContext.Items[DefaultAuthenticationMiddleware.InvocationContextUserIdKey] = userId;
            return request;
        }

        private async Task<TestHttpResponseData?> PinAsync(string secretName, string userId, CaseSensitiveClaimsIdentity identity)
        {
            var request = this.CreatePinRequest("put", userId);
            var principal = new ClaimsPrincipal(identity);
            var response = await this.pinnedSecrets.Run(request, secretName, principal, this.logger);
            return response as TestHttpResponseData;
        }

        private async Task<TestHttpResponseData?> UnpinAsync(string secretName, string userId, CaseSensitiveClaimsIdentity identity)
        {
            var request = this.CreatePinRequest("delete", userId);
            var principal = new ClaimsPrincipal(identity);
            var response = await this.pinnedSecrets.Run(request, secretName, principal, this.logger);
            return response as TestHttpResponseData;
        }

        private async Task<TestHttpResponseData?> GetPinAsync(string secretName, string userId, CaseSensitiveClaimsIdentity identity)
        {
            var request = this.CreatePinRequest("get", userId);
            var principal = new ClaimsPrincipal(identity);
            var response = await this.pinnedSecrets.Run(request, secretName, principal, this.logger);
            return response as TestHttpResponseData;
        }

        private async Task<TestHttpResponseData?> ListPinsAsync(string userId, CaseSensitiveClaimsIdentity identity)
        {
            var request = this.CreatePinRequest("get", userId);
            var principal = new ClaimsPrincipal(identity);
            var response = await this.pinnedSecretsList.RunList(request, principal, this.logger);
            return response as TestHttpResponseData;
        }
    }
}
```

> **Note on `SeedSecretAsync`:** the exact constructor signature for `ObjectMetadata` may differ slightly. Before running, open `SafeExchange.Core/Model/ObjectMetadata.cs` and adapt the constructor call to whatever constructor exists. If a simpler ctor is available (e.g. `new ObjectMetadata { ObjectName = ..., ... }`), use that. The test just needs an `ObjectMetadata` row in the DB with `ObjectName = secretName` and `Tags = tags`. If this gets messy, the simplest alternative is direct property initializer on `new ObjectMetadata()`.

- [ ] **Step 2: Confirm the file won't yet compile** (handler classes don't exist)

Run: `dotnet build SafeExchange.sln`
Expected: build FAILS with errors like `'SafeExchangePinnedSecrets' could not be found`. That's fine — we'll create the handler in Task 6. The fixture is checked in as scaffolding.

> **Do not commit yet.** Wait for Task 6 so the repo compiles after each commit.

---

## Task 6: PUT - happy path (RED → GREEN → COMMIT)

Creates `SafeExchangePinnedSecrets` with the minimum to make the happy-path test pass. Subsequent tasks extend the handler in small steps.

**Files:**
- Create: `SafeExchange.Core/Functions/SafeExchangePinnedSecrets.cs`
- Modify: `SafeExchange.Tests/Tests/PinnedSecretsTests.cs` (add test method)

- [ ] **Step 1: Write the failing test**

Add inside the `PinnedSecretsTests` class, after the helpers:

```csharp
[Test]
public async Task Pin_HappyPath_PersistsRowAndReturnsDto()
{
    // [GIVEN] User A owns secret 's-1' and has Read on it.
    var ownerId = "00000000-0000-0000-0000-000000000001";
    await this.SeedSecretAsync("s-1", ownerId, "prod", "db");

    // [WHEN] User A pins 's-1'.
    var response = await this.PinAsync("s-1", ownerId, this.firstIdentity);

    // [THEN] 200 OK with the expected DTO.
    Assert.That(response, Is.Not.Null);
    Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    var body = response.ReadBodyAsJson<BaseResponseObject<PinnedSecretOutput>>();
    Assert.That(body!.Status, Is.EqualTo("ok"));
    Assert.That(body.Result.SecretName, Is.EqualTo("s-1"));
    Assert.That(body.Result.Exists, Is.True);
    Assert.That(body.Result.CanRead, Is.True);
    Assert.That(body.Result.Tags, Is.EquivalentTo(new[] { "prod", "db" }));

    // [THEN] Pin row persisted.
    var rows = await this.dbContext.PinnedSecrets.ToListAsync();
    Assert.That(rows.Count, Is.EqualTo(1));
    Assert.That(rows[0].UserId, Is.EqualTo(ownerId));
    Assert.That(rows[0].SecretName, Is.EqualTo("s-1"));
}
```

- [ ] **Step 2: Run the test, confirm it fails to compile**

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~PinnedSecretsTests.Pin_HappyPath_PersistsRowAndReturnsDto"`
Expected: build FAILS (`SafeExchangePinnedSecrets` not found).

- [ ] **Step 3: Create the handler (minimal — only PUT happy path)**

```csharp
/// <summary>
/// SafeExchangePinnedSecrets
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class SafeExchangePinnedSecrets
    {
        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        private readonly IPermissionsManager permissionsManager;

        private readonly PinnedSecretsConfiguration config;

        public SafeExchangePinnedSecrets(
            SafeExchangeDbContext dbContext,
            ITokenHelper tokenHelper,
            GlobalFilters globalFilters,
            IPermissionsManager permissionsManager,
            IOptions<PinnedSecretsConfiguration> config)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
            this.config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        }

        public async Task<HttpResponseData> Run(
            HttpRequestData request,
            string secretId,
            ClaimsPrincipal principal,
            ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            (SubjectType subjectType, string subjectId) = await SubjectHelper.GetSubjectInfoAsync(this.tokenHelper, principal, this.dbContext);
            if (SubjectType.Application.Equals(subjectType))
            {
                return await ActionResults.ForbiddenAsync(request, "Applications cannot use this API.");
            }

            if (string.IsNullOrEmpty(secretId))
            {
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Secret id value is not provided." });
            }

            log.LogInformation($"{nameof(SafeExchangePinnedSecrets)} triggered for '{secretId}' by {subjectType} {subjectId}, [{request.Method}].");

            var userId = request.FunctionContext.GetUserId();

            switch (request.Method.ToLower())
            {
                case "put":
                    return await this.HandlePin(request, secretId, userId, subjectType, subjectId, log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = $"Request method '{request.Method}' not allowed." });
            }
        }

        private async Task<HttpResponseData> HandlePin(
            HttpRequestData request, string secretId, string userId,
            SubjectType subjectType, string subjectId, ILogger log)
            => await ActionResults.TryCatchAsync(request, async () =>
        {
            var metadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
            if (metadata is null)
            {
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.NotFound,
                    new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretId}' does not exist." });
            }

            if (!await this.permissionsManager.IsAuthorizedAsync(subjectType, subjectId, secretId, PermissionType.Read))
            {
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Forbidden,
                    ActionResults.InsufficientPermissions(PermissionType.Read, secretId, string.Empty));
            }

            var existing = await this.dbContext.PinnedSecrets
                .FirstOrDefaultAsync(p => p.UserId.Equals(userId) && p.SecretName.Equals(secretId));
            if (existing is not null)
            {
                log.LogInformation($"User '{userId}' attempted to pin secret '{secretId}' but pin already exists.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK,
                    new BaseResponseObject<PinnedSecretOutput>
                    {
                        Status = "ok",
                        Result = await this.BuildDtoAsync(existing.SecretName, userId, subjectType, subjectId)
                    });
            }

            var count = await this.dbContext.PinnedSecrets.Where(p => p.UserId.Equals(userId)).CountAsync();
            if (count >= this.config.MaxPinnedSecretsPerUser)
            {
                log.LogInformation($"User '{userId}' has {count} pinned secrets, which is >= max. allowed {this.config.MaxPinnedSecretsPerUser}.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object>
                    {
                        Status = "error",
                        Error = $"Pinned secret count is {count}, which is higher or equal than allowed no. of {this.config.MaxPinnedSecretsPerUser} pinned secrets. Please unpin secrets before adding new ones."
                    });
            }

            var pin = await DbUtils.TryAddOrGetEntityAsync(
                async () =>
                {
                    var entity = await this.dbContext.PinnedSecrets.AddAsync(new PinnedSecret(userId, secretId));
                    await this.dbContext.SaveChangesAsync();
                    return entity.Entity;
                },
                async () =>
                {
                    return await this.dbContext.PinnedSecrets.FirstAsync(p => p.UserId.Equals(userId) && p.SecretName.Equals(secretId));
                },
                log);

            log.LogInformation($"User '{userId}' pinned secret '{secretId}'.");

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<PinnedSecretOutput>
                {
                    Status = "ok",
                    Result = await this.BuildDtoAsync(pin.SecretName, userId, subjectType, subjectId)
                });

        }, nameof(HandlePin), log);

        internal async Task<PinnedSecretOutput> BuildDtoAsync(
            string secretName, string userId, SubjectType subjectType, string subjectId)
        {
            var dto = new PinnedSecretOutput { SecretName = secretName };

            var metadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretName));
            dto.Exists = metadata is not null;

            var permission = await this.dbContext.Permissions
                .FirstOrDefaultAsync(p => p.SecretName.Equals(secretName)
                                       && p.SubjectType.Equals(subjectType)
                                       && p.SubjectId.Equals(subjectId));
            if (permission is not null)
            {
                dto.CanRead = permission.CanRead;
                dto.CanWrite = permission.CanWrite;
                dto.CanGrantAccess = permission.CanGrantAccess;
                dto.CanRevokeAccess = permission.CanRevokeAccess;
            }

            if (dto.Exists && dto.CanRead)
            {
                dto.Tags = metadata.Tags?.ToList() ?? new List<string>();
            }
            else
            {
                dto.Tags = new List<string>();
            }

            return dto;
        }
    }
}
```

- [ ] **Step 4: Run the test, confirm it passes**

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~PinnedSecretsTests.Pin_HappyPath"`
Expected: PASS.

- [ ] **Step 5: Commit (test scaffolding + handler skeleton + first green test)**

```bash
git add SafeExchange.Tests/Tests/PinnedSecretsTests.cs SafeExchange.Core/Functions/SafeExchangePinnedSecrets.cs
git commit -m "feat(pinned-secrets): PUT happy path with handler skeleton"
```

---

## Task 7: PUT - 403 for Application caller

**Files:**
- Modify: `SafeExchange.Tests/Tests/PinnedSecretsTests.cs`

- [ ] **Step 1: Write the failing test**

Add to the test class (modeled on `PinnedGroupsTests.RunList_ReturnsForbidden_ForApplicationToken`):

```csharp
[Test]
public async Task Pin_ApplicationCaller_Returns403()
{
    // [GIVEN] A token helper that classifies the caller as an application.
    var appTokenHelper = new Mock<ITokenHelper>();
    appTokenHelper.Setup(h => h.IsUserToken(It.IsAny<ClaimsPrincipal>())).Returns(false);
    appTokenHelper.Setup(h => h.GetTenantId(It.IsAny<ClaimsPrincipal>()))
        .Returns("00000000-0000-0000-0000-000000000001");
    appTokenHelper.Setup(h => h.GetApplicationClientId(It.IsAny<ClaimsPrincipal>()))
        .Returns("00000000-0000-0000-0000-aaaaaaaaaaaa");
    appTokenHelper.Setup(h => h.GetUpn(It.IsAny<ClaimsPrincipal>())).Returns(string.Empty);
    appTokenHelper.Setup(h => h.GetObjectId(It.IsAny<ClaimsPrincipal>()))
        .Returns("00000000-0000-0000-0000-999999999999");
    appTokenHelper.Setup(h => h.GetDisplayName(It.IsAny<ClaimsPrincipal>())).Returns(string.Empty);

    var appPinnedSecrets = new SafeExchangePinnedSecrets(
        this.dbContext, appTokenHelper.Object, this.globalFilters,
        this.permissionsManager, Options.Create(this.pinnedSecretsConfig));

    var appIdentity = new CaseSensitiveClaimsIdentity(new List<Claim>
    {
        new Claim("appid", "00000000-0000-0000-0000-aaaaaaaaaaaa"),
        new Claim("oid", "00000000-0000-0000-0000-999999999999"),
        new Claim("tid", "00000000-0000-0000-0000-000000000001"),
    }.AsEnumerable());

    var request = TestFactory.CreateHttpRequestData("put");
    request.FunctionContext.Items[DefaultAuthenticationMiddleware.InvocationContextUserIdKey] =
        "00000000-0000-0000-0000-999999999999";

    // [WHEN] App calls PUT.
    var raw = await appPinnedSecrets.Run(request, "s-1", new ClaimsPrincipal(appIdentity), this.logger);

    // [THEN] 403 Forbidden with the "Applications cannot use this API" message.
    var response = raw as TestHttpResponseData;
    Assert.That(response, Is.Not.Null);
    Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    var body = response.ReadBodyAsJson<BaseResponseObject<object>>();
    Assert.That(body!.Status, Is.EqualTo("forbidden"));
    Assert.That(body.Error, Does.Contain("Applications cannot use this API"));
}
```

- [ ] **Step 2: Run the test, confirm it passes (Application gate is already in `Run`)**

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~PinnedSecretsTests.Pin_ApplicationCaller"`
Expected: PASS (regression guard; the gate is already in place).

- [ ] **Step 3: Commit**

```bash
git add SafeExchange.Tests/Tests/PinnedSecretsTests.cs
git commit -m "test(pinned-secrets): lock in Application=403 regression guard"
```

---

## Task 8: PUT - 404 when secret doesn't exist

**Files:**
- Modify: `SafeExchange.Tests/Tests/PinnedSecretsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Test]
public async Task Pin_SecretMissing_Returns404()
{
    var userId = "00000000-0000-0000-0000-000000000001";

    var response = await this.PinAsync("does-not-exist", userId, this.firstIdentity);

    Assert.That(response, Is.Not.Null);
    Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    var body = response.ReadBodyAsJson<BaseResponseObject<object>>();
    Assert.That(body!.Status, Is.EqualTo("not_found"));
    Assert.That(body.Error, Does.Contain("does not exist"));

    // No pin row created.
    Assert.That(await this.dbContext.PinnedSecrets.CountAsync(), Is.EqualTo(0));
}
```

- [ ] **Step 2: Run, confirm PASS** (404 logic is already in `HandlePin`)

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~PinnedSecretsTests.Pin_SecretMissing"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add SafeExchange.Tests/Tests/PinnedSecretsTests.cs
git commit -m "test(pinned-secrets): pin missing secret returns 404"
```

---

## Task 9: PUT - 403 when caller lacks Read

**Files:**
- Modify: `SafeExchange.Tests/Tests/PinnedSecretsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Test]
public async Task Pin_NoReadPermission_Returns403()
{
    // [GIVEN] First user owns s-1 (and has Read). Second user has nothing.
    var ownerId = "00000000-0000-0000-0000-000000000001";
    await this.SeedSecretAsync("s-1", ownerId);

    var secondUserId = "00000000-0000-0000-0000-000000000002";

    // [WHEN] Second user tries to pin s-1.
    var response = await this.PinAsync("s-1", secondUserId, this.secondIdentity);

    // [THEN] 403 with InsufficientPermissions shape.
    Assert.That(response, Is.Not.Null);
    Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

    // No pin row created.
    var rows = await this.dbContext.PinnedSecrets.Where(p => p.UserId == secondUserId).ToListAsync();
    Assert.That(rows.Count, Is.EqualTo(0));
}
```

- [ ] **Step 2: Run, confirm PASS** (Read check already in handler)

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~PinnedSecretsTests.Pin_NoReadPermission"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add SafeExchange.Tests/Tests/PinnedSecretsTests.cs
git commit -m "test(pinned-secrets): pin without Read returns 403"
```

---

## Task 10: PUT - idempotent re-pin

**Files:**
- Modify: `SafeExchange.Tests/Tests/PinnedSecretsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Test]
public async Task Pin_Idempotent_DoesNotDuplicate()
{
    var userId = "00000000-0000-0000-0000-000000000001";
    await this.SeedSecretAsync("s-1", userId);

    // [WHEN] User pins twice.
    var first = await this.PinAsync("s-1", userId, this.firstIdentity);
    var second = await this.PinAsync("s-1", userId, this.firstIdentity);

    // [THEN] Both succeed.
    Assert.That(first!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    Assert.That(second!.StatusCode, Is.EqualTo(HttpStatusCode.OK));

    // [THEN] Only one row persisted.
    var rows = await this.dbContext.PinnedSecrets.ToListAsync();
    Assert.That(rows.Count, Is.EqualTo(1));
}
```

- [ ] **Step 2: Run, confirm PASS** (existence check already in handler)

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~PinnedSecretsTests.Pin_Idempotent"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add SafeExchange.Tests/Tests/PinnedSecretsTests.cs
git commit -m "test(pinned-secrets): pin is idempotent"
```

---

## Task 11: PUT - cap exceeded returns 400

**Files:**
- Modify: `SafeExchange.Tests/Tests/PinnedSecretsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Test]
public async Task Pin_CapExceeded_Returns400()
{
    var userId = "00000000-0000-0000-0000-000000000001";
    var max = this.pinnedSecretsConfig.MaxPinnedSecretsPerUser; // 5

    // [GIVEN] 5 secrets already pinned (max).
    for (int i = 1; i <= max; i++)
    {
        await this.SeedSecretAsync($"s-{i}", userId);
        var ok = await this.PinAsync($"s-{i}", userId, this.firstIdentity);
        Assert.That(ok!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    // [WHEN] Tries to pin a 6th.
    await this.SeedSecretAsync("s-6", userId);
    var response = await this.PinAsync("s-6", userId, this.firstIdentity);

    // [THEN] 400 with cap-exceeded message.
    Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    var body = response.ReadBodyAsJson<BaseResponseObject<object>>();
    Assert.That(body!.Error, Is.EqualTo(
        "Pinned secret count is 5, which is higher or equal than allowed no. of 5 pinned secrets. Please unpin secrets before adding new ones."));

    // [THEN] Still only 5 rows.
    Assert.That(await this.dbContext.PinnedSecrets.CountAsync(), Is.EqualTo(max));
}
```

- [ ] **Step 2: Run, confirm PASS** (cap check already in handler)

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~PinnedSecretsTests.Pin_CapExceeded"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add SafeExchange.Tests/Tests/PinnedSecretsTests.cs
git commit -m "test(pinned-secrets): pin past cap returns 400"
```

---

## Task 12: GET single - returns no_content when pin absent

**Files:**
- Modify: `SafeExchange.Core/Functions/SafeExchangePinnedSecrets.cs` (add `case "get"`)
- Modify: `SafeExchange.Tests/Tests/PinnedSecretsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Test]
public async Task GetPin_NotPinned_ReturnsNoContent()
{
    var userId = "00000000-0000-0000-0000-000000000001";
    await this.SeedSecretAsync("s-1", userId);

    // No pin row exists.
    var response = await this.GetPinAsync("s-1", userId, this.firstIdentity);

    Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    var body = response.ReadBodyAsJson<BaseResponseObject<PinnedSecretOutput>>();
    Assert.That(body!.Status, Is.EqualTo("no_content"));
    Assert.That(body.Result, Is.Null);
}
```

- [ ] **Step 2: Run, confirm FAIL** (no `get` case yet — falls through to "method not allowed")

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~PinnedSecretsTests.GetPin_NotPinned"`
Expected: FAIL.

- [ ] **Step 3: Add the GET case to the handler**

In `SafeExchangePinnedSecrets.Run`, add a new case before `default:`:

```csharp
case "get":
    return await this.HandleGetPin(request, secretId, userId, subjectType, subjectId, log);
```

…and a new method (place after `HandlePin`):

```csharp
private async Task<HttpResponseData> HandleGetPin(
    HttpRequestData request, string secretId, string userId,
    SubjectType subjectType, string subjectId, ILogger log)
    => await ActionResults.TryCatchAsync(request, async () =>
{
    var existing = await this.dbContext.PinnedSecrets
        .FirstOrDefaultAsync(p => p.UserId.Equals(userId) && p.SecretName.Equals(secretId));
    if (existing is null)
    {
        return await ActionResults.CreateResponseAsync(
            request, HttpStatusCode.OK,
            new BaseResponseObject<PinnedSecretOutput> { Status = "no_content", Result = null });
    }

    return await ActionResults.CreateResponseAsync(
        request, HttpStatusCode.OK,
        new BaseResponseObject<PinnedSecretOutput>
        {
            Status = "ok",
            Result = await this.BuildDtoAsync(existing.SecretName, userId, subjectType, subjectId)
        });

}, nameof(HandleGetPin), log);
```

- [ ] **Step 4: Run, confirm PASS**

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~PinnedSecretsTests.GetPin_NotPinned"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add SafeExchange.Core/Functions/SafeExchangePinnedSecrets.cs SafeExchange.Tests/Tests/PinnedSecretsTests.cs
git commit -m "feat(pinned-secrets): GET single - no_content when not pinned"
```

---

## Task 13: GET single - live readable secret

**Files:**
- Modify: `SafeExchange.Tests/Tests/PinnedSecretsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Test]
public async Task GetPin_LiveSecret_ReturnsDto()
{
    var userId = "00000000-0000-0000-0000-000000000001";
    await this.SeedSecretAsync("s-1", userId, "prod");
    await this.PinAsync("s-1", userId, this.firstIdentity);

    var response = await this.GetPinAsync("s-1", userId, this.firstIdentity);
    Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));

    var body = response.ReadBodyAsJson<BaseResponseObject<PinnedSecretOutput>>();
    Assert.That(body!.Status, Is.EqualTo("ok"));
    Assert.That(body.Result.SecretName, Is.EqualTo("s-1"));
    Assert.That(body.Result.Exists, Is.True);
    Assert.That(body.Result.CanRead, Is.True);
    Assert.That(body.Result.Tags, Is.EquivalentTo(new[] { "prod" }));
}
```

- [ ] **Step 2: Run, confirm PASS**

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~PinnedSecretsTests.GetPin_LiveSecret"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add SafeExchange.Tests/Tests/PinnedSecretsTests.cs
git commit -m "test(pinned-secrets): GET single - live secret returns DTO"
```

---

## Task 14: GET single - access lost

**Files:**
- Modify: `SafeExchange.Tests/Tests/PinnedSecretsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Test]
public async Task GetPin_AccessLost_ReturnsDtoWithCanReadFalse()
{
    var userId = "00000000-0000-0000-0000-000000000001";
    await this.SeedSecretAsync("s-1", userId, "prod");
    await this.PinAsync("s-1", userId, this.firstIdentity);

    // [GIVEN] User's permission row removed (admin revoked).
    var perm = await this.dbContext.Permissions.FirstAsync(
        p => p.SecretName == "s-1" && p.SubjectId == userId);
    this.dbContext.Permissions.Remove(perm);
    await this.dbContext.SaveChangesAsync();

    var response = await this.GetPinAsync("s-1", userId, this.firstIdentity);
    Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    var body = response.ReadBodyAsJson<BaseResponseObject<PinnedSecretOutput>>();
    Assert.That(body!.Status, Is.EqualTo("ok"));
    Assert.That(body.Result.SecretName, Is.EqualTo("s-1"));
    Assert.That(body.Result.Exists, Is.True);
    Assert.That(body.Result.CanRead, Is.False);
    Assert.That(body.Result.CanWrite, Is.False);
    Assert.That(body.Result.CanGrantAccess, Is.False);
    Assert.That(body.Result.CanRevokeAccess, Is.False);
    Assert.That(body.Result.Tags, Is.Empty);
}
```

- [ ] **Step 2: Run, confirm PASS** (DTO builder already does this)

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~PinnedSecretsTests.GetPin_AccessLost"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add SafeExchange.Tests/Tests/PinnedSecretsTests.cs
git commit -m "test(pinned-secrets): GET single - access lost zeroes permission flags"
```

---

## Task 15: GET single - secret deleted

**Files:**
- Modify: `SafeExchange.Tests/Tests/PinnedSecretsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Test]
public async Task GetPin_SecretDeleted_ReturnsDtoWithExistsFalse()
{
    var userId = "00000000-0000-0000-0000-000000000001";
    await this.SeedSecretAsync("s-1", userId);
    await this.PinAsync("s-1", userId, this.firstIdentity);

    // [GIVEN] Secret deleted, permission row deleted, pin row remains.
    var perm = await this.dbContext.Permissions.FirstAsync(
        p => p.SecretName == "s-1" && p.SubjectId == userId);
    var metadata = await this.dbContext.Objects.FirstAsync(o => o.ObjectName == "s-1");
    this.dbContext.Permissions.Remove(perm);
    this.dbContext.Objects.Remove(metadata);
    await this.dbContext.SaveChangesAsync();

    var response = await this.GetPinAsync("s-1", userId, this.firstIdentity);
    Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    var body = response.ReadBodyAsJson<BaseResponseObject<PinnedSecretOutput>>();
    Assert.That(body!.Status, Is.EqualTo("ok"));
    Assert.That(body.Result.SecretName, Is.EqualTo("s-1"));
    Assert.That(body.Result.Exists, Is.False);
    Assert.That(body.Result.CanRead, Is.False);
    Assert.That(body.Result.Tags, Is.Empty);
}
```

- [ ] **Step 2: Run, confirm PASS**

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~PinnedSecretsTests.GetPin_SecretDeleted"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add SafeExchange.Tests/Tests/PinnedSecretsTests.cs
git commit -m "test(pinned-secrets): GET single - deleted secret has exists=false"
```

---

## Task 16: DELETE - happy path

**Files:**
- Modify: `SafeExchange.Core/Functions/SafeExchangePinnedSecrets.cs` (add `case "delete"`)
- Modify: `SafeExchange.Tests/Tests/PinnedSecretsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Test]
public async Task Unpin_HappyPath_RemovesRow()
{
    var userId = "00000000-0000-0000-0000-000000000001";
    await this.SeedSecretAsync("s-1", userId);
    await this.PinAsync("s-1", userId, this.firstIdentity);
    Assert.That(await this.dbContext.PinnedSecrets.CountAsync(), Is.EqualTo(1));

    var response = await this.UnpinAsync("s-1", userId, this.firstIdentity);

    Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    var body = response.ReadBodyAsJson<BaseResponseObject<string>>();
    Assert.That(body!.Status, Is.EqualTo("ok"));
    Assert.That(body.Result, Is.EqualTo("ok"));

    Assert.That(await this.dbContext.PinnedSecrets.CountAsync(), Is.EqualTo(0));
}
```

- [ ] **Step 2: Run, confirm FAIL** (no `delete` case yet)

Expected: FAIL with method-not-allowed.

- [ ] **Step 3: Add `case "delete"` and handler**

In `Run`, add before `default:`:

```csharp
case "delete":
    return await this.HandleUnpin(request, secretId, userId, log);
```

…and after `HandleGetPin`:

```csharp
private async Task<HttpResponseData> HandleUnpin(
    HttpRequestData request, string secretId, string userId, ILogger log)
    => await ActionResults.TryCatchAsync(request, async () =>
{
    var existing = await this.dbContext.PinnedSecrets
        .FirstOrDefaultAsync(p => p.UserId.Equals(userId) && p.SecretName.Equals(secretId));
    if (existing is null)
    {
        log.LogInformation($"User '{userId}' attempted to unpin secret '{secretId}' but pin does not exist.");
        return await ActionResults.CreateResponseAsync(
            request, HttpStatusCode.OK,
            new BaseResponseObject<string>
            {
                Status = "no_content",
                Result = $"Pin for secret '{secretId}' does not exist."
            });
    }

    this.dbContext.PinnedSecrets.Remove(existing);
    await this.dbContext.SaveChangesAsync();

    log.LogInformation($"User '{userId}' unpinned secret '{secretId}'.");

    return await ActionResults.CreateResponseAsync(
        request, HttpStatusCode.OK,
        new BaseResponseObject<string> { Status = "ok", Result = "ok" });

}, nameof(HandleUnpin), log);
```

- [ ] **Step 4: Run, confirm PASS**

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~PinnedSecretsTests.Unpin_HappyPath"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add SafeExchange.Core/Functions/SafeExchangePinnedSecrets.cs SafeExchange.Tests/Tests/PinnedSecretsTests.cs
git commit -m "feat(pinned-secrets): DELETE happy path"
```

---

## Task 17: DELETE - idempotent (no row exists)

**Files:**
- Modify: `SafeExchange.Tests/Tests/PinnedSecretsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Test]
public async Task Unpin_NoExistingPin_ReturnsNoContent()
{
    var userId = "00000000-0000-0000-0000-000000000001";

    var response = await this.UnpinAsync("never-pinned", userId, this.firstIdentity);

    Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    var body = response.ReadBodyAsJson<BaseResponseObject<string>>();
    Assert.That(body!.Status, Is.EqualTo("no_content"));
    Assert.That(body.Result, Does.Contain("does not exist"));
}
```

- [ ] **Step 2: Run, confirm PASS** (idempotent path already in handler)

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~PinnedSecretsTests.Unpin_NoExistingPin"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add SafeExchange.Tests/Tests/PinnedSecretsTests.cs
git commit -m "test(pinned-secrets): DELETE on missing pin is idempotent"
```

---

## Task 18: DELETE - works after access loss

**Files:**
- Modify: `SafeExchange.Tests/Tests/PinnedSecretsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Test]
public async Task Unpin_AfterAccessLoss_StillSucceeds()
{
    var userId = "00000000-0000-0000-0000-000000000001";
    await this.SeedSecretAsync("s-1", userId);
    await this.PinAsync("s-1", userId, this.firstIdentity);

    // Revoke permission row (admin-driven scenario).
    var perm = await this.dbContext.Permissions.FirstAsync(
        p => p.SecretName == "s-1" && p.SubjectId == userId);
    this.dbContext.Permissions.Remove(perm);
    await this.dbContext.SaveChangesAsync();

    var response = await this.UnpinAsync("s-1", userId, this.firstIdentity);
    Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    var body = response.ReadBodyAsJson<BaseResponseObject<string>>();
    Assert.That(body!.Status, Is.EqualTo("ok"));

    Assert.That(await this.dbContext.PinnedSecrets.CountAsync(), Is.EqualTo(0));
}
```

- [ ] **Step 2: Run, confirm PASS** (DELETE doesn't gate on Read)

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~PinnedSecretsTests.Unpin_AfterAccessLoss"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add SafeExchange.Tests/Tests/PinnedSecretsTests.cs
git commit -m "test(pinned-secrets): DELETE works after access loss"
```

---

## Task 19: List - empty (creates list handler)

**Files:**
- Create: `SafeExchange.Core/Functions/SafeExchangePinnedSecretsList.cs`
- Modify: `SafeExchange.Tests/Tests/PinnedSecretsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Test]
public async Task List_NoPins_ReturnsNoContentEmptyList()
{
    var userId = "00000000-0000-0000-0000-000000000001";

    var response = await this.ListPinsAsync(userId, this.firstIdentity);

    Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    var body = response.ReadBodyAsJson<BaseResponseObject<List<PinnedSecretOutput>>>();
    Assert.That(body!.Status, Is.EqualTo("no_content"));
    Assert.That(body.Result, Is.Empty);
}
```

- [ ] **Step 2: Run, confirm FAIL to compile** (`SafeExchangePinnedSecretsList` not found)

Expected: FAIL (build).

- [ ] **Step 3: Create the list handler**

```csharp
/// <summary>
/// SafeExchangePinnedSecretsList
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class SafeExchangePinnedSecretsList
    {
        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        public SafeExchangePinnedSecretsList(
            SafeExchangeDbContext dbContext,
            ITokenHelper tokenHelper,
            GlobalFilters globalFilters)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
        }

        public async Task<HttpResponseData> RunList(
            HttpRequestData request, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            (SubjectType subjectType, string subjectId) = await SubjectHelper.GetSubjectInfoAsync(this.tokenHelper, principal, this.dbContext);
            if (SubjectType.Application.Equals(subjectType))
            {
                return await ActionResults.ForbiddenAsync(request, "Applications cannot use this API.");
            }

            log.LogInformation($"{nameof(SafeExchangePinnedSecretsList)} triggered by {subjectType} {subjectId}, [{request.Method}].");

            var userId = request.FunctionContext.GetUserId();
            switch (request.Method.ToLower())
            {
                case "get":
                    return await this.HandleList(request, userId, subjectType, subjectId, log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = $"Request method '{request.Method}' not allowed." });
            }
        }

        private async Task<HttpResponseData> HandleList(
            HttpRequestData request, string userId,
            SubjectType subjectType, string subjectId, ILogger log)
            => await ActionResults.TryCatchAsync(request, async () =>
        {
            var pins = await this.dbContext.PinnedSecrets
                .Where(p => p.UserId.Equals(userId))
                .ToListAsync();

            if (pins.Count == 0)
            {
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK,
                    new BaseResponseObject<List<PinnedSecretOutput>>
                    {
                        Status = "no_content",
                        Result = new List<PinnedSecretOutput>()
                    });
            }

            // Sort DESC in memory (Cosmos OrderBy on non-indexed property can be costly;
            // small N here makes this trivially cheap).
            pins = pins.OrderByDescending(p => p.CreatedAt).ToList();

            var names = pins.Select(p => p.SecretName).Distinct().ToList();

            var metadataByName = (await this.dbContext.Objects
                    .Where(o => names.Contains(o.ObjectName))
                    .ToListAsync())
                .ToDictionary(o => o.ObjectName, o => o);

            var permsByName = (await this.dbContext.Permissions
                    .Where(p => names.Contains(p.SecretName)
                             && p.SubjectType.Equals(subjectType)
                             && p.SubjectId.Equals(subjectId))
                    .ToListAsync())
                .ToDictionary(p => p.SecretName, p => p);

            var result = new List<PinnedSecretOutput>(pins.Count);
            foreach (var pin in pins)
            {
                var dto = new PinnedSecretOutput { SecretName = pin.SecretName };

                if (metadataByName.TryGetValue(pin.SecretName, out var meta))
                {
                    dto.Exists = true;
                }

                if (permsByName.TryGetValue(pin.SecretName, out var perm))
                {
                    dto.CanRead = perm.CanRead;
                    dto.CanWrite = perm.CanWrite;
                    dto.CanGrantAccess = perm.CanGrantAccess;
                    dto.CanRevokeAccess = perm.CanRevokeAccess;
                }

                if (dto.Exists && dto.CanRead)
                {
                    dto.Tags = meta.Tags?.ToList() ?? new List<string>();
                }
                else
                {
                    dto.Tags = new List<string>();
                }

                result.Add(dto);
            }

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<List<PinnedSecretOutput>> { Status = "ok", Result = result });

        }, nameof(HandleList), log);
    }
}
```

- [ ] **Step 4: Run, confirm PASS**

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~PinnedSecretsTests.List_NoPins"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add SafeExchange.Core/Functions/SafeExchangePinnedSecretsList.cs SafeExchange.Tests/Tests/PinnedSecretsTests.cs
git commit -m "feat(pinned-secrets): list endpoint (empty case)"
```

---

## Task 20: List - multiple pins sorted CreatedAt DESC

**Files:**
- Modify: `SafeExchange.Tests/Tests/PinnedSecretsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Test]
public async Task List_MultiplePins_SortedDescByCreatedAt()
{
    var userId = "00000000-0000-0000-0000-000000000001";
    await this.SeedSecretAsync("s-1", userId);
    await this.SeedSecretAsync("s-2", userId);
    await this.SeedSecretAsync("s-3", userId);

    // [GIVEN] Pin s-1, advance time, pin s-2, advance time, pin s-3.
    DateTimeProvider.SpecifiedDateTime = DateTime.UtcNow;
    await this.PinAsync("s-1", userId, this.firstIdentity);
    DateTimeProvider.SpecifiedDateTime = DateTimeProvider.SpecifiedDateTime.AddSeconds(10);
    await this.PinAsync("s-2", userId, this.firstIdentity);
    DateTimeProvider.SpecifiedDateTime = DateTimeProvider.SpecifiedDateTime.AddSeconds(10);
    await this.PinAsync("s-3", userId, this.firstIdentity);

    var response = await this.ListPinsAsync(userId, this.firstIdentity);
    Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    var body = response.ReadBodyAsJson<BaseResponseObject<List<PinnedSecretOutput>>>();
    Assert.That(body!.Status, Is.EqualTo("ok"));
    Assert.That(body.Result.Count, Is.EqualTo(3));

    // [THEN] Newest first.
    Assert.That(body.Result[0].SecretName, Is.EqualTo("s-3"));
    Assert.That(body.Result[1].SecretName, Is.EqualTo("s-2"));
    Assert.That(body.Result[2].SecretName, Is.EqualTo("s-1"));
}
```

- [ ] **Step 2: Run, confirm PASS**

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~PinnedSecretsTests.List_MultiplePins"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add SafeExchange.Tests/Tests/PinnedSecretsTests.cs
git commit -m "test(pinned-secrets): list returns CreatedAt DESC"
```

---

## Task 21: List - mix of live/access-lost/deleted

**Files:**
- Modify: `SafeExchange.Tests/Tests/PinnedSecretsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Test]
public async Task List_MixedStates_AllReturnedWithFlags()
{
    var userId = "00000000-0000-0000-0000-000000000001";

    // [GIVEN] Three secrets pinned, then one's permission revoked and one deleted.
    await this.SeedSecretAsync("s-live", userId, "tag1");
    await this.SeedSecretAsync("s-noaccess", userId, "tag2");
    await this.SeedSecretAsync("s-deleted", userId, "tag3");

    DateTimeProvider.SpecifiedDateTime = DateTime.UtcNow;
    await this.PinAsync("s-live", userId, this.firstIdentity);
    DateTimeProvider.SpecifiedDateTime = DateTimeProvider.SpecifiedDateTime.AddSeconds(10);
    await this.PinAsync("s-noaccess", userId, this.firstIdentity);
    DateTimeProvider.SpecifiedDateTime = DateTimeProvider.SpecifiedDateTime.AddSeconds(10);
    await this.PinAsync("s-deleted", userId, this.firstIdentity);

    // Revoke permission on s-noaccess.
    var perm = await this.dbContext.Permissions.FirstAsync(
        p => p.SecretName == "s-noaccess" && p.SubjectId == userId);
    this.dbContext.Permissions.Remove(perm);

    // Delete s-deleted entirely.
    var meta = await this.dbContext.Objects.FirstAsync(o => o.ObjectName == "s-deleted");
    var deletedPerm = await this.dbContext.Permissions.FirstAsync(
        p => p.SecretName == "s-deleted" && p.SubjectId == userId);
    this.dbContext.Objects.Remove(meta);
    this.dbContext.Permissions.Remove(deletedPerm);

    await this.dbContext.SaveChangesAsync();

    var response = await this.ListPinsAsync(userId, this.firstIdentity);
    var body = response!.ReadBodyAsJson<BaseResponseObject<List<PinnedSecretOutput>>>();
    Assert.That(body!.Result.Count, Is.EqualTo(3));

    // Order: s-deleted (newest), s-noaccess, s-live.
    var deleted = body.Result[0];
    Assert.That(deleted.SecretName, Is.EqualTo("s-deleted"));
    Assert.That(deleted.Exists, Is.False);
    Assert.That(deleted.CanRead, Is.False);
    Assert.That(deleted.Tags, Is.Empty);

    var noaccess = body.Result[1];
    Assert.That(noaccess.SecretName, Is.EqualTo("s-noaccess"));
    Assert.That(noaccess.Exists, Is.True);
    Assert.That(noaccess.CanRead, Is.False);
    Assert.That(noaccess.Tags, Is.Empty);

    var live = body.Result[2];
    Assert.That(live.SecretName, Is.EqualTo("s-live"));
    Assert.That(live.Exists, Is.True);
    Assert.That(live.CanRead, Is.True);
    Assert.That(live.Tags, Is.EquivalentTo(new[] { "tag1" }));
}
```

- [ ] **Step 2: Run, confirm PASS**

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~PinnedSecretsTests.List_MixedStates"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add SafeExchange.Tests/Tests/PinnedSecretsTests.cs
git commit -m "test(pinned-secrets): list surfaces live/access-lost/deleted states"
```

---

## Task 22: List - user isolation

**Files:**
- Modify: `SafeExchange.Tests/Tests/PinnedSecretsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Test]
public async Task List_TwoUsers_EachSeesOwnPinsOnly()
{
    var firstUserId = "00000000-0000-0000-0000-000000000001";
    var secondUserId = "00000000-0000-0000-0000-000000000002";

    await this.SeedSecretAsync("s-1", firstUserId);
    await this.SeedSecretAsync("s-2", secondUserId);

    await this.PinAsync("s-1", firstUserId, this.firstIdentity);
    await this.PinAsync("s-2", secondUserId, this.secondIdentity);

    var firstResponse = await this.ListPinsAsync(firstUserId, this.firstIdentity);
    var firstBody = firstResponse!.ReadBodyAsJson<BaseResponseObject<List<PinnedSecretOutput>>>();
    Assert.That(firstBody!.Result.Count, Is.EqualTo(1));
    Assert.That(firstBody.Result[0].SecretName, Is.EqualTo("s-1"));

    var secondResponse = await this.ListPinsAsync(secondUserId, this.secondIdentity);
    var secondBody = secondResponse!.ReadBodyAsJson<BaseResponseObject<List<PinnedSecretOutput>>>();
    Assert.That(secondBody!.Result.Count, Is.EqualTo(1));
    Assert.That(secondBody.Result[0].SecretName, Is.EqualTo("s-2"));
}
```

- [ ] **Step 2: Run, confirm PASS**

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~PinnedSecretsTests.List_TwoUsers"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add SafeExchange.Tests/Tests/PinnedSecretsTests.cs
git commit -m "test(pinned-secrets): list isolated per user"
```

---

## Task 23: List - Application caller returns 403 (regression guard)

**Files:**
- Modify: `SafeExchange.Tests/Tests/PinnedSecretsTests.cs`

- [ ] **Step 1: Write the failing test** (regression guard for the missing-return bug class — see `PinnedGroupsTests.RunList_ReturnsForbidden_ForApplicationToken`)

```csharp
[Test]
public async Task List_ApplicationCaller_Returns403()
{
    var appTokenHelper = new Mock<ITokenHelper>();
    appTokenHelper.Setup(h => h.IsUserToken(It.IsAny<ClaimsPrincipal>())).Returns(false);
    appTokenHelper.Setup(h => h.GetTenantId(It.IsAny<ClaimsPrincipal>()))
        .Returns("00000000-0000-0000-0000-000000000001");
    appTokenHelper.Setup(h => h.GetApplicationClientId(It.IsAny<ClaimsPrincipal>()))
        .Returns("00000000-0000-0000-0000-aaaaaaaaaaaa");
    appTokenHelper.Setup(h => h.GetUpn(It.IsAny<ClaimsPrincipal>())).Returns(string.Empty);
    appTokenHelper.Setup(h => h.GetObjectId(It.IsAny<ClaimsPrincipal>()))
        .Returns("00000000-0000-0000-0000-999999999999");
    appTokenHelper.Setup(h => h.GetDisplayName(It.IsAny<ClaimsPrincipal>())).Returns(string.Empty);

    var appList = new SafeExchangePinnedSecretsList(this.dbContext, appTokenHelper.Object, this.globalFilters);

    var appIdentity = new CaseSensitiveClaimsIdentity(new List<Claim>
    {
        new Claim("appid", "00000000-0000-0000-0000-aaaaaaaaaaaa"),
        new Claim("oid", "00000000-0000-0000-0000-999999999999"),
        new Claim("tid", "00000000-0000-0000-0000-000000000001"),
    }.AsEnumerable());

    var request = TestFactory.CreateHttpRequestData("get");
    request.FunctionContext.Items[DefaultAuthenticationMiddleware.InvocationContextUserIdKey] =
        "00000000-0000-0000-0000-999999999999";

    var raw = await appList.RunList(request, new ClaimsPrincipal(appIdentity), this.logger);
    var response = raw as TestHttpResponseData;
    Assert.That(response, Is.Not.Null);
    Assert.That(response!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    var body = response.ReadBodyAsJson<BaseResponseObject<object>>();
    Assert.That(body!.Error, Does.Contain("Applications cannot use this API"));
}
```

- [ ] **Step 2: Run, confirm PASS**

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~PinnedSecretsTests.List_ApplicationCaller"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add SafeExchange.Tests/Tests/PinnedSecretsTests.cs
git commit -m "test(pinned-secrets): list rejects Application token (regression guard)"
```

---

## Task 24: Run full test suite (gate before wiring HTTP triggers)

- [ ] **Step 1: Run all PinnedSecrets tests**

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj --filter "FullyQualifiedName~PinnedSecretsTests"`
Expected: ALL PASS. Count should match the 13 tests added across tasks 6–23. If any fails, fix before continuing.

- [ ] **Step 2: Run full test suite to catch regressions**

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj`
Expected: ALL PASS. If anything fails that wasn't failing before this branch, investigate.

- [ ] **Step 3: No commit** — this is a verification gate. Proceed only if green.

---

## Task 25: Wire up Azure Function HTTP triggers

**Files:**
- Create: `SafeExchange.Functions/Functions/SafePinnedSecrets.cs`

- [ ] **Step 1: Create the trigger class**

```csharp
/// <summary>
/// SafePinnedSecrets
/// </summary>

namespace SafeExchange.Functions.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Permissions;
    using System;
    using System.Threading.Tasks;

    public class SafePinnedSecrets
    {
        private const string Version = "v2";

        private SafeExchangePinnedSecrets safeExchangePinnedSecretsHandler;

        private SafeExchangePinnedSecretsList safeExchangePinnedSecretsListHandler;

        private readonly ILogger log;

        public SafePinnedSecrets(
            SafeExchangeDbContext dbContext,
            ITokenHelper tokenHelper,
            GlobalFilters globalFilters,
            IPermissionsManager permissionsManager,
            IOptions<PinnedSecretsConfiguration> config,
            ILogger<SafePinnedSecrets> log)
        {
            this.safeExchangePinnedSecretsHandler = new SafeExchangePinnedSecrets(
                dbContext, tokenHelper, globalFilters, permissionsManager, config);
            this.safeExchangePinnedSecretsListHandler = new SafeExchangePinnedSecretsList(
                dbContext, tokenHelper, globalFilters);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-PinnedSecrets")]
        public async Task<HttpResponseData> RunPinnedSecrets(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "put", "delete", Route = $"{Version}/pinnedsecrets/{{secretId}}")]
            HttpRequestData request,
            string secretId)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.safeExchangePinnedSecretsHandler.Run(request, secretId, principal, this.log);
        }

        [Function("SafeExchange-PinnedSecretsList")]
        public async Task<HttpResponseData> RunListPinnedSecrets(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/pinnedsecrets-list")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.safeExchangePinnedSecretsListHandler.RunList(request, principal, this.log);
        }
    }
}
```

- [ ] **Step 2: Verify the project builds**

Run: `dotnet build SafeExchange.sln`
Expected: build succeeds. (Failures most likely from `IPermissionsManager` or `PinnedSecretsConfiguration` not being available — they should both already be registered in DI from prior tasks except `PinnedSecretsConfiguration` which gets bound in the next task.)

- [ ] **Step 3: Commit**

```bash
git add SafeExchange.Functions/Functions/SafePinnedSecrets.cs
git commit -m "feat(pinned-secrets): HTTP triggers for /v2/pinnedsecrets/* routes"
```

---

## Task 26: Bind PinnedSecretsConfiguration in startup

**Files:**
- Modify: `SafeExchange.Core/SafeExchangeStartup.cs`

- [ ] **Step 1: Add the binding**

In `SafeExchangeStartup.cs`, find the line that binds `OrphanedSecretConfiguration`:

```csharp
services.Configure<OrphanedSecretConfiguration>(configuration.GetSection("OrphanedSecret"));
```

…and add immediately after:

```csharp
services.Configure<PinnedSecretsConfiguration>(configuration.GetSection("PinnedSecrets"));
```

- [ ] **Step 2: Verify the project builds and the full test suite still passes**

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj`
Expected: ALL PASS.

- [ ] **Step 3: Commit**

```bash
git add SafeExchange.Core/SafeExchangeStartup.cs
git commit -m "feat(pinned-secrets): bind PinnedSecretsConfiguration"
```

---

## Task 27: Add PinnedSecrets container to ARM template

**Files:**
- Modify: `deployment/current/arm/services-template.arm.json`

- [ ] **Step 1: Locate the PinnedGroups container block**

Open `deployment/current/arm/services-template.arm.json` and search for `"PinnedGroups"`. Inside the Cosmos DB account `containers` array there will be a JSON object that looks similar to:

```json
{
  "name": "PinnedGroups",
  "partitionKey": { "paths": [ "/PartitionKey" ], "kind": "Hash" }
}
```

(actual fields may vary — copy whatever shape the existing PinnedGroups entry uses).

- [ ] **Step 2: Add a sibling entry**

Insert a new container block right after the PinnedGroups one, identical except `"name": "PinnedSecrets"`. Keep the same `partitionKey` and `indexingPolicy` (if present) the existing container uses.

- [ ] **Step 3: Validate the JSON parses**

Run: `pwsh -c "Get-Content deployment/current/arm/services-template.arm.json | ConvertFrom-Json | Out-Null"`
Expected: no error.

- [ ] **Step 4: Commit**

```bash
git add deployment/current/arm/services-template.arm.json
git commit -m "chore(pinned-secrets): provision PinnedSecrets Cosmos container in ARM"
```

---

## Task 28: Update API documentation

**Files:**
- Modify: `docs/api-endpoints.md`

- [ ] **Step 1: Add a new section**

After the existing `## Groups` section (around line 77), insert:

```markdown
## Pinned Secrets

| Method | Path | Description |
|--------|------|-------------|
| GET | `/v2/pinnedsecrets-list` | List the caller's pinned secrets, newest-pin first |
| GET | `/v2/pinnedsecrets/{secretId}` | Check whether the caller has pinned `{secretId}` |
| PUT | `/v2/pinnedsecrets/{secretId}` | Pin a secret. Requires `Read`. Capped at `PinnedSecrets.MaxPinnedSecretsPerUser` (default 5) |
| DELETE | `/v2/pinnedsecrets/{secretId}` | Unpin a secret (idempotent) |

The list / single-GET responses always return a DTO with `exists`, `canRead`, `canWrite`, `canGrantAccess`, `canRevokeAccess`, and `tags`. When the secret has been deleted, `exists` is `false`; when the caller has lost access, all permission flags are `false` and `tags` is empty. The UI uses this to render deleted / no-access status badges.
```

- [ ] **Step 2: Commit**

```bash
git add docs/api-endpoints.md
git commit -m "docs(pinned-secrets): document /v2/pinnedsecrets endpoints"
```

---

## Task 29: Update data-model documentation

**Files:**
- Modify: `docs/data-model.md`

- [ ] **Step 1: Add the entity entry**

After the `### WebhookSubscription` block (or wherever the entity list ends, before `## Storage Strategy`), insert:

```markdown
### PinnedSecret

A per-user bookmark of a secret. Capped at `PinnedSecrets.MaxPinnedSecretsPerUser` per user (default 5). Independent of permission state — a pin row may outlive the user's access to the secret.

| Field | Description |
|-------|-------------|
| PartitionKey | Constant `"PSEC"` |
| UserId | Caller's user id (composite key) |
| SecretName | The pinned `ObjectMetadata.ObjectName` (composite key) |
| CreatedAt | UTC creation time; list is ordered DESC |
```

- [ ] **Step 2: Commit**

```bash
git add docs/data-model.md
git commit -m "docs(pinned-secrets): describe PinnedSecret entity"
```

---

## Task 30: Final verification + code review

- [ ] **Step 1: Run full test suite one more time**

Run: `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj`
Expected: ALL PASS.

- [ ] **Step 2: Build the Functions project**

Run: `dotnet build SafeExchange.Functions/SafeExchange.Functions.csproj`
Expected: build succeeds, no warnings introduced by this branch.

- [ ] **Step 3: Review the branch as a whole**

Use `git log --oneline main..HEAD` to confirm the commit history reads cleanly. Use the `superpowers:requesting-code-review` skill to dispatch a code-review subagent against the branch. Address feedback (if any) in fresh commits, then re-verify.

- [ ] **Step 4: Mark branch ready**

If review is clean, the feature is ready for the `superpowers:finishing-a-development-branch` skill to handle merge/PR.

---

## Self-Review Notes (from plan author)

**Spec coverage check:**
- ✅ Configuration class (Task 1) — spec § Configuration
- ✅ Entity + DbContext registration (Tasks 2–3) — spec § Data model
- ✅ Output DTO (Task 4) — spec § `PinnedSecretOutput`
- ✅ All 4 endpoints with auth gates (Tasks 6–22) — spec § API surface
- ✅ Cap-check semantics (Task 11) — spec § Cap-check semantics
- ✅ Idempotent PUT (Task 10) — spec § PUT step 5
- ✅ Stale-pin handling in single GET + list (Tasks 14–15, 21) — spec § Revised stale-pin handling
- ✅ Application-caller regression guards (Tasks 7, 23) — spec § standard prelude
- ✅ HTTP triggers (Task 25) — spec § File layout (new files)
- ✅ DI binding (Task 26) — spec § Configuration
- ✅ ARM template (Task 27) — spec § Migration
- ✅ Docs updates (Tasks 28–29) — spec § File layout (modified files)

**Items intentionally not covered:**
- Concurrent-PUT test for the same `(user, secret)` pair: omitted because `DbUtils.TryAddOrGetEntityAsync` is the same pattern used by `PinnedGroups` and is already covered by `PinnedGroupsTests.RegisterOnePinnedGroup_Simultaneously`. If desired, add a copy in a follow-up.
- Concurrent PUTs against the cap: documented in spec as acceptable trade-off (cap may be temporarily breached); not worth a test.
- `appsettings.json` / `local.settings.json` template change: the repo does not appear to check in user-facing config templates with feature defaults (the existing `Features` section is in the deployment ARM). If a template file exists for `appsettings.json` and is conventionally updated alongside features, add a single-line `"PinnedSecrets": { "MaxPinnedSecretsPerUser": 5 }` entry in Task 26.

**Placeholder scan:** None.

**Type consistency check:** `SafeExchangePinnedSecrets` constructor signature is consistent across Tasks 5 (fixture), 6 (impl), 25 (function trigger). `BuildDtoAsync` signature stable from Task 6 onward. `PinnedSecretOutput` shape unchanged from Task 4.
