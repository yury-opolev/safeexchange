# Phase 3: `Caller`, `ICallerResolver` and a host-agnostic global filter (implementation plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Give `SafeExchange.Core` a host-agnostic way to turn an authenticated `ClaimsPrincipal` into a
`Caller`, or into the same denial REST returns today. Add `Caller`, `CallerChannels`, `CallerDenial`,
`CallerResolution`, `ICallerResolver` and the default `CallerResolver`. Move the decision logic of
`GlobalAccessFilter` into Core as `GlobalAccessPolicy`, and turn `GlobalAccessFilter` into a thin HTTP wrapper
that writes the same response as before. REST behaviour does not change.

**Architecture:** New Core folder `SafeExchange.Core\Callers\` (namespace `SafeExchange.Core.Callers`, no
Functions types). `CallerResolver` runs today's handler preamble in today's order: global access-group gate
(`GlobalAccessPolicy`), then `SubjectHelper.GetSubjectInfoAsync`, then the registered-application gate.
`SafeExchange.Core.Functions` gets `CallerDenialHttpExtensions.ToResponseAsync` (denial to `HttpResponseData`
through `ActionResults.CreateResponseAsync`). `GlobalAccessFilter` uses both. `GlobalFilters`, `AdminGroupFilter`,
`IRequestFilter` and the six handlers do not change. `SafeExchangeStartup.ConfigureServices` registers
`ICallerResolver` as scoped.

**Tech stack:** .NET 10, EF Core (InMemory in tests), NUnit 4, Moq, `Microsoft.Extensions.Options` /
`DependencyInjection`.

**Master plan:** `docs/superpowers/plans/2026-11-21-mcp-endpoint.md` (ground rules, style, commands).
**Spec:** `docs/superpowers/specs/2026-11-21-mcp-endpoint-design.md` (§"`Caller` and caller resolution",
§Services contract, §Consumer migration note).

**Verified baseline:** this plan was run step by step in a scratch worktree created from `wt/p1` at
**`9841bff`** (phase 2 done). Results:
- every red step failed with the error quoted in this plan, and every green step passed;
- the solution builds with 0 errors; a `--no-incremental` build reports 705 warnings before and after, and the
  set of unique warnings (692) is identical, so there are no new warnings;
- contract + architecture + new tests: 235/235 (212 contract + 2 architecture + 7 + 2 + 12 new);
- the other EF InMemory / DB-free regression sets: 66/66 and 69/69.

The Cosmos emulator is **not** needed for this phase.

---

## Ground rules for this phase

1. **Run every command from the repository root**, i.e. the worktree that holds the phase 2 commit (HEAD was
   `9841bff` when this plan was verified). All paths are relative to that root. If a phase 2 review fix landed
   on top, carry on: nothing here depends on the exact commit, only on the files quoted below.
2. **The phase 1 contract tests (`SafeExchange.Tests\Contract\*`) are frozen.** Do not edit them. They must pass
   unchanged after every task. `GlobalFilterContractTests` pins the global-group and registered-app responses
   on every entry point of the six handlers, so it guards Task 2.
3. **Public APIs used by consumers stay the same:** the constructors and methods of `GlobalFilters`,
   `GlobalAccessFilter`, `AdminGroupFilter`, `IRequestFilter` and the handlers.
4. **Style** (master plan): file header `/// <summary>\n/// TypeName\n/// </summary>`, block namespace, `using`
   directives inside the namespace, `this.` prefix, constructor null-guards with `?? throw new
   ArgumentNullException(nameof(x))`, 4-space indent, NUnit `Assert.That(..., Is....)`. Add a comment only
   where a line needs explaining. The test project has no implicit usings, so every test file lists its usings.
5. **No commits until Task 6.** The phase is committed once, at the end, together with this plan file.
6. **No new build warnings.** Task 0 records the baseline and Task 6 compares against it.

## Decisions taken in this phase

### D1: How `Caller` is filled (no new lookups)

| Field | User token | Application token |
|---|---|---|
| `SubjectType` | `User` | `Application` |
| `SubjectId` | UPN (`ITokenHelper.GetUpn`), exactly what handlers use today | the registered application's **display name**, exactly what `SubjectHelper` returns and handlers use today for permissions and audit |
| `DisplayName` | `ITokenHelper.GetDisplayName(principal)` (token claims, no DB); falls back to the UPN when the token has no name claim | the registered display name (same as `SubjectId`) |
| `GroupIds` | empty | empty |
| `Channel` | argument (`CallerChannels.Rest` / `CallerChannels.Mcp`) | argument |
| `ClientLabel` | argument (MCP only, self-reported) | argument |

`GroupIds` stays empty on purpose. Nothing in Core reads it: `PermissionsManager` loads the Users row itself
and applies `Features.UseGroupsAuthorization` and `ConsentRequired` (`IsAuthorizedAsync`,
`GetReadableSecretsAsync`, `GetEffectivePermissions*`). Filling it would need a Users lookup on every call (the
global-group gate loads the row only when `AllowedGroups` is set), and a second source of group truth could
drift from the permission checks. The record's XML doc says so; a custom resolver may fill it. YAGNI: no DB
lookup is added, so behaviour cannot change.

### D2: The six handlers keep their preamble in this phase (question 5 of the brief)

Recommended and chosen: **do not** switch the handlers to `ICallerResolver` now.
- Phase 4 rewrites every handler into a thin adapter over a service that takes a `Caller`. Switching now would
  change the same six preambles twice and the six handler constructors (and `ContractTestBase.BuildHandlers`
  plumbing) twice.
- REST already runs the shared decision code from this phase on: `GlobalAccessFilter` delegates to
  `GlobalAccessPolicy`, so the 212 contract tests exercise it on every entry point.
- `CallerResolverTests` pins the resolver's equivalence to the preamble with the same scenarios as
  `GlobalFilterContractTests` (user ok, group member / non-member / missing user, registered / unregistered /
  disabled app, group gate before the registration gate).

Phase 4 replaces each preamble (global filter call + `SubjectHelper` + "Application is not registered or
disabled." check) with:

```csharp
var resolution = await this.callerResolver.ResolveAsync(principal, CallerChannels.Rest, null);
if (!resolution.IsAllowed)
{
    return await resolution.Denial.ToResponseAsync(request);
}

var caller = resolution.Caller;
```

The old `filterResult ?? request.CreateResponse(HttpStatusCode.NoContent)` branch is unreachable today
(`GlobalAccessFilter` always builds a response when it denies), so dropping it changes nothing. One
observable difference phase 4 must accept or avoid: the two global-group log lines would be written under the
`SafeExchange.Core.Callers.CallerResolver` category instead of `SafeExchange.Core.Filters.GlobalFilters` (same
level, same message template). Logs are not part of the REST contract.

### D3: Denial shape

`CallerDenial(HttpStatusCode StatusCode, string Status, string Error, string SubStatus)` with
`CallerDenial.Forbidden(error, subStatus = "")`. `System.Net.HttpStatusCode` is a BCL type, not a Functions
type, so Core stays host-agnostic; MCP (phase 6) maps the same value. `SubStatus` defaults to `""` because
`BaseResponseObject.SubStatus` defaults to `""` and is always serialized (`"subStatus":""`).
`CallerDenialHttpExtensions.ToResponseAsync` writes `new BaseResponseObject<object> { Status, SubStatus, Error }`
through `ActionResults.CreateResponseAsync`, i.e. the same call `ActionResults.ForbiddenAsync` and the old
`GlobalAccessFilter` made, so the bytes are identical (pinned by `CallerDenialHttpExtensionsTests` and by the
contract tests). The two messages become constants: `GlobalAccessPolicy.NotAMemberError` and
`CallerResolver.ApplicationNotRegisteredError`. The handlers keep their own literals (D2).

### D4: `GlobalAccessPolicy` keeps the filter's exact semantics

Copied from `GlobalAccessFilter`, not redesigned: `AllowedGroups` split on `","` with `RemoveEmptyEntries` (no
trimming); nothing configured → allowed with no DB access and no log; otherwise the Users row is looked up by
`AadUpn.Equals(GetUpn(principal))` (an app token has no UPN, so an app is always denied while groups are
configured, which the contract tests pin); group ids compared with `string.Equals` (ordinal); same two
`LogInformation` templates; no log when the user row is missing. Like the filter, it is built per call from
the configuration snapshot, so `IOptionsMonitor.CurrentValue` changes apply on the next request.
`GlobalFilters` keeps creating `GlobalAccessFilter` per call with its own `ILogger<GlobalFilters>`, so REST log
lines keep their category. The policy takes an optional `CancellationToken` and passes it to the Users query
(REST passes none).

### D5: Registration and replacement

`services.AddScoped<ICallerResolver, CallerResolver>()` in `SafeExchangeStartup.ConfigureServices`, next to
the other scoped managers: `CallerResolver` needs the scoped `SafeExchangeDbContext`. Its other dependencies
(`IOptionsMonitor<GloballyAllowedGroupsConfiguration>`, `ITokenHelper`, `ILogger<>`) are singletons. A consumer
with different claims registers its own `ICallerResolver` **after** `ConfigureServices`; the last registration
wins for `GetService`. The registration test builds a provider with `ValidateScopes = true` and resolves it in
a scope, proving the graph is complete. `CallerResolver` is `sealed`; consumers implement the interface.

### D6: Scope kept minimal

- `AdminGroupFilter` and `GlobalFilters.GetAdminFilterResultAsync` stay as they are (MCP needs no admin gate).
- `SubjectHelper` is unchanged (no `CancellationToken` added).
- `ResolveAsync` throws `ArgumentNullException` for a null principal and `ArgumentException` for an empty
  channel. This is new API, so no existing behaviour changes.

### D7: Test filter (deviation from the brief)

The brief's filter `FullyQualifiedName~Caller|FullyQualifiedName~GlobalAccess` also matches unrelated tests
whose names contain "Caller", for example `PinnedSecretsTests.List_ApplicationCaller_Returns403`. That fixture
needs the Cosmos emulator. This plan filters on the exact new class names instead:

```powershell
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.Contract|FullyQualifiedName~SafeExchange.Tests.Architecture|FullyQualifiedName~SafeExchange.Tests.CallerResolverTests|FullyQualifiedName~SafeExchange.Tests.CallerDenialHttpExtensionsTests|FullyQualifiedName~SafeExchange.Tests.GlobalAccessPolicyTests"
```

## File map

Create:
- `SafeExchange.Core\Callers\CallerDenial.cs`: the denial record.
- `SafeExchange.Core\Callers\GlobalAccessPolicy.cs`: host-agnostic global access-group gate.
- `SafeExchange.Core\Callers\Caller.cs`: the `Caller` record.
- `SafeExchange.Core\Callers\CallerChannels.cs`: `"rest"`, `"mcp"`.
- `SafeExchange.Core\Callers\CallerResolution.cs`: `Ok(Caller)` | `Denied(CallerDenial)`.
- `SafeExchange.Core\Callers\ICallerResolver.cs`: the interface.
- `SafeExchange.Core\Callers\CallerResolver.cs`: default implementation.
- `SafeExchange.Core.Functions\Utilities\CallerDenialHttpExtensions.cs`: `CallerDenial` → `HttpResponseData`.
- `SafeExchange.Tests\Tests\GlobalAccessPolicyTests.cs`
- `SafeExchange.Tests\Tests\CallerDenialHttpExtensionsTests.cs`
- `SafeExchange.Tests\Tests\CallerResolverTests.cs`

Modify:
- `SafeExchange.Core.Functions\Requests\Filters\GlobalAccessFilter.cs`: thin wrapper over the policy.
- `SafeExchange.Core\SafeExchangeStartup.cs`: register `ICallerResolver`.
- `docs\architecture.md`: project structure section.

---

### Task 0: Record the baseline

**Files:** none.

- [ ] **Step 1: Confirm the starting commit and a clean tree**

Run: `git --no-pager log --oneline -1; git status --short`
Expected: `9841bff refactor: move Functions-typed code from SafeExchange.Core into SafeExchange.Core.Functions`
or a later phase 2 review commit (at the time of writing: `18a3dca docs(plan): track deferred review findings`,
docs only), and no status lines other than this plan file
(`?? docs/superpowers/plans/2026-11-21-mcp-p3-caller.md`) if it is not committed yet.

- [ ] **Step 2: Full rebuild and note the warning count**

Run: `dotnet build SafeExchange.slnx --nologo -v q --no-incremental`
Expected: the last lines are `705 Warning(s)` and `0 Error(s)`. Write the number down; Task 6 must report the
same number. (If a review fix changed the count, use the new number.)

- [ ] **Step 3: Run the baseline test set**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchange.Tests.Contract|FullyQualifiedName~SafeExchange.Tests.Architecture"`
Expected: `Passed!  - Failed:     0, Passed:   214` (212 contract + 2 architecture). If a review fix added
contract tests, use that count + 21 wherever this plan says 235.

---

### Task 1: `GlobalAccessPolicy` and `CallerDenial` (Core)

**Files:**
- Create: `SafeExchange.Tests\Tests\GlobalAccessPolicyTests.cs`
- Create: `SafeExchange.Core\Callers\CallerDenial.cs`
- Create: `SafeExchange.Core\Callers\GlobalAccessPolicy.cs`

- [ ] **Step 1: Write the failing tests**

Create `SafeExchange.Tests\Tests\GlobalAccessPolicyTests.cs`:

```csharp
/// <summary>
/// GlobalAccessPolicyTests
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Model;
    using System;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    [TestFixture]
    public class GlobalAccessPolicyTests
    {
        private const string TenantId = "00000000-0000-0000-0000-000000000001";

        private const string UserUpn = "user@policy.test";

        private const string FirstGroup = "00000000-0000-0000-0000-0000000000d1";

        private const string SecondGroup = "00000000-0000-0000-0000-0000000000d3";

        private const string OtherGroup = "00000000-0000-0000-0000-0000000000d2";

        private SafeExchangeDbContext dbContext = null!;

        private ITokenHelper tokenHelper = null!;

        private RecordingLogger log = null!;

        [SetUp]
        public void SetUp()
        {
            var options = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseInMemoryDatabase($"{nameof(GlobalAccessPolicyTests)}-{Guid.NewGuid()}")
                .Options;
            this.dbContext = new SafeExchangeDbContext(options);
            this.tokenHelper = new TokenHelper(new ConfigurationBuilder().Build(), NullLogger<TokenHelper>.Instance);
            this.log = new RecordingLogger();
        }

        [TearDown]
        public void TearDown()
        {
            this.dbContext.Database.EnsureDeleted();
            this.dbContext.Dispose();
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(",")]
        public async Task NoGroupConfigured_AllowsEveryone_WithoutLogging(string? allowedGroups)
        {
            var denial = await this.Policy(allowedGroups).EvaluateAsync(UserPrincipal(UserUpn), this.dbContext);

            Assert.That(denial, Is.Null);
            Assert.That(this.log.Entries, Is.Empty);
        }

        [Test]
        public async Task GroupConfigured_UserNotInUsers_ForbiddenWithoutLogging()
        {
            var denial = await this.Policy(FirstGroup).EvaluateAsync(UserPrincipal(UserUpn), this.dbContext);

            AssertNotAMember(denial);
            Assert.That(this.log.Entries, Is.Empty);
        }

        [Test]
        public async Task GroupConfigured_UserInSecondAllowedGroup_AllowedAndLogged()
        {
            await this.SeedUserAsync(UserUpn, "tid-member", OtherGroup, SecondGroup);

            var denial = await this.Policy($"{FirstGroup},{SecondGroup}").EvaluateAsync(UserPrincipal(UserUpn), this.dbContext);

            Assert.That(denial, Is.Null);
            Assert.That(this.log.Entries.Select(e => (e.Level, e.Message)), Is.EqualTo(new[]
            {
                (LogLevel.Information, $"User (tid tid-member) is a member of global access group '{SecondGroup}', authorized."),
            }));
        }

        [Test]
        public async Task GroupConfigured_UserWithoutAllowedGroup_ForbiddenAndLogged()
        {
            await this.SeedUserAsync(UserUpn, "tid-outsider", OtherGroup);

            var denial = await this.Policy(FirstGroup).EvaluateAsync(UserPrincipal(UserUpn), this.dbContext);

            AssertNotAMember(denial);
            Assert.That(this.log.Entries.Select(e => (e.Level, e.Message)), Is.EqualTo(new[]
            {
                (LogLevel.Information, "User (tid tid-outsider) is not a member of any global access group, unauthorized."),
            }));
        }

        [Test]
        public async Task GroupConfigured_ApplicationPrincipal_Forbidden()
        {
            await this.SeedUserAsync(UserUpn, "tid-member", FirstGroup);

            var denial = await this.Policy(FirstGroup).EvaluateAsync(AppPrincipal(), this.dbContext);

            AssertNotAMember(denial);
        }

        private static void AssertNotAMember(CallerDenial? denial)
        {
            Assert.That(denial, Is.Not.Null);
            Assert.That(denial!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(denial.Status, Is.EqualTo("forbidden"));
            Assert.That(denial.Error, Is.EqualTo("Not a member of a global group."));
            Assert.That(denial.SubStatus, Is.EqualTo(string.Empty));
        }

        private static ClaimsPrincipal UserPrincipal(string upn)
            => new(new ClaimsIdentity(new[]
            {
                new Claim("upn", upn),
                new Claim("oid", "00000000-0000-0000-0000-0000000000a1"),
                new Claim("tid", TenantId),
            }));

        private static ClaimsPrincipal AppPrincipal()
            => new(new ClaimsIdentity(new[]
            {
                new Claim("azp", "00000000-0000-0000-0000-0000000000c1"),
                new Claim("azpacr", "1"),
                new Claim("oid", "00000000-0000-0000-0000-0000000000b1"),
                new Claim("tid", TenantId),
            }));

        private GlobalAccessPolicy Policy(string? allowedGroups)
            => new(new GloballyAllowedGroupsConfiguration { AllowedGroups = allowedGroups! }, this.tokenHelper, this.log);

        private async Task SeedUserAsync(string upn, string telemetryId, params string[] groupIds)
        {
            this.dbContext.Users.Add(new User("Policy User", "00000000-0000-0000-0000-0000000000a1", TenantId, upn, upn)
            {
                TelemetryId = telemetryId,
                Groups = groupIds.Select(g => new UserGroup { AadGroupId = g }).ToList(),
            });
            await this.dbContext.SaveChangesAsync();
        }
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.GlobalAccessPolicyTests"`
Expected: the build fails with
`GlobalAccessPolicyTests.cs(13,29): error CS0234: The type or namespace name 'Callers' does not exist in the namespace 'SafeExchange.Core'`
plus `error CS0246` for `CallerDenial` and `GlobalAccessPolicy`.

- [ ] **Step 3: Create the denial record**

Create the folder: `New-Item -ItemType Directory SafeExchange.Core\Callers | Out-Null`

Create `SafeExchange.Core\Callers\CallerDenial.cs`:

```csharp
/// <summary>
/// CallerDenial
/// </summary>

namespace SafeExchange.Core.Callers
{
    using System.Net;

    /// <summary>
    /// Why a caller may not use the API, in the shape REST returns today: the HTTP status code and the
    /// <see cref="BaseResponseObject{T}"/> <c>status</c>, <c>subStatus</c> and <c>error</c> values.
    /// </summary>
    public sealed record CallerDenial(HttpStatusCode StatusCode, string Status, string Error, string SubStatus)
    {
        public static CallerDenial Forbidden(string error, string subStatus = "")
            => new(HttpStatusCode.Forbidden, "forbidden", error, subStatus);
    }
}
```

- [ ] **Step 4: Create the policy (logic copied from `GlobalAccessFilter`, see D4)**

Create `SafeExchange.Core\Callers\GlobalAccessPolicy.cs`:

```csharp
/// <summary>
/// GlobalAccessPolicy
/// </summary>

namespace SafeExchange.Core.Callers
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Configuration;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Claims;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Host-agnostic global access-group gate (<c>GlobalAllowLists:AllowedGroups</c>). When groups are
    /// configured, only a user that exists in Users and is a member of one of them may call the API.
    /// </summary>
    public sealed class GlobalAccessPolicy
    {
        public const string NotAMemberError = "Not a member of a global group.";

        private readonly IReadOnlyList<string> accessGroupIds;

        private readonly ITokenHelper tokenHelper;

        private readonly ILogger log;

        public GlobalAccessPolicy(GloballyAllowedGroupsConfiguration groupsConfiguration, ITokenHelper tokenHelper, ILogger log)
        {
            if (groupsConfiguration is null)
            {
                throw new ArgumentNullException(nameof(groupsConfiguration));
            }

            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.log = log ?? throw new ArgumentNullException(nameof(log));

            var allowedGroupsString = groupsConfiguration.AllowedGroups;
            this.accessGroupIds = string.IsNullOrEmpty(allowedGroupsString)
                ? Array.Empty<string>()
                : allowedGroupsString.Split(",", StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// Returns <c>null</c> when the caller passes the gate, otherwise the denial.
        /// </summary>
        public async Task<CallerDenial?> EvaluateAsync(ClaimsPrincipal principal, SafeExchangeDbContext dbContext, CancellationToken cancellationToken = default)
        {
            if (this.accessGroupIds.Count == 0)
            {
                return null;
            }

            var userUpn = this.tokenHelper.GetUpn(principal);
            var existingUser = await dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn.Equals(userUpn), cancellationToken);
            if (existingUser is null)
            {
                return CallerDenial.Forbidden(NotAMemberError);
            }

            foreach (var groupId in this.accessGroupIds)
            {
                if (existingUser.Groups.Any(g => g.AadGroupId.Equals(groupId)))
                {
                    this.log.LogInformation("User (tid {TelemetryId}) is a member of global access group '{GroupId}', authorized.", existingUser.TelemetryId, groupId);
                    return null;
                }
            }

            this.log.LogInformation("User (tid {TelemetryId}) is not a member of any global access group, unauthorized.", existingUser.TelemetryId);
            return CallerDenial.Forbidden(NotAMemberError);
        }
    }
}
```

- [ ] **Step 5: Run the tests and watch them pass**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.GlobalAccessPolicyTests"`
Expected: `Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7`

---

### Task 2: `GlobalAccessFilter` becomes a thin wrapper

**Files:**
- Create: `SafeExchange.Tests\Tests\CallerDenialHttpExtensionsTests.cs`
- Create: `SafeExchange.Core.Functions\Utilities\CallerDenialHttpExtensions.cs`
- Modify: `SafeExchange.Core.Functions\Requests\Filters\GlobalAccessFilter.cs`

- [ ] **Step 1: Write the failing tests**

The fixture derives from `ContractTestBase` only to reuse its production serializer setup, `Request(...)` and
`ContractAssert`; it does not touch the contract tests.

Create `SafeExchange.Tests\Tests\CallerDenialHttpExtensionsTests.cs`:

```csharp
/// <summary>
/// CallerDenialHttpExtensionsTests
/// </summary>

namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Callers;
    using SafeExchange.Tests.Contract;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>
    /// Uses the contract fixture plumbing (production serializer settings) so the bytes compare
    /// with what REST writes today.
    /// </summary>
    [TestFixture]
    public class CallerDenialHttpExtensionsTests : ContractTestBase
    {
        [Test]
        public async Task ToResponseAsync_Forbidden_IsByteIdenticalToActionResultsForbidden()
        {
            var expected = await ActionResults.ForbiddenAsync(Request("get"), "Not a member of a global group.", "consent_required");

            var actual = await CallerDenial.Forbidden("Not a member of a global group.", "consent_required").ToResponseAsync(Request("get"));

            Assert.That(actual.StatusCode, Is.EqualTo(expected.StatusCode));
            Assert.That(ContractAssert.HeaderLines(actual), Is.EqualTo(ContractAssert.HeaderLines(expected)));
            Assert.That(ContractAssert.ReadBodyBytes(actual), Is.EqualTo(ContractAssert.ReadBodyBytes(expected)));
        }

        [Test]
        public async Task ToResponseAsync_WritesStatusCodeAndAllFields()
        {
            var denial = new CallerDenial(HttpStatusCode.Unauthorized, "unauthorized", "Token missing.", string.Empty);

            var response = await denial.ToResponseAsync(Request("post"));

            ContractAssert.Json(response, HttpStatusCode.Unauthorized, """
                {"status":"unauthorized","subStatus":"","error":"Token missing."}
                """);
        }
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.CallerDenialHttpExtensionsTests"`
Expected: the build fails with
`CallerDenialHttpExtensionsTests.cs(26,110): error CS1061: 'CallerDenial' does not contain a definition for 'ToResponseAsync' and no accessible extension method 'ToResponseAsync' ...`
(and the same at line 38).

- [ ] **Step 3: Create the HTTP extension**

Create `SafeExchange.Core.Functions\Utilities\CallerDenialHttpExtensions.cs`:

```csharp
/// <summary>
/// CallerDenialHttpExtensions
/// </summary>

namespace SafeExchange.Core.Callers
{
    using Microsoft.Azure.Functions.Worker.Http;
    using System;
    using System.Threading.Tasks;

    public static class CallerDenialHttpExtensions
    {
        /// <summary>
        /// Writes the denial as today's REST response: its status code and a
        /// <see cref="BaseResponseObject{T}"/> body with its status, subStatus and error.
        /// </summary>
        public static Task<HttpResponseData> ToResponseAsync(this CallerDenial denial, HttpRequestData request)
        {
            if (denial is null)
            {
                throw new ArgumentNullException(nameof(denial));
            }

            return ActionResults.CreateResponseAsync(
                request,
                denial.StatusCode,
                new BaseResponseObject<object> { Status = denial.Status, SubStatus = denial.SubStatus, Error = denial.Error });
        }
    }
}
```

- [ ] **Step 4: Run the new tests and watch them pass**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.CallerDenialHttpExtensionsTests"`
Expected: `Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2`

- [ ] **Step 5: Replace `GlobalAccessFilter` with the wrapper**

Replace the **whole content** of `SafeExchange.Core.Functions\Requests\Filters\GlobalAccessFilter.cs` with the
code below (keep the file's leading UTF-8 BOM if your editor preserves it; either way the code is the same).
The public constructor and `GetFilterResultAsync` keep their signatures; the constructor's null
checks (same order, same parameter names) now happen in `GlobalAccessPolicy`.

```csharp
/// <summary>
/// SafeExchange
/// </summary>

namespace SafeExchange.Core.Filters
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Configuration;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class GlobalAccessFilter : IRequestFilter
    {
        private readonly GlobalAccessPolicy policy;

        public GlobalAccessFilter(GloballyAllowedGroupsConfiguration groupsConfiguration, ITokenHelper tokenHelper, ILogger log)
        {
            this.policy = new GlobalAccessPolicy(groupsConfiguration, tokenHelper, log);
        }

        public async ValueTask<(bool shouldReturn, HttpResponseData? response)> GetFilterResultAsync(HttpRequestData req, ClaimsPrincipal principal, SafeExchangeDbContext dbContext)
        {
            var denial = await this.policy.EvaluateAsync(principal, dbContext);
            if (denial is null)
            {
                return (shouldReturn: false, response: null);
            }

            return (shouldReturn: true, response: await denial.ToResponseAsync(req));
        }
    }
}
```

`GlobalFilters.cs` does not change: it still builds a `GlobalAccessFilter` per call from
`groupsConfiguration.CurrentValue` with its `ILogger<GlobalFilters>`.

- [ ] **Step 6: Run the contract tests (the REST guard) plus the new tests**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.Contract|FullyQualifiedName~SafeExchange.Tests.CallerDenialHttpExtensionsTests|FullyQualifiedName~SafeExchange.Tests.GlobalAccessPolicyTests"`
Expected: `Passed!  - Failed:     0, Passed:   221, Skipped:     0, Total:   221` (212 + 2 + 7).
`GlobalFilterContractTests` (66 cases: 6 scenarios × 11 entry points) and
`SecretMetaContractTests.GlobalAccessGroupConfigured_CallerNotMember_Forbidden` pin the unchanged bodies.

---

### Task 3: `Caller`, `CallerResolution`, `ICallerResolver`, `CallerResolver`

**Files:**
- Create: `SafeExchange.Tests\Tests\CallerResolverTests.cs`
- Create: `SafeExchange.Core\Callers\Caller.cs`
- Create: `SafeExchange.Core\Callers\CallerChannels.cs`
- Create: `SafeExchange.Core\Callers\CallerResolution.cs`
- Create: `SafeExchange.Core\Callers\ICallerResolver.cs`
- Create: `SafeExchange.Core\Callers\CallerResolver.cs`

- [ ] **Step 1: Write the failing tests**

The scenarios mirror `GlobalFilterContractTests` (same principal shapes as `ContractTestBase`: user =
`upn`/`oid`/`tid`; app-only v2 token = `azp` + `azpacr`, no scope), with the real `TokenHelper` so app tokens
are classified exactly as in production. The groups configuration is returned through a mocked
`IOptionsMonitor` that reads a field, so a test can swap it between calls.

Create `SafeExchange.Tests\Tests\CallerResolverTests.cs`:

```csharp
/// <summary>
/// CallerResolverTests
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Model;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    [TestFixture]
    public class CallerResolverTests
    {
        private const string TenantId = "00000000-0000-0000-0000-000000000001";

        private const string UserUpn = "user@resolver.test";

        private const string AgentClientId = "00000000-0000-0000-0000-0000000000c1";

        private const string AgentDisplayName = "resolver-agent";

        private const string AllowedGroup = "00000000-0000-0000-0000-0000000000d1";

        private const string OtherGroup = "00000000-0000-0000-0000-0000000000d2";

        private SafeExchangeDbContext dbContext = null!;

        private GloballyAllowedGroupsConfiguration groups = null!;

        private CallerResolver resolver = null!;

        [SetUp]
        public void SetUp()
        {
            var options = new DbContextOptionsBuilder<SafeExchangeDbContext>()
                .UseInMemoryDatabase($"{nameof(CallerResolverTests)}-{Guid.NewGuid()}")
                .Options;
            this.dbContext = new SafeExchangeDbContext(options);
            this.groups = new GloballyAllowedGroupsConfiguration { AllowedGroups = string.Empty };

            var tokenHelper = new TokenHelper(new ConfigurationBuilder().Build(), NullLogger<TokenHelper>.Instance);
            var groupsMonitor = new Mock<IOptionsMonitor<GloballyAllowedGroupsConfiguration>>();
            groupsMonitor.Setup(m => m.CurrentValue).Returns(() => this.groups);
            this.resolver = new CallerResolver(groupsMonitor.Object, tokenHelper, this.dbContext, NullLogger<CallerResolver>.Instance);
        }

        [TearDown]
        public void TearDown()
        {
            this.dbContext.Database.EnsureDeleted();
            this.dbContext.Dispose();
        }

        [Test]
        public async Task User_ResolvesToUserCaller_WithTokenDisplayName()
        {
            var resolution = await this.resolver.ResolveAsync(UserPrincipal(UserUpn, "Resolver User"), CallerChannels.Rest, null);

            AssertCaller(resolution, SubjectType.User, UserUpn, "Resolver User", CallerChannels.Rest, null);
        }

        [Test]
        public async Task User_WithoutNameClaim_DisplayNameFallsBackToUpn()
        {
            var resolution = await this.resolver.ResolveAsync(UserPrincipal(UserUpn), CallerChannels.Mcp, "copilot-cli/1.0.114");

            AssertCaller(resolution, SubjectType.User, UserUpn, UserUpn, CallerChannels.Mcp, "copilot-cli/1.0.114");
        }

        [Test]
        public async Task RegisteredApplication_ResolvesToApplicationCaller_NamedAfterTheRegistration()
        {
            await this.SeedApplicationAsync(enabled: true);

            var resolution = await this.resolver.ResolveAsync(AppPrincipal(AgentClientId), CallerChannels.Mcp, "agent/2.0");

            AssertCaller(resolution, SubjectType.Application, AgentDisplayName, AgentDisplayName, CallerChannels.Mcp, "agent/2.0");
        }

        [Test]
        public async Task UnregisteredApplication_IsDenied()
        {
            var resolution = await this.resolver.ResolveAsync(AppPrincipal(AgentClientId), CallerChannels.Rest, null);

            AssertDenied(resolution, "Application is not registered or disabled.");
        }

        [Test]
        public async Task DisabledApplication_IsDenied()
        {
            await this.SeedApplicationAsync(enabled: false);

            var resolution = await this.resolver.ResolveAsync(AppPrincipal(AgentClientId), CallerChannels.Mcp, null);

            AssertDenied(resolution, "Application is not registered or disabled.");
        }

        [Test]
        public async Task GroupConfigured_MemberUser_Resolves()
        {
            this.groups.AllowedGroups = AllowedGroup;
            await this.SeedUserAsync(OtherGroup, AllowedGroup);

            var resolution = await this.resolver.ResolveAsync(UserPrincipal(UserUpn), CallerChannels.Rest, null);

            AssertCaller(resolution, SubjectType.User, UserUpn, UserUpn, CallerChannels.Rest, null);
        }

        [Test]
        public async Task GroupConfigured_NonMemberUser_IsDenied()
        {
            this.groups.AllowedGroups = AllowedGroup;
            await this.SeedUserAsync(OtherGroup);

            var resolution = await this.resolver.ResolveAsync(UserPrincipal(UserUpn), CallerChannels.Rest, null);

            AssertDenied(resolution, "Not a member of a global group.");
        }

        [Test]
        public async Task GroupConfigured_UserNotInUsers_IsDenied()
        {
            this.groups.AllowedGroups = AllowedGroup;

            var resolution = await this.resolver.ResolveAsync(UserPrincipal(UserUpn), CallerChannels.Rest, null);

            AssertDenied(resolution, "Not a member of a global group.");
        }

        [Test]
        public async Task GroupConfigured_RegisteredApplication_IsDeniedByTheGroupGate()
        {
            await this.SeedApplicationAsync(enabled: true);
            this.groups.AllowedGroups = AllowedGroup;

            var resolution = await this.resolver.ResolveAsync(AppPrincipal(AgentClientId), CallerChannels.Mcp, null);

            AssertDenied(resolution, "Not a member of a global group.");
        }

        [Test]
        public async Task GroupConfigured_UnregisteredApplication_GroupGateRunsFirst()
        {
            this.groups.AllowedGroups = AllowedGroup;

            var resolution = await this.resolver.ResolveAsync(AppPrincipal(AgentClientId), CallerChannels.Rest, null);

            AssertDenied(resolution, "Not a member of a global group.");
        }

        [Test]
        public async Task GroupConfiguration_IsReadOnEveryCall()
        {
            Assert.That((await this.resolver.ResolveAsync(UserPrincipal(UserUpn), CallerChannels.Rest, null)).IsAllowed, Is.True);

            this.groups = new GloballyAllowedGroupsConfiguration { AllowedGroups = AllowedGroup };

            AssertDenied(await this.resolver.ResolveAsync(UserPrincipal(UserUpn), CallerChannels.Rest, null), "Not a member of a global group.");
        }

        private static void AssertCaller(CallerResolution resolution, SubjectType subjectType, string subjectId, string displayName, string channel, string? clientLabel)
        {
            Assert.That(resolution.IsAllowed, Is.True, $"Denied: {resolution.Denial}");
            Assert.That(resolution.Denial, Is.Null);
            var caller = resolution.Caller!;
            Assert.That(caller.SubjectType, Is.EqualTo(subjectType));
            Assert.That(caller.SubjectId, Is.EqualTo(subjectId));
            Assert.That(caller.DisplayName, Is.EqualTo(displayName));
            Assert.That(caller.GroupIds, Is.Empty);
            Assert.That(caller.Channel, Is.EqualTo(channel));
            Assert.That(caller.ClientLabel, Is.EqualTo(clientLabel));
        }

        private static void AssertDenied(CallerResolution resolution, string error)
        {
            Assert.That(resolution.IsAllowed, Is.False);
            Assert.That(resolution.Caller, Is.Null);
            Assert.That(resolution.Denial, Is.EqualTo(new CallerDenial(HttpStatusCode.Forbidden, "forbidden", error, string.Empty)));
        }

        private static ClaimsPrincipal UserPrincipal(string upn, string? name = null)
        {
            var claims = new List<Claim>
            {
                new("upn", upn),
                new("oid", "00000000-0000-0000-0000-0000000000a1"),
                new("tid", TenantId),
            };
            if (name is not null)
            {
                claims.Add(new Claim("name", name));
            }

            return new ClaimsPrincipal(new ClaimsIdentity(claims));
        }

        // App-only (client-credentials) v2 token shape: azp + azpacr and no scope claim.
        private static ClaimsPrincipal AppPrincipal(string clientId)
            => new(new ClaimsIdentity(new[]
            {
                new Claim("azp", clientId),
                new Claim("azpacr", "1"),
                new Claim("oid", "00000000-0000-0000-0000-0000000000b1"),
                new Claim("tid", TenantId),
            }));

        private async Task SeedApplicationAsync(bool enabled)
        {
            this.dbContext.Applications.Add(new Application
            {
                Id = Guid.NewGuid().ToString(),
                PartitionKey = Application.DefaultPartitionKey,
                Enabled = enabled,
                DisplayName = AgentDisplayName,
                ContactEmail = "agent-owner@resolver.test",
                AadClientId = AgentClientId,
                AadTenantId = TenantId,
                CreatedAt = DateTime.UnixEpoch,
                CreatedBy = "resolver-test",
                ModifiedAt = DateTime.MinValue,
                ModifiedBy = string.Empty,
            });
            await this.dbContext.SaveChangesAsync();
        }

        private async Task SeedUserAsync(params string[] groupIds)
        {
            this.dbContext.Users.Add(new User("Resolver User", "00000000-0000-0000-0000-0000000000a1", TenantId, UserUpn, UserUpn)
            {
                Groups = groupIds.Select(g => new UserGroup { AadGroupId = g }).ToList(),
            });
            await this.dbContext.SaveChangesAsync();
        }
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.CallerResolverTests"`
Expected: the build fails with `error CS0246: The type or namespace name 'CallerResolver' could not be found`
and `error CS0246: The type or namespace name 'CallerResolution' could not be found` (the compiler may list
other missing types too).

- [ ] **Step 3: Create the `Caller` record**

Create `SafeExchange.Core\Callers\Caller.cs`:

```csharp
/// <summary>
/// Caller
/// </summary>

namespace SafeExchange.Core.Callers
{
    using SafeExchange.Core.Model;
    using System.Collections.Generic;

    /// <summary>
    /// The authenticated subject a service call is made for.
    /// </summary>
    /// <param name="SubjectType">User or Application.</param>
    /// <param name="SubjectId">The id permissions and audit use: the UPN for users, the registered
    /// application's display name for applications (as <see cref="SubjectHelper"/> resolves it).</param>
    /// <param name="DisplayName">The token's display name for users (the UPN when the token has none),
    /// the registered display name for applications.</param>
    /// <param name="GroupIds">Group ids of the caller, when a resolver knows them. The default
    /// <see cref="CallerResolver"/> leaves this empty: permission checks read group memberships from the
    /// Users row themselves (<c>IPermissionsManager</c>), so the resolver adds no lookup for them.</param>
    /// <param name="Channel">Where the call came from, one of <see cref="CallerChannels"/>.</param>
    /// <param name="ClientLabel">Self-reported client label (MCP only); carries no authority.</param>
    public sealed record Caller(
        SubjectType SubjectType,
        string SubjectId,
        string DisplayName,
        IReadOnlyList<string> GroupIds,
        string Channel,
        string? ClientLabel);
}
```

- [ ] **Step 4: Create the channel constants**

Create `SafeExchange.Core\Callers\CallerChannels.cs`:

```csharp
/// <summary>
/// CallerChannels
/// </summary>

namespace SafeExchange.Core.Callers
{
    public static class CallerChannels
    {
        public const string Rest = "rest";

        public const string Mcp = "mcp";
    }
}
```

- [ ] **Step 5: Create the resolution type**

`IsAllowed` is an auto-property set in the constructor, so the `MemberNotNullWhen` attributes give callers
flow analysis (`resolution.Denial` is non-null after `!resolution.IsAllowed`) without compiler warnings.

Create `SafeExchange.Core\Callers\CallerResolution.cs`:

```csharp
/// <summary>
/// CallerResolution
/// </summary>

namespace SafeExchange.Core.Callers
{
    using System;
    using System.Diagnostics.CodeAnalysis;

    /// <summary>
    /// Either the resolved <see cref="Callers.Caller"/> or the <see cref="CallerDenial"/> to return instead.
    /// </summary>
    public sealed class CallerResolution
    {
        private CallerResolution(Caller? caller, CallerDenial? denial)
        {
            this.Caller = caller;
            this.Denial = denial;
            this.IsAllowed = caller is not null;
        }

        [MemberNotNullWhen(true, nameof(Caller))]
        [MemberNotNullWhen(false, nameof(Denial))]
        public bool IsAllowed { get; }

        public Caller? Caller { get; }

        public CallerDenial? Denial { get; }

        public static CallerResolution Ok(Caller caller)
            => new(caller ?? throw new ArgumentNullException(nameof(caller)), null);

        public static CallerResolution Denied(CallerDenial denial)
            => new(null, denial ?? throw new ArgumentNullException(nameof(denial)));
    }
}
```

- [ ] **Step 6: Create the interface**

Create `SafeExchange.Core\Callers\ICallerResolver.cs`:

```csharp
/// <summary>
/// ICallerResolver
/// </summary>

namespace SafeExchange.Core.Callers
{
    using System.Security.Claims;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Turns an authenticated principal into a <see cref="Caller"/>, or a denial. Hosts whose tokens
    /// carry different claims register their own implementation after <c>SafeExchangeStartup.ConfigureServices</c>.
    /// </summary>
    public interface ICallerResolver
    {
        Task<CallerResolution> ResolveAsync(ClaimsPrincipal principal, string channel, string? clientLabel, CancellationToken cancellationToken = default);
    }
}
```

- [ ] **Step 7: Create the default resolver**

It calls `SubjectHelper.GetSubjectInfoAsync` exactly once, so `TokenHelper.IsUserToken` writes its
"Principal is authenticated as ..." trace once per request, as today. It does not call `IsUserToken` again.

Create `SafeExchange.Core\Callers\CallerResolver.cs`:

```csharp
/// <summary>
/// CallerResolver
/// </summary>

namespace SafeExchange.Core.Callers
{
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Model;
    using System;
    using System.Security.Claims;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Default <see cref="ICallerResolver"/>: the REST handlers' preamble, in the same order. First the global
    /// access-group gate, then <see cref="SubjectHelper.GetSubjectInfoAsync"/>, then the registered-application
    /// gate (only enabled registrations resolve, which makes disabling an application its kill switch).
    /// </summary>
    public sealed class CallerResolver : ICallerResolver
    {
        public const string ApplicationNotRegisteredError = "Application is not registered or disabled.";

        private readonly IOptionsMonitor<GloballyAllowedGroupsConfiguration> groupsConfiguration;

        private readonly ITokenHelper tokenHelper;

        private readonly SafeExchangeDbContext dbContext;

        private readonly ILogger<CallerResolver> log;

        public CallerResolver(IOptionsMonitor<GloballyAllowedGroupsConfiguration> groupsConfiguration, ITokenHelper tokenHelper, SafeExchangeDbContext dbContext, ILogger<CallerResolver> log)
        {
            this.groupsConfiguration = groupsConfiguration ?? throw new ArgumentNullException(nameof(groupsConfiguration));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public async Task<CallerResolution> ResolveAsync(ClaimsPrincipal principal, string channel, string? clientLabel, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(principal);
            ArgumentException.ThrowIfNullOrEmpty(channel);

            var policy = new GlobalAccessPolicy(this.groupsConfiguration.CurrentValue, this.tokenHelper, this.log);
            var denial = await policy.EvaluateAsync(principal, this.dbContext, cancellationToken);
            if (denial is not null)
            {
                return CallerResolution.Denied(denial);
            }

            (SubjectType subjectType, string subjectId) = await SubjectHelper.GetSubjectInfoAsync(this.tokenHelper, principal, this.dbContext);
            if (SubjectType.Application.Equals(subjectType) && string.IsNullOrEmpty(subjectId))
            {
                return CallerResolution.Denied(CallerDenial.Forbidden(ApplicationNotRegisteredError));
            }

            var displayName = SubjectType.User.Equals(subjectType) ? this.tokenHelper.GetDisplayName(principal) : subjectId;
            if (string.IsNullOrEmpty(displayName))
            {
                displayName = subjectId;
            }

            return CallerResolution.Ok(new Caller(subjectType, subjectId, displayName, Array.Empty<string>(), channel, clientLabel));
        }
    }
}
```

- [ ] **Step 8: Run the tests and watch them pass**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.CallerResolverTests"`
Expected: `Passed!  - Failed:     0, Passed:    11, Skipped:     0, Total:    11`

---

### Task 4: Register `ICallerResolver`

**Files:**
- Modify: `SafeExchange.Tests\Tests\CallerResolverTests.cs`
- Modify: `SafeExchange.Core\SafeExchangeStartup.cs`

- [ ] **Step 1: Write the failing registration test**

In `SafeExchange.Tests\Tests\CallerResolverTests.cs`, add the DI using. Before:

```csharp
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging.Abstractions;
```

After:

```csharp
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging.Abstractions;
```

Then add the test right before the `AssertCaller` helper. Before:

```csharp
        private static void AssertCaller(CallerResolution resolution, SubjectType subjectType, string subjectId, string displayName, string channel, string? clientLabel)
```

After:

```csharp
        [Test]
        public void ConfigureServices_RegistersCallerResolverAsScoped()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "CosmosDb:CosmosDbEndpoint", "https://localhost:8081/" },
                    { "CosmosDb:DatabaseName", "startup-test" },
                })
                .Build();
            var services = new ServiceCollection();

            SafeExchangeStartup.ConfigureServices(configuration, services);

            var registrations = services
                .Where(d => d.ServiceType == typeof(ICallerResolver))
                .Select(d => $"{d.Lifetime} {d.ImplementationType?.Name}")
                .ToList();
            Assert.That(registrations, Is.EqualTo(new[] { "Scoped CallerResolver" }));

            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging();
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            using var scope = provider.CreateScope();
            Assert.That(scope.ServiceProvider.GetRequiredService<ICallerResolver>(), Is.InstanceOf<CallerResolver>());
        }

        private static void AssertCaller(CallerResolution resolution, SubjectType subjectType, string subjectId, string displayName, string channel, string? clientLabel)
```

(`ConfigureServices` only builds the Cosmos `DbContext` options; nothing connects, as in
`SafeExchangeFunctionsStartupTests`.)

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~ConfigureServices_RegistersCallerResolverAsScoped"`
Expected: `Failed ConfigureServices_RegistersCallerResolverAsScoped` with
`Expected is <System.String[1]>, actual is <System.Collections.Generic.List`1[System.String]> with 0 elements`.

- [ ] **Step 3: Register the resolver**

In `SafeExchange.Core\SafeExchangeStartup.cs`, add the using. Before:

```csharp
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.AzureAd;
```

After:

```csharp
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.AzureAd;
    using SafeExchange.Core.Callers;
```

Then, in `ConfigureServices`, before:

```csharp
            services.AddScoped<IApplicationOwnerService, ApplicationOwnerService>();
```

After:

```csharp
            services.AddScoped<IApplicationOwnerService, ApplicationOwnerService>();
            services.AddScoped<ICallerResolver, CallerResolver>();
```

- [ ] **Step 4: Run it and the startup tests and watch them pass**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~ConfigureServices_RegistersCallerResolverAsScoped|FullyQualifiedName~SafeExchangeFunctionsStartupTests"`
Expected: `Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3`

---

### Task 5: Docs

**Files:**
- Modify: `docs\architecture.md`

- [ ] **Step 1: Update the project structure**

In `docs\architecture.md` (section "Project Structure"), before:

```text
├── Blob/                  — Blob storage helpers (upload, download, encrypt)
```

After:

```text
├── Blob/                  — Blob storage helpers (upload, download, encrypt)
├── Callers/               — Caller, ICallerResolver/CallerResolver, GlobalAccessPolicy (host-agnostic caller resolution)
```

Before:

```text
├── Requests/Filters/      — Global authorization filters
├── Utilities/             — ActionResults, TokenHelperHttpExtensions
```

After:

```text
├── Requests/Filters/      — Global authorization filters (HTTP wrappers; the global-group decision is Core's GlobalAccessPolicy)
├── Utilities/             — ActionResults, TokenHelperHttpExtensions, CallerDenialHttpExtensions
```

No other doc changes in this phase: the consumer migration note and `docs/mcp.md` are phase 7, and REST is
unchanged.

---

### Task 6: Final verification and commit

**Files:** none (plus this plan file in the commit).

- [ ] **Step 1: Full rebuild, no new warnings**

Run: `dotnet build SafeExchange.slnx --nologo -v q --no-incremental`
Expected: `705 Warning(s)` and `0 Error(s)` (the Task 0 number). Optional stricter check: the new and changed
files produce no warning lines:
`dotnet build SafeExchange.slnx --nologo -v q --no-incremental 2>&1 | Select-String "Callers\\|CallerDenial|CallerResolver|GlobalAccessPolicy|GlobalAccessFilter|SafeExchangeStartup.cs"`
Expected: no output.

- [ ] **Step 2: Contract, architecture and new tests**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchange.Tests.Contract|FullyQualifiedName~SafeExchange.Tests.Architecture|FullyQualifiedName~SafeExchange.Tests.CallerResolverTests|FullyQualifiedName~SafeExchange.Tests.CallerDenialHttpExtensionsTests|FullyQualifiedName~SafeExchange.Tests.GlobalAccessPolicyTests"`
Expected: `Passed!  - Failed:     0, Passed:   235, Skipped:     0, Total:   235`
(212 contract + 2 architecture + 12 resolver + 2 HTTP extension + 7 policy).

- [ ] **Step 3: Regression sets that construct `GlobalFilters` or touch tokens (EF InMemory / DB-free)**

Run:
```powershell
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SecC|FullyQualifiedName~SecE|FullyQualifiedName~SecF|FullyQualifiedName~EffectivePermissions|FullyQualifiedName~TokenClassification|FullyQualifiedName~SecA"
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~TokenHelperTests|FullyQualifiedName~SecB|FullyQualifiedName~ApplicationSearch|FullyQualifiedName~ApplicationOwnerInvariant|FullyQualifiedName~S2SAllowedTenantsLogging|FullyQualifiedName~S2SAppRegistrationAllowlist|FullyQualifiedName~RegistrarGuard|FullyQualifiedName~ActionResultsTests|FullyQualifiedName~ApiVersionTelemetryMiddleware|FullyQualifiedName~DefaultAuthenticationMiddleware|FullyQualifiedName~SafeExchangeFunctionsStartup|FullyQualifiedName~TokenHelperHttpExtensions"
```
Expected: `Passed!  - Failed:     0, Passed:    66` and `Passed!  - Failed:     0, Passed:    69`.

- [ ] **Step 4: Check the change set**

Run: `git add -A; git --no-pager diff --cached --name-status`
Expected exactly:
```text
M	SafeExchange.Core.Functions/Requests/Filters/GlobalAccessFilter.cs
A	SafeExchange.Core.Functions/Utilities/CallerDenialHttpExtensions.cs
A	SafeExchange.Core/Callers/Caller.cs
A	SafeExchange.Core/Callers/CallerChannels.cs
A	SafeExchange.Core/Callers/CallerDenial.cs
A	SafeExchange.Core/Callers/CallerResolution.cs
A	SafeExchange.Core/Callers/CallerResolver.cs
A	SafeExchange.Core/Callers/GlobalAccessPolicy.cs
A	SafeExchange.Core/Callers/ICallerResolver.cs
M	SafeExchange.Core/SafeExchangeStartup.cs
A	SafeExchange.Tests/Tests/CallerDenialHttpExtensionsTests.cs
A	SafeExchange.Tests/Tests/CallerResolverTests.cs
A	SafeExchange.Tests/Tests/GlobalAccessPolicyTests.cs
M	docs/architecture.md
A	docs/superpowers/plans/2026-11-21-mcp-p3-caller.md
```
Nothing under `SafeExchange.Tests/Contract/` may appear. If the plan file was already committed, its line is
absent.

- [ ] **Step 5: Commit**

```powershell
git commit -m "feat(core): Caller abstraction and host-agnostic caller resolution" -m "Core gains SafeExchange.Core.Callers: Caller, CallerChannels, CallerDenial, CallerResolution, ICallerResolver and the default CallerResolver (global access-group gate, then SubjectHelper, then the registered-application gate, as in today's handler preamble). The global-group decision moves into GlobalAccessPolicy; GlobalAccessFilter becomes a thin wrapper that writes the identical response via CallerDenialHttpExtensions. ICallerResolver is registered scoped. Handlers are unchanged until phase 4." -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```
Expected: `[<branch> <sha>] feat(core): Caller abstraction and host-agnostic caller resolution` and
`15 files changed`.

---

## Risks and notes for later phases

- **Log category (phase 4).** When the handlers switch to `ICallerResolver` (D2), the global-group log lines
  move from the `GlobalFilters` category to `CallerResolver`. Same text and level; not a REST contract.
- **`GroupIds` is empty (D1).** Phase 6's `whoami` must not present it as "the caller's groups". If a later
  feature needs groups, read them the way `PermissionsManager` does (Users row, `ConsentRequired`,
  `UseGroupsAuthorization`), not from token claims.
- **Shared `DbContext`.** `CallerResolver` uses the request-scoped `SafeExchangeDbContext`, the same instance
  the handlers and services get from DI. It only reads, so it adds no pending changes.
- **Consumer override.** A consumer that registers its own `ICallerResolver` before calling
  `ConfigureServices` is overridden by ours; it must register after (documented on the interface; the phase 7
  consumer migration note should repeat it).
- **Preserved quirks.** `AllowedGroups` entries are not trimmed and group ids compare ordinally; an app token
  has no UPN, so it never passes the group gate. These are today's behaviour and are kept on purpose.
