# Phase 2: split SafeExchange.Core.Functions out of SafeExchange.Core (implementation plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Move every Azure Functions-typed file out of `SafeExchange.Core` into a new class library
`SafeExchange.Core.Functions`. After this phase `SafeExchange.Core` has no `Microsoft.Azure.Functions.*` package
reference and no reference to any Functions assembly. An architecture test enforces this. REST behaviour,
the DI graph and the middleware order stay the same.

**Architecture:** New project `SafeExchange.Core.Functions` (net10.0, references `SafeExchange.Core`, packages
`Microsoft.Azure.Functions.Worker.Core` + `Microsoft.Azure.Functions.Worker.Extensions.Http`). 42 files move
with `git mv` and keep their namespaces (master plan decision 1). `ITokenHelper.GetAccountIdAndToken` becomes
an extension method in the new project (decision 2). `SafeExchangeStartup` is split (decision 3). Core gets
`InternalsVisibleTo("SafeExchange.Core.Functions")` (decision 4).

**Tech stack:** .NET 10 SDK 10.0.300, Azure Functions isolated worker 2.52 (`Worker.Core`), Http extension
3.3.0, central package management (`Directory.Packages.props`), NUnit 4.6, Moq 4.20.

**Master plan:** `docs/superpowers/plans/2026-11-21-mcp-endpoint.md`. It has the ground rules, style,
commands and cross-phase decisions 1-4.
**Spec:** `docs/superpowers/specs/2026-11-21-mcp-endpoint-design.md` (§Architecture/Projects, §Testing 2,
§Consumer migration note).

**Verified baseline:** this plan was run step by step in a scratch worktree created from `wt/p1` at
**`7745dfa`** (phase 1 plus its review fixes). The new files and every before/after snippet were applied
**from this document's own code blocks** by a script, so the snippets match the code exactly. Results:
- the solution builds with 0 errors;
- a `--no-incremental` build reports 705 warnings, and the set of unique warnings (692) is identical before
  and after, so there are no new warnings;
- the 42 moves are pure renames (`R100`);
- `functions.metadata` of `SafeExchange.Functions` is byte-identical;
- the DI descriptor multiset from `ConfigureFunctionsServices` + `ConfigureServices` is identical to the old
  `ConfigureServices` (75 descriptors);
- contract + architecture tests: 214/214;
- every test that does not need the Cosmos emulator: 618/618 (baseline 609 + 9 new);
- the Release build succeeds and the commit command works.

The Cosmos emulator is **not** needed for this phase.

---

## Ground rules for this phase

1. **Run every command from the repository root**, i.e. the worktree that holds the phase 1 commits
   (HEAD was `7745dfa` when this plan was verified). All paths in this plan are relative to that root.
2. **Moved files are not edited.** `git mv` only. Their namespaces stay (decision 1), so callers do not change.
   The only production files whose content changes are listed under "File map" below.
3. **The phase 1 contract tests (`SafeExchange.Tests\Contract\*`) are frozen.** Do not edit them. They must pass
   unchanged after every task.
4. **Style** (master plan): file header `/// <summary>\n/// TypeName\n/// </summary>`, block namespace, `using`
   directives inside the namespace, `this.` prefix, 4-space indent, NUnit `Assert.That(..., Is....)`. Add a
   comment only where a line needs explaining. The test project has no implicit usings, so every test file lists
   its usings.
5. **No commits until Task 8.** The phase is committed once, at the end.
6. **No new build warnings.** Task 0 records the baseline and Task 8 compares against it.

## Decisions taken in this phase

### D1: Final move set is 42 files, not 45

The dependency scan found 45 Core files that use `Microsoft.Azure.Functions.Worker`. Three of them stay in Core
and are edited so they no longer use Functions types:

- `SafeExchange.Core\Utilities\ITokenHelper.cs` and `SafeExchange.Core\Utilities\TokenHelper.cs`: only
  `GetAccountIdAndToken(HttpRequestData, ClaimsPrincipal)` used Functions types. `SubjectHelper` stays in Core
  and depends on `ITokenHelper`, so the interface must stay in Core too (decision 2).
- `SafeExchange.Core\SafeExchangeStartup.cs`: split (decision 3). The Functions part goes to the new
  `SafeExchange.Core.Functions\SafeExchangeFunctionsStartup.cs`.

These four files in `SafeExchange.Core\Functions\` use no Functions types, so they also stay in Core:
`PermissionNetChange.cs`, `SafeExchangeNotificationDataPurge.cs`, `SafeExchangeProcessExternalNotification.cs`,
`SafeExchangePurge.cs`. Phase 4 uses `PermissionNetChange` from a Core service.

### D2: Where the `GetAccountIdAndToken` log line goes

The old member logged through `TokenHelper`'s injected `ILogger<TokenHelper>`. The trace had category
`SafeExchange.Core.TokenHelper` and this message:
`GetAccountIdAndToken: AccountId [{accountId}], token is empty|extracted`.

An extension method cannot reach that private logger. The options considered were:

| Option | Rejected because |
|---|---|
| Add an `ILogger` parameter to the extension | All three call sites would change (decision 2 says they must compile unchanged), and the category would become the caller's. |
| Log from `TokenHelper.GetAccountId` | Every future host-agnostic `GetAccountId` caller (phases 3-4) would emit an HTTP-specific trace. |
| Drop the trace | That changes observable behaviour. |

**Chosen:** the extension resolves `ILoggerFactory` from `request.FunctionContext.InstanceServices` and logs
through `CreateLogger<TokenHelper>()`. The category stays `SafeExchange.Core.TokenHelper`, and the level,
message text (still built with `nameof(GetAccountIdAndToken)`) and timing are unchanged. If no
`ILoggerFactory` is available, as in unit tests whose `InstanceServices` is a loose `Mock<IServiceProvider>`
or `null`, the extension falls back to `NullLogger.Instance`. In production the worker always registers
`ILoggerFactory`. `TokenHelperHttpExtensionsTests` pins the category, the message and the fallback.

`GetAccessToken` (bearer extraction) moves unchanged into the extension class as a private helper.
`TokenHelper.GetAccountId` becomes public and joins `ITokenHelper` without changing its logic.

### D3: Call order of the two DI methods (deviation from the brief's "ConfigureServices then ConfigureFunctionsServices")

The old `ConfigureServices` registered the `ITelemetryInitializer`s in this order: `SessionCorrelation`,
`ApiVersion`, `TelemetryId`, `PiiRedaction`. App Insights runs initializers in registration order. To keep that
order **identical**, `Program.cs` calls `SafeExchangeFunctionsStartup.ConfigureFunctionsServices(services)`
**before** `SafeExchangeStartup.ConfigureServices(configuration, services)`. The other two registrations that
move (`ITokenMiddlewareCore`, `GlobalFilters`) are single registrations, so their position does not matter.

In the scratch run the descriptor multiset was identical, and with this call order the
`ITelemetryInitializer` order was identical too. `SafeExchangeFunctionsStartupTests` pins that order. The
middleware order in `ConfigureWorkerDefaults` is copied verbatim.

### D4: `InternalsVisibleTo`

The brief expected `IConfidentialClientProvider` and `MigrationsHelper` to need IVT. They don't: they are used
only by `ConfigureServices`, which stays in Core.

The moved code does use many Core internals, though. Without IVT the new project fails with 57 errors. The
internals it uses are:
- `PermissionNetChange` and its `Add`, `Remove`, `SubjectType`, `SubjectId`, `SubjectName`;
- `PermissionsManager.NormalizeSubjectId`;
- `GraphDataProvider`;
- the internal model `ToDto` / `ToOverviewDto` / `ToCreationDto` / `ToPinnedGroupDto` methods on
  `ObjectMetadata`, `ContentMetadata`, `ExpirationMetadata`, `ChunkMetadata`, `SubjectPermissions`,
  `AccessRequest`, `Application`, `GroupDictionaryItem`, `WebhookSubscription`, `GraphUserInfo` and
  `GraphGroupInfo`.

So Core gets `<InternalsVisibleTo Include="SafeExchange.Core.Functions" />`.

The tests use internals of moved types, for example `SafeExchangeApplicationSearch.ScanCap`,
`SafeExchangeS2SApps.IsRegistrarRow` and `SafeExchangeSecretAudit.BuildAuditPageAsync`. So the new project gets
`<InternalsVisibleTo Include="SafeExchange.Tests" />`, mirroring Core.

### D5: Architecture assertion

`CoreHostAgnosticTests` has two tests:

1. **Primary:** `typeof(SafeExchangeDbContext).Assembly.GetReferencedAssemblies()` must contain no name starting
   with `Microsoft.Azure.Functions`. The compiler emits an `AssemblyRef` for every assembly whose types or
   members Core uses anywhere: signatures, IL bodies, attributes, generic instantiations, `typeof`. So this
   check is complete. It is also cheaper and more reliable than walking types and members with reflection,
   which misses references that appear only inside method bodies. Every type in a `Microsoft.Azure.Functions.*`
   namespace lives in a `Microsoft.Azure.Functions.*` assembly, so this also covers the spec's namespace
   wording. Comments and `cref`s emit nothing, so they cannot cause false failures.
2. **Secondary:** `SafeExchange.Core\SafeExchange.Core.csproj` has no `PackageReference` starting with
   `Microsoft.Azure.Functions`. This is the spec's literal rule. Test 1 alone would not catch a package that
   is referenced but unused. The test walks up from the test output directory to find the csproj, which works
   in local and CI checkouts.

Before the phase, test 1 fails with `Microsoft.Azure.Functions.Worker.Core` and test 2 fails with both
packages. After Task 5, test 1 passes. After Task 6, both pass.

### D6: `cref`s in Core comments

Core does not set `GenerateDocumentationFile`, so unresolved `cref`s are not reported. The two `cref`s the
dependency scan flagged point at `TokenHelper`, which stays in Core:
- `Configuration\AuthenticationConfiguration.cs:40`
- `Utilities\TokenClassification.cs:7`

The other mentions of moved types are plain comments:
- `TokenValidationParametersProvider.cs`
- `TelemetryContext.cs`
- `GraphDataProvider.cs`

No comment edits are needed. The unique-warning set stays identical (verified).

### D7: CI and package versions

`.github\workflows\ci.yml` runs `dotnet restore SafeExchange.slnx`, `dotnet build SafeExchange.slnx`,
`dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj` and `dotnet publish SafeExchange.Functions`. All of
these pick up the new project through the `.slnx` entry and the project references, so **ci.yml does not
change**. Both package versions are already in `Directory.Packages.props`, which **does not change** either.
`Microsoft.Extensions.Hosting.Abstractions` (for `HostBuilderContext` and `AddHostedService`) still reaches both
projects transitively (verified: Core builds without the Functions packages).

## File map

Create:
- `SafeExchange.Core.Functions\SafeExchange.Core.Functions.csproj`: the new library.
- `SafeExchange.Core.Functions\SafeExchangeFunctionsStartup.cs`: `IsHttpTrigger`, `ConfigureWorkerDefaults`,
  `ConfigureFunctionsServices`.
- `SafeExchange.Core.Functions\Utilities\TokenHelperHttpExtensions.cs`: `GetAccountIdAndToken` extension.
- `SafeExchange.Tests\Architecture\CoreHostAgnosticTests.cs`: architecture tests.
- `SafeExchange.Tests\Tests\SafeExchangeFunctionsStartupTests.cs`: pins the DI registrations and order.
- `SafeExchange.Tests\Tests\TokenHelperHttpExtensionsTests.cs`: pins the extension's behaviour and log line.

Move (`git mv`, content unchanged, 42 files):
- `SafeExchange.Core\Functions\` → `SafeExchange.Core.Functions\Functions\` (20)
- `SafeExchange.Core\Functions\Admin\` → `SafeExchange.Core.Functions\Functions\Admin\` (10)
- `SafeExchange.Core\Middleware\` → `SafeExchange.Core.Functions\Middleware\` (7)
- `SafeExchange.Core\Requests\Filters\` → `SafeExchange.Core.Functions\Requests\Filters\` (4)
- `SafeExchange.Core\Utilities\ActionResults.cs` → `SafeExchange.Core.Functions\Utilities\ActionResults.cs` (1)

Modify:
- `SafeExchange.slnx`: add the project.
- `SafeExchange.Core\SafeExchange.Core.csproj`: IVT, remove the 2 Functions packages.
- `SafeExchange.Core\SafeExchangeStartup.cs`: remove the Functions part.
- `SafeExchange.Core\Utilities\ITokenHelper.cs`: drop `GetAccountIdAndToken`, add `GetAccountId`.
- `SafeExchange.Core\Utilities\TokenHelper.cs`: same, plus drop `GetAccessToken`.
- `SafeExchange.Functions\SafeExchange.Functions.csproj`: project reference.
- `SafeExchange.Functions\Program.cs`: call both startups.
- `SafeExchange.Tests\SafeExchange.Tests.csproj`: project reference.
- `SafeExchange.Tests\Utilities\TestTokenHelper.cs`: `GetAccountId` replaces `GetAccountIdAndToken`.
- `SafeExchange.Tests\Tests\ApplicationSearchTests.cs`: the nested `AppOnlyTokenHelper` gets the same change.
- `SafeExchange.Tests\Tests\SecE1_NotificationIdorTests.cs`: the nested `FakeTokenHelper` gets the same change.
- `README.md`, `docs\architecture.md`: the project structure sections.

---

### Task 0: Record the baseline

**Files:** none.

- [ ] **Step 1: Confirm the starting commit and a clean tree**

Run: `git --no-pager log --oneline -1; git status --short`
Expected: `7745dfa test(contract): close review gaps (case, filters on all entry points, error branches, audit paging, lock expiry, flags)`
and no status lines. If HEAD is a later phase 1 commit, carry on, but use the contract count from Step 3
wherever this plan says 212.

- [ ] **Step 2: Full rebuild and note the warning count**

Run: `dotnet build SafeExchange.slnx --nologo -v q --no-incremental`
Expected: the last lines are `705 Warning(s)` and `0 Error(s)`. Write the number down. Task 8 must
report the same number.

- [ ] **Step 3: Run the baseline test sets**

Run:
```powershell
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchange.Tests.Contract"
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SecC|FullyQualifiedName~SecE|FullyQualifiedName~SecF|FullyQualifiedName~EffectivePermissions|FullyQualifiedName~TokenClassification|FullyQualifiedName~SecA"
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~TokenHelperTests|FullyQualifiedName~SecB|FullyQualifiedName~ApplicationSearch|FullyQualifiedName~ApplicationOwnerInvariant|FullyQualifiedName~S2SAllowedTenantsLogging|FullyQualifiedName~S2SAppRegistrationAllowlist|FullyQualifiedName~RegistrarGuard|FullyQualifiedName~ActionResultsTests|FullyQualifiedName~ApiVersionTelemetryMiddleware|FullyQualifiedName~DefaultAuthenticationMiddleware"
```
Expected: `Passed: 212`, `Passed: 66`, `Passed: 62` (all `Failed: 0`). These fixtures are all EF InMemory or
DB-free.

---

### Task 1: Architecture test (red)

**Files:**
- Create: `SafeExchange.Tests\Architecture\CoreHostAgnosticTests.cs`

- [ ] **Step 1: Write the failing test**

Create the folder: `New-Item -ItemType Directory SafeExchange.Tests\Architecture`

Create `SafeExchange.Tests\Architecture\CoreHostAgnosticTests.cs`:

```csharp
/// <summary>
/// CoreHostAgnosticTests
/// </summary>

namespace SafeExchange.Tests.Architecture
{
    using NUnit.Framework;
    using SafeExchange.Core;
    using System;
    using System.IO;
    using System.Linq;
    using System.Xml.Linq;

    [TestFixture]
    public class CoreHostAgnosticTests
    {
        private const string AzureFunctionsPrefix = "Microsoft.Azure.Functions";

        [Test]
        public void CoreAssembly_ReferencesNoAzureFunctionsAssembly()
        {
            var offending = typeof(SafeExchangeDbContext).Assembly
                .GetReferencedAssemblies()
                .Select(name => name.Name ?? string.Empty)
                .Where(name => name.StartsWith(AzureFunctionsPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            Assert.That(offending, Is.Empty, "SafeExchange.Core must not use Azure Functions types; put them in SafeExchange.Core.Functions.");
        }

        [Test]
        public void CoreProject_HasNoAzureFunctionsPackageReference()
        {
            var project = XDocument.Load(FindCoreProjectFile());

            var offending = project.Descendants("PackageReference")
                .Select(reference => (string?)reference.Attribute("Include") ?? string.Empty)
                .Where(name => name.StartsWith(AzureFunctionsPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            Assert.That(offending, Is.Empty, "SafeExchange.Core.csproj must not reference Azure Functions packages.");
        }

        private static string FindCoreProjectFile()
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "SafeExchange.Core", "SafeExchange.Core.csproj");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException("SafeExchange.Core/SafeExchange.Core.csproj was not found above the test output directory.");
        }
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchange.Tests.Architecture"`
Expected: `Failed: 2, Passed: 0`, with:
- `CoreAssembly_ReferencesNoAzureFunctionsAssembly`: `But was:  < "Microsoft.Azure.Functions.Worker.Core" >`
- `CoreProject_HasNoAzureFunctionsPackageReference`:
  `But was:  < "Microsoft.Azure.Functions.Worker.Core", "Microsoft.Azure.Functions.Worker.Extensions.Http" >`

These tests stay red until Task 5 (test 1) and Task 6 (test 2). Do not change them.

---

### Task 2: Create the `SafeExchange.Core.Functions` project

**Files:**
- Create: `SafeExchange.Core.Functions\SafeExchange.Core.Functions.csproj`
- Modify: `SafeExchange.slnx`, `SafeExchange.Functions\SafeExchange.Functions.csproj`,
  `SafeExchange.Tests\SafeExchange.Tests.csproj`

- [ ] **Step 1: Create the project file**

Create the folder: `New-Item -ItemType Directory SafeExchange.Core.Functions`

Create `SafeExchange.Core.Functions\SafeExchange.Core.Functions.csproj`. It mirrors Core's properties and its
`ExecutionContext` alias, so moved files compile the same way. Versions come from `Directory.Packages.props`.

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="SafeExchange.Tests" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Core" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Http" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\SafeExchange.Core\SafeExchange.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="System.Threading.ExecutionContext" Alias="ExecutionContext" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add it to the solution**

Run: `dotnet sln SafeExchange.slnx add SafeExchange.Core.Functions\SafeExchange.Core.Functions.csproj`
Expected: ``Project `SafeExchange.Core.Functions\SafeExchange.Core.Functions.csproj` added to the solution.``

`SafeExchange.slnx` is now exactly:

```xml
<Solution>
  <Project Path="SafeExchange.Core.Functions/SafeExchange.Core.Functions.csproj" />
  <Project Path="SafeExchange.Core/SafeExchange.Core.csproj" />
  <Project Path="SafeExchange.Functions/SafeExchange.Functions.csproj" />
  <Project Path="SafeExchange.Tests/SafeExchange.Tests.csproj" />
</Solution>
```

- [ ] **Step 3: Reference it from the Functions app and the tests**

Run:
```powershell
dotnet add SafeExchange.Functions\SafeExchange.Functions.csproj reference SafeExchange.Core.Functions\SafeExchange.Core.Functions.csproj
dotnet add SafeExchange.Tests\SafeExchange.Tests.csproj reference SafeExchange.Core.Functions\SafeExchange.Core.Functions.csproj
```
Expected: ``Reference `..\SafeExchange.Core.Functions\SafeExchange.Core.Functions.csproj` added to the project.``
twice.

The resulting edit in **both** `SafeExchange.Functions\SafeExchange.Functions.csproj` and
`SafeExchange.Tests\SafeExchange.Tests.csproj`:

Before:
```xml
    <ProjectReference Include="..\SafeExchange.Core\SafeExchange.Core.csproj" />
  </ItemGroup>
```
After:
```xml
    <ProjectReference Include="..\SafeExchange.Core\SafeExchange.Core.csproj" />
    <ProjectReference Include="..\SafeExchange.Core.Functions\SafeExchange.Core.Functions.csproj" />
  </ItemGroup>
```

- [ ] **Step 4: Build**

Run: `dotnet build SafeExchange.slnx --nologo -v q`
Expected: `0 Error(s)`. The new project has no sources yet and builds an empty assembly.

---

### Task 3: Split `SafeExchangeStartup` (decision 3)

**Files:**
- Create: `SafeExchange.Tests\Tests\SafeExchangeFunctionsStartupTests.cs`
- Create: `SafeExchange.Core.Functions\SafeExchangeFunctionsStartup.cs`
- Modify: `SafeExchange.Core\SafeExchangeStartup.cs`, `SafeExchange.Functions\Program.cs`

`SafeExchangeFunctionsStartup` can live in the new project right away. The types it registers
(`TokenMiddlewareCore`, `GlobalFilters`, the middleware and initializers) are still public types in Core, and
the new project references Core. Task 4 moves those types next to it without changing this file.

- [ ] **Step 1: Write the failing test**

Create `SafeExchange.Tests\Tests\SafeExchangeFunctionsStartupTests.cs`:

```csharp
/// <summary>
/// SafeExchangeFunctionsStartupTests
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.ApplicationInsights.Extensibility;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Middleware;
    using SafeExchange.Core.Telemetry;
    using System.Collections.Generic;
    using System.Linq;

    [TestFixture]
    public class SafeExchangeFunctionsStartupTests
    {
        [Test]
        public void ConfigureFunctionsServices_RegistersTheFunctionsTypedServices()
        {
            var services = new ServiceCollection();

            SafeExchangeFunctionsStartup.ConfigureFunctionsServices(services);

            var registrations = services
                .Select(d => $"{d.Lifetime} {d.ServiceType.Name} <- {d.ImplementationType?.Name}")
                .ToList();
            Assert.That(registrations, Is.EqualTo(new[]
            {
                "Scoped ITokenMiddlewareCore <- TokenMiddlewareCore",
                "Singleton GlobalFilters <- GlobalFilters",
                "Singleton ITelemetryInitializer <- SessionCorrelationTelemetryInitializer",
                "Singleton ITelemetryInitializer <- ApiVersionTelemetryInitializer",
            }));
        }

        [Test]
        public void FunctionsThenCoreServices_KeepTheTelemetryInitializerOrder()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "CosmosDb:CosmosDbEndpoint", "https://localhost:8081/" },
                    { "CosmosDb:DatabaseName", "startup-test" },
                })
                .Build();
            var services = new ServiceCollection();

            SafeExchangeFunctionsStartup.ConfigureFunctionsServices(services);
            SafeExchangeStartup.ConfigureServices(configuration, services);

            var initializers = services
                .Where(d => d.ServiceType == typeof(ITelemetryInitializer))
                .Select(d => d.ImplementationType)
                .ToList();
            Assert.That(initializers, Is.EqualTo(new[]
            {
                typeof(SessionCorrelationTelemetryInitializer),
                typeof(ApiVersionTelemetryInitializer),
                typeof(TelemetryIdTelemetryInitializer),
                typeof(PiiRedactionTelemetryInitializer),
            }));
        }
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~SafeExchangeFunctionsStartupTests"`
Expected: build fails with
`SafeExchangeFunctionsStartupTests.cs(25,13): error CS0103: The name 'SafeExchangeFunctionsStartup' does not exist in the current context`
(and the same at line 51).

- [ ] **Step 3: Create `SafeExchangeFunctionsStartup`**

Create `SafeExchange.Core.Functions\SafeExchangeFunctionsStartup.cs`. `IsHttpTrigger` and
`ConfigureWorkerDefaults` are copied verbatim from `SafeExchangeStartup`, so the middleware order is the same.
The four registrations are the ones removed from `ConfigureServices` in Step 4.

```csharp
/// <summary>
/// SafeExchangeFunctionsStartup
/// </summary>

namespace SafeExchange.Core
{
    using Microsoft.ApplicationInsights.Extensibility;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Middleware;

    public static class SafeExchangeFunctionsStartup
    {
        public static bool IsHttpTrigger(FunctionContext context)
            => context.FunctionDefinition.InputBindings.Values
                .First(a => a.Type.EndsWith("Trigger")).Type == "httpTrigger";

        public static void ConfigureWorkerDefaults(HostBuilderContext context, IFunctionsWorkerApplicationBuilder builder)
        {
            builder.UseWhen<DefaultAuthenticationMiddleware>(IsHttpTrigger);
            builder.UseWhen<TokenFilterMiddleware>(IsHttpTrigger);

            // After auth so failed-auth requests still get the dimension,
            // but before any handler logs so downstream ILogger scopes
            // inherit the x-saex-session-id correlation.
            builder.UseWhen<SessionCorrelationMiddleware>(IsHttpTrigger);

            // Records the {apiVersion} route segment so v2-vs-v3 traffic can be measured.
            builder.UseWhen<ApiVersionTelemetryMiddleware>(IsHttpTrigger);
        }

        // Call before SafeExchangeStartup.ConfigureServices: telemetry initializers run in
        // registration order, and these two were registered ahead of the Core ones.
        public static void ConfigureFunctionsServices(IServiceCollection services)
        {
            services.AddScoped<ITokenMiddlewareCore, TokenMiddlewareCore>();
            services.AddSingleton<GlobalFilters>();

            // Stamps customDimensions.saex.sessionId on every telemetry
            // item emitted within a request that carried the header. Works
            // in tandem with SessionCorrelationMiddleware (which sets the
            // AsyncLocal for the duration of the request handler).
            services.AddSingleton<ITelemetryInitializer, SessionCorrelationTelemetryInitializer>();
            services.AddSingleton<ITelemetryInitializer, ApiVersionTelemetryInitializer>();
        }
    }
}
```

- [ ] **Step 4: Remove the Functions part from `SafeExchange.Core\SafeExchangeStartup.cs`**

Make six edits. Everything else in the file stays as it is.

Edit 4a (usings). Before:
```csharp
    using Azure.Identity;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.EntityFrameworkCore;
```
After:
```csharp
    using Azure.Identity;
    using Microsoft.EntityFrameworkCore;
```

Edit 4b (usings; both namespaces leave Core in Task 4). Before:
```csharp
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Crypto;
    using SafeExchange.Core.Middleware;
    using SafeExchange.Core.Migrations;
```
After:
```csharp
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Crypto;
    using SafeExchange.Core.Migrations;
```

Edit 4c (`IsHttpTrigger`). Before:
```csharp
    public class SafeExchangeStartup
    {
        public static bool IsHttpTrigger(FunctionContext context)
            => context.FunctionDefinition.InputBindings.Values
                .First(a => a.Type.EndsWith("Trigger")).Type == "httpTrigger";

        // LOCAL SPIKE ONLY: when SAEX_DEV_MODE=true we substitute local-runnable
```
After:
```csharp
    public class SafeExchangeStartup
    {
        // LOCAL SPIKE ONLY: when SAEX_DEV_MODE=true we substitute local-runnable
```

Edit 4d (`ConfigureWorkerDefaults`). Before:
```csharp
            => string.Equals(Environment.GetEnvironmentVariable("SAEX_DEV_MODE"), "true", StringComparison.OrdinalIgnoreCase);

        public static void ConfigureWorkerDefaults(HostBuilderContext context, IFunctionsWorkerApplicationBuilder builder)
        {
            builder.UseWhen<DefaultAuthenticationMiddleware>(IsHttpTrigger);
            builder.UseWhen<TokenFilterMiddleware>(IsHttpTrigger);

            // After auth so failed-auth requests still get the dimension,
            // but before any handler logs so downstream ILogger scopes
            // inherit the x-saex-session-id correlation.
            builder.UseWhen<SessionCorrelationMiddleware>(IsHttpTrigger);

            // Records the {apiVersion} route segment so v2-vs-v3 traffic can be measured.
            builder.UseWhen<ApiVersionTelemetryMiddleware>(IsHttpTrigger);
        }

        // The categories below are emitted inside the isolated worker process —
```
After:
```csharp
            => string.Equals(Environment.GetEnvironmentVariable("SAEX_DEV_MODE"), "true", StringComparison.OrdinalIgnoreCase);

        // The categories below are emitted inside the isolated worker process —
```

Edit 4e (`ITokenMiddlewareCore` and `GlobalFilters` registrations). Before:
```csharp
            services.AddHttpClient();

            services.AddScoped<ITokenMiddlewareCore, TokenMiddlewareCore>();
            services.AddSingleton<ITokenValidationParametersProvider, TokenValidationParametersProvider>();
```
After:
```csharp
            services.AddHttpClient();

            services.AddSingleton<ITokenValidationParametersProvider, TokenValidationParametersProvider>();
```
and before:
```csharp
            services.AddSingleton<IPurger, PurgeManager>();
            services.AddSingleton<GlobalFilters>();

            services.AddScoped<IPermissionsManager, PermissionsManager>();
```
After:
```csharp
            services.AddSingleton<IPurger, PurgeManager>();

            services.AddScoped<IPermissionsManager, PermissionsManager>();
```

Edit 4f (the two middleware telemetry initializers). Before:
```csharp
            services.Configure<WebClientTelemetryConfiguration>(configuration.GetSection("WebClientTelemetry"));

            // Stamps customDimensions.saex.sessionId on every telemetry
            // item emitted within a request that carried the header. Works
            // in tandem with SessionCorrelationMiddleware (which sets the
            // AsyncLocal for the duration of the request handler).
            services.AddSingleton<ITelemetryInitializer, SessionCorrelationTelemetryInitializer>();
            services.AddSingleton<ITelemetryInitializer, ApiVersionTelemetryInitializer>();

            // Stamps customDimensions.saex.telemetryId — a pseudonymous, weekly-rotating
```
After:
```csharp
            services.Configure<WebClientTelemetryConfiguration>(configuration.GetSection("WebClientTelemetry"));

            // Stamps customDimensions.saex.telemetryId — a pseudonymous, weekly-rotating
```

Keep `using Microsoft.Extensions.Hosting;` (needed by `AddHostedService`) and
`using Microsoft.ApplicationInsights.Extensibility;` (needed by the two remaining initializers).

- [ ] **Step 5: Call both startups from `SafeExchange.Functions\Program.cs`**

Before:
```csharp
                .ConfigureFunctionsWorkerDefaults(SafeExchangeStartup.ConfigureWorkerDefaults)
```
After:
```csharp
                .ConfigureFunctionsWorkerDefaults(SafeExchangeFunctionsStartup.ConfigureWorkerDefaults)
```

Before:
```csharp
                .ConfigureServices((hostBuilderContext, serviceCollection) =>
                {
                    SafeExchangeStartup.ConfigureServices(hostBuilderContext.Configuration, serviceCollection);
                })
```
After:
```csharp
                .ConfigureServices((hostBuilderContext, serviceCollection) =>
                {
                    SafeExchangeFunctionsStartup.ConfigureFunctionsServices(serviceCollection);
                    SafeExchangeStartup.ConfigureServices(hostBuilderContext.Configuration, serviceCollection);
                })
```

`Program.cs` already has `using SafeExchange.Core;`, the namespace of `SafeExchangeFunctionsStartup`.

- [ ] **Step 6: Build and run the tests**

Run: `dotnet build SafeExchange.slnx --nologo -v q`
Expected: `0 Error(s)`.

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchangeFunctionsStartupTests|FullyQualifiedName~CosmosCertificateValidationTests|FullyQualifiedName~SafeExchange.Tests.Contract"`
Expected: `Failed: 0, Passed: 231` (2 + 17 + 212). `CosmosCertificateValidationTests` runs the real
`ConfigureServices` and needs no emulator.

---

### Task 4: Move the 42 Functions-typed files

**Files:**
- Move: the 42 files listed in Step 1
- Modify: `SafeExchange.Core\SafeExchange.Core.csproj` (IVT)

- [ ] **Step 1: `git mv` the files**

Run (PowerShell, from the repo root):

```powershell
New-Item -ItemType Directory -Force SafeExchange.Core.Functions\Functions\Admin, SafeExchange.Core.Functions\Middleware, SafeExchange.Core.Functions\Requests\Filters, SafeExchange.Core.Functions\Utilities | Out-Null

git mv SafeExchange.Core\Functions\SafeExchangeAccess.cs SafeExchange.Core.Functions\Functions\SafeExchangeAccess.cs
git mv SafeExchange.Core\Functions\SafeExchangeAccessGiveUp.cs SafeExchange.Core.Functions\Functions\SafeExchangeAccessGiveUp.cs
git mv SafeExchange.Core\Functions\SafeExchangeAccessRequest.cs SafeExchange.Core.Functions\Functions\SafeExchangeAccessRequest.cs
git mv SafeExchange.Core\Functions\SafeExchangeApplicationSearch.cs SafeExchange.Core.Functions\Functions\SafeExchangeApplicationSearch.cs
git mv SafeExchange.Core\Functions\SafeExchangeApplicationsList.cs SafeExchange.Core.Functions\Functions\SafeExchangeApplicationsList.cs
git mv SafeExchange.Core\Functions\SafeExchangeContentCommit.cs SafeExchange.Core.Functions\Functions\SafeExchangeContentCommit.cs
git mv SafeExchange.Core\Functions\SafeExchangeExternalNotificationDetails.cs SafeExchange.Core.Functions\Functions\SafeExchangeExternalNotificationDetails.cs
git mv SafeExchange.Core\Functions\SafeExchangeGroupSearch.cs SafeExchange.Core.Functions\Functions\SafeExchangeGroupSearch.cs
git mv SafeExchange.Core\Functions\SafeExchangeGroupsList.cs SafeExchange.Core.Functions\Functions\SafeExchangeGroupsList.cs
git mv SafeExchange.Core\Functions\SafeExchangePinnedGroups.cs SafeExchange.Core.Functions\Functions\SafeExchangePinnedGroups.cs
git mv SafeExchange.Core\Functions\SafeExchangePinnedGroupsList.cs SafeExchange.Core.Functions\Functions\SafeExchangePinnedGroupsList.cs
git mv SafeExchange.Core\Functions\SafeExchangePinnedSecrets.cs SafeExchange.Core.Functions\Functions\SafeExchangePinnedSecrets.cs
git mv SafeExchange.Core\Functions\SafeExchangePinnedSecretsList.cs SafeExchange.Core.Functions\Functions\SafeExchangePinnedSecretsList.cs
git mv SafeExchange.Core\Functions\SafeExchangeS2SApps.cs SafeExchange.Core.Functions\Functions\SafeExchangeS2SApps.cs
git mv SafeExchange.Core\Functions\SafeExchangeSecretAudit.cs SafeExchange.Core.Functions\Functions\SafeExchangeSecretAudit.cs
git mv SafeExchange.Core\Functions\SafeExchangeSecretContentMeta.cs SafeExchange.Core.Functions\Functions\SafeExchangeSecretContentMeta.cs
git mv SafeExchange.Core\Functions\SafeExchangeSecretMeta.cs SafeExchange.Core.Functions\Functions\SafeExchangeSecretMeta.cs
git mv SafeExchange.Core\Functions\SafeExchangeSecretStream.cs SafeExchange.Core.Functions\Functions\SafeExchangeSecretStream.cs
git mv SafeExchange.Core\Functions\SafeExchangeTelemetryConfig.cs SafeExchange.Core.Functions\Functions\SafeExchangeTelemetryConfig.cs
git mv SafeExchange.Core\Functions\SafeExchangeUserSearch.cs SafeExchange.Core.Functions\Functions\SafeExchangeUserSearch.cs

git mv SafeExchange.Core\Functions\Admin\PaginationHelper.cs SafeExchange.Core.Functions\Functions\Admin\PaginationHelper.cs
git mv SafeExchange.Core\Functions\Admin\SafeExchangeAdminApplications.cs SafeExchange.Core.Functions\Functions\Admin\SafeExchangeAdminApplications.cs
git mv SafeExchange.Core\Functions\Admin\SafeExchangeAdminAudit.cs SafeExchange.Core.Functions\Functions\Admin\SafeExchangeAdminAudit.cs
git mv SafeExchange.Core\Functions\Admin\SafeExchangeAdminGroups.cs SafeExchange.Core.Functions\Functions\Admin\SafeExchangeAdminGroups.cs
git mv SafeExchange.Core\Functions\Admin\SafeExchangeAdminOperations.cs SafeExchange.Core.Functions\Functions\Admin\SafeExchangeAdminOperations.cs
git mv SafeExchange.Core\Functions\Admin\SafeExchangeAdminSecrets.cs SafeExchange.Core.Functions\Functions\Admin\SafeExchangeAdminSecrets.cs
git mv SafeExchange.Core\Functions\Admin\SafeExchangeAdminUsers.cs SafeExchange.Core.Functions\Functions\Admin\SafeExchangeAdminUsers.cs
git mv SafeExchange.Core\Functions\Admin\SafeExchangeApplications.cs SafeExchange.Core.Functions\Functions\Admin\SafeExchangeApplications.cs
git mv SafeExchange.Core\Functions\Admin\SafeExchangeWebhookSubscriptions.cs SafeExchange.Core.Functions\Functions\Admin\SafeExchangeWebhookSubscriptions.cs
git mv SafeExchange.Core\Functions\Admin\SafeExchangeWebhookSubscriptionsList.cs SafeExchange.Core.Functions\Functions\Admin\SafeExchangeWebhookSubscriptionsList.cs

git mv SafeExchange.Core\Middleware\ApiVersionTelemetryMiddleware.cs SafeExchange.Core.Functions\Middleware\ApiVersionTelemetryMiddleware.cs
git mv SafeExchange.Core\Middleware\DefaultAuthenticationMiddleware.cs SafeExchange.Core.Functions\Middleware\DefaultAuthenticationMiddleware.cs
git mv SafeExchange.Core\Middleware\FunctionContextAuthenticationExtensions.cs SafeExchange.Core.Functions\Middleware\FunctionContextAuthenticationExtensions.cs
git mv SafeExchange.Core\Middleware\ITokenMiddlewareCore.cs SafeExchange.Core.Functions\Middleware\ITokenMiddlewareCore.cs
git mv SafeExchange.Core\Middleware\SessionCorrelationMiddleware.cs SafeExchange.Core.Functions\Middleware\SessionCorrelationMiddleware.cs
git mv SafeExchange.Core\Middleware\TokenFilterMiddleware.cs SafeExchange.Core.Functions\Middleware\TokenFilterMiddleware.cs
git mv SafeExchange.Core\Middleware\TokenMiddlewareCore.cs SafeExchange.Core.Functions\Middleware\TokenMiddlewareCore.cs

git mv SafeExchange.Core\Requests\Filters\AdminGroupFilter.cs SafeExchange.Core.Functions\Requests\Filters\AdminGroupFilter.cs
git mv SafeExchange.Core\Requests\Filters\GlobalAccessFilter.cs SafeExchange.Core.Functions\Requests\Filters\GlobalAccessFilter.cs
git mv SafeExchange.Core\Requests\Filters\GlobalFilters.cs SafeExchange.Core.Functions\Requests\Filters\GlobalFilters.cs
git mv SafeExchange.Core\Requests\Filters\IRequestFilter.cs SafeExchange.Core.Functions\Requests\Filters\IRequestFilter.cs

git mv SafeExchange.Core\Utilities\ActionResults.cs SafeExchange.Core.Functions\Utilities\ActionResults.cs

Remove-Item SafeExchange.Core\Middleware, SafeExchange.Core\Requests\Filters, SafeExchange.Core\Functions\Admin
```

The last line removes the three folders the moves leave empty. `SafeExchange.Core\Functions\` and
`SafeExchange.Core\Requests\` keep their remaining files.

- [ ] **Step 2: Check the move**

Run: `git status --short | Select-String "^R " | Measure-Object | Select-Object -ExpandProperty Count`
Expected: `42`.

Run: `Get-ChildItem SafeExchange.Core\Functions -File | Select-Object -ExpandProperty Name`
Expected exactly: `PermissionNetChange.cs`, `SafeExchangeNotificationDataPurge.cs`,
`SafeExchangeProcessExternalNotification.cs`, `SafeExchangePurge.cs`.

Run: `git grep -l "Microsoft.Azure.Functions" -- SafeExchange.Core`
Expected exactly: `SafeExchange.Core/SafeExchange.Core.csproj`, `SafeExchange.Core/Utilities/ITokenHelper.cs`,
`SafeExchange.Core/Utilities/TokenHelper.cs`. Task 5 cleans up the two `.cs` files and Task 6 cleans up the
csproj.

- [ ] **Step 3: Build and watch it fail on Core internals**

Run: `dotnet build SafeExchange.slnx --nologo -v q`
Expected: 57 errors, all in `SafeExchange.Core.Functions`. Examples:
`error CS0122: 'PermissionNetChange' is inaccessible due to its protection level`,
`error CS0117: 'PermissionsManager' does not contain a definition for 'NormalizeSubjectId'`,
`error CS0122: 'GraphDataProvider' is inaccessible due to its protection level`,
`error CS1929: 'ObjectMetadata' does not contain a definition for 'ToDto' ...`.
These come from internal Core members (see D4).

- [ ] **Step 4: Grant the new project access to Core internals**

Edit `SafeExchange.Core\SafeExchange.Core.csproj`. Before:
```xml
  <ItemGroup>
    <InternalsVisibleTo Include="SafeExchange.Tests" />
  </ItemGroup>
```
After:
```xml
  <ItemGroup>
    <InternalsVisibleTo Include="SafeExchange.Tests" />
    <InternalsVisibleTo Include="SafeExchange.Core.Functions" />
  </ItemGroup>
```

- [ ] **Step 5: Build and run the tests**

Run: `dotnet build SafeExchange.slnx --nologo -v q`
Expected: `0 Error(s)`.

Run:
```powershell
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchange.Tests.Contract"
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SecC|FullyQualifiedName~SecE|FullyQualifiedName~SecF|FullyQualifiedName~EffectivePermissions|FullyQualifiedName~TokenClassification|FullyQualifiedName~SecA"
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchangeFunctionsStartupTests|FullyQualifiedName~TokenHelperTests|FullyQualifiedName~SecB|FullyQualifiedName~ApplicationSearch|FullyQualifiedName~ApplicationOwnerInvariant|FullyQualifiedName~S2SAllowedTenantsLogging|FullyQualifiedName~S2SAppRegistrationAllowlist|FullyQualifiedName~RegistrarGuard|FullyQualifiedName~ActionResultsTests|FullyQualifiedName~ApiVersionTelemetryMiddleware|FullyQualifiedName~DefaultAuthenticationMiddleware"
```
Expected: `Passed: 212`, `Passed: 66`, `Passed: 64` (62 baseline + 2 startup tests), all `Failed: 0`.

---

### Task 5: Move `GetAccountIdAndToken` to an extension method (decision 2)

**Files:**
- Create: `SafeExchange.Tests\Tests\TokenHelperHttpExtensionsTests.cs`
- Create: `SafeExchange.Core.Functions\Utilities\TokenHelperHttpExtensions.cs`
- Modify: `SafeExchange.Core\Utilities\ITokenHelper.cs`, `SafeExchange.Core\Utilities\TokenHelper.cs`,
  `SafeExchange.Tests\Utilities\TestTokenHelper.cs`, `SafeExchange.Tests\Tests\ApplicationSearchTests.cs`,
  `SafeExchange.Tests\Tests\SecE1_NotificationIdorTests.cs`

The three call sites now live in `SafeExchange.Core.Functions`:
- `Middleware\TokenMiddlewareCore.cs:249`
- `Functions\SafeExchangeGroupSearch.cs:102`
- `Functions\SafeExchangeUserSearch.cs:102`

Each calls `this.tokenHelper.GetAccountIdAndToken(request, principal)`. Their namespaces are nested under
`SafeExchange.Core`, so the extension (namespace `SafeExchange.Core`) is in scope without edits.

- [ ] **Step 1: Write the failing test**

Create `SafeExchange.Tests\Tests\TokenHelperHttpExtensionsTests.cs`:

```csharp
/// <summary>
/// TokenHelperHttpExtensionsTests
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Claims;

    [TestFixture]
    public class TokenHelperHttpExtensionsTests
    {
        private const string ObjectId = "00000000-0000-0000-0000-0000000000b1";

        private const string TenantId = "00000000-0000-0000-0000-0000000000f1";

        private const string AccountId = ObjectId + "." + TenantId;

        private ITokenHelper tokenHelper = null!;

        [SetUp]
        public void Setup()
        {
            this.tokenHelper = new TokenHelper(new ConfigurationBuilder().Build(), NullLogger<TokenHelper>.Instance);
        }

        [Test]
        public void GetAccountIdAndToken_BearerHeader_ReturnsAccountIdAndToken()
        {
            var request = CreateRequest(instanceServices: null, authorization: "Bearer token-value");

            var result = this.tokenHelper.GetAccountIdAndToken(request, Principal());

            Assert.That(result.AccountId, Is.EqualTo(AccountId));
            Assert.That(result.Token, Is.EqualTo("token-value"));
        }

        [TestCase(null)]
        [TestCase("Basic dXNlcjpwYXNz")]
        [TestCase("Bearer")]
        public void GetAccountIdAndToken_NoBearerToken_ReturnsEmptyToken(string? authorization)
        {
            var request = CreateRequest(instanceServices: null, authorization);

            var result = this.tokenHelper.GetAccountIdAndToken(request, Principal());

            Assert.That(result.AccountId, Is.EqualTo(AccountId));
            Assert.That(result.Token, Is.Empty);
        }

        [Test]
        public void GetAccountIdAndToken_LogsUnderTheTokenHelperCategory()
        {
            var log = new RecordingLogger();
            var categories = new List<string>();
            var loggerFactory = new Mock<ILoggerFactory>();
            loggerFactory
                .Setup(f => f.CreateLogger(It.IsAny<string>()))
                .Callback<string>(categories.Add)
                .Returns(log);
            var instanceServices = new Mock<IServiceProvider>();
            instanceServices
                .Setup(s => s.GetService(typeof(ILoggerFactory)))
                .Returns(loggerFactory.Object);
            var request = CreateRequest(instanceServices.Object, "Bearer token-value");

            this.tokenHelper.GetAccountIdAndToken(request, Principal());

            Assert.That(categories, Is.EqualTo(new[] { "SafeExchange.Core.TokenHelper" }));
            Assert.That(
                log.Entries.Select(e => $"{e.Level} {e.Message}"),
                Is.EqualTo(new[] { $"Information GetAccountIdAndToken: AccountId [{AccountId}], token extracted" }));
        }

        private static TestHttpRequestData CreateRequest(IServiceProvider? instanceServices, string? authorization)
        {
            var request = new TestHttpRequestData(new TestFunctionContext { InstanceServices = instanceServices! });
            request.SetMethod("get");
            if (authorization is not null)
            {
                request.Headers.Add("Authorization", authorization);
            }

            return request;
        }

        private static ClaimsPrincipal Principal()
            => new(new ClaimsIdentity(new[]
            {
                new Claim("oid", ObjectId),
                new Claim("tid", TenantId),
            }));
    }
}
```

Each test builds its own `TestFunctionContext`, so the shared `TestFactory.FunctionContext` is never touched.

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --filter "FullyQualifiedName~TokenHelperHttpExtensionsTests"`
Expected: `Failed: 1, Passed: 4`. The failing test is `GetAccountIdAndToken_LogsUnderTheTokenHelperCategory` with
`Missing:  < "SafeExchange.Core.TokenHelper" >`. For now the call still binds to the interface member, which
logs through the injected `NullLogger`. The token and account-id tests already pass: they pin today's
behaviour, which the extension must keep.

- [ ] **Step 3: Create the extension**

Create `SafeExchange.Core.Functions\Utilities\TokenHelperHttpExtensions.cs`. `GetAccessToken` is the former
private `TokenHelper.GetAccessToken`, unchanged except for removing trailing whitespace. The log statement is
the former `TokenHelper` line. Decision D2 explains the logger resolution.

```csharp
/// <summary>
/// TokenHelperHttpExtensions
/// </summary>

namespace SafeExchange.Core
{
    using System.Net.Http.Headers;
    using System.Security.Claims;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;

    public static class TokenHelperHttpExtensions
    {
        /// <summary>
        /// Extract token and AccountId for later on-behalf token acquisition.
        /// </summary>
        /// <param name="tokenHelper">Token helper that resolves the account id.</param>
        /// <param name="request">Incoming http request.</param>
        /// <param name="principal"><see cref="ClaimsPrincipal">ClaimsPrincipal</see> for authenticated user.</param>
        /// <returns><see cref="AccountIdAndToken">AccountIdAndToken</see>.</returns>
        public static AccountIdAndToken GetAccountIdAndToken(this ITokenHelper tokenHelper, HttpRequestData request, ClaimsPrincipal principal)
        {
            var accessToken = GetAccessToken(request);
            var accountId = tokenHelper.GetAccountId(principal);

            // Logged under the TokenHelper category, where this trace was written before it moved here.
            var log = request.FunctionContext.InstanceServices?.GetService<ILoggerFactory>()?.CreateLogger<TokenHelper>()
                ?? (ILogger)NullLogger.Instance;
            log.LogInformation($"{nameof(GetAccountIdAndToken)}: AccountId [{accountId}], token {(string.IsNullOrEmpty(accessToken) ? "is empty" : "extracted")}");

            return new AccountIdAndToken(accountId, accessToken);
        }

        private static string GetAccessToken(HttpRequestData request)
        {
            if (!request.Headers.TryGetValues("Authorization", out var headerValues) ||
                !AuthenticationHeaderValue.TryParse(headerValues.FirstOrDefault(), out var authHeader))
            {
                return string.Empty;
            }

            if (authHeader != null && authHeader.Scheme.ToLower() == "bearer" && !string.IsNullOrEmpty(authHeader.Parameter))
            {
                return authHeader.Parameter;
            }

            return string.Empty;
        }
    }
}
```

- [ ] **Step 4: Change `SafeExchange.Core\Utilities\ITokenHelper.cs`**

Before:
```csharp
namespace SafeExchange.Core
{
    using Microsoft.Azure.Functions.Worker.Http;
    using System.Security.Claims;
```
After:
```csharp
namespace SafeExchange.Core
{
    using System.Security.Claims;
```

Before (the last member):
```csharp
        /// <summary>
        /// Extract token and AccountId for later on-behalf token acquisition.
        /// </summary>
        /// <param name="request">Incoming http request.</param>
        /// <param name="principal"><see cref="ClaimsPrincipal">ClaimsPrincipal</see> for authenticated user.</param>
        /// <returns><see cref="AccountIdAndToken">AccountIdAndToken</see>.</returns>
        public AccountIdAndToken GetAccountIdAndToken(HttpRequestData request, ClaimsPrincipal principal);
    }
}
```
After:
```csharp
        /// <summary>
        /// Get the account id ('{ObjectId}.{TenantId}') used for later on-behalf token acquisition.
        /// </summary>
        /// <param name="principal"><see cref="ClaimsPrincipal">ClaimsPrincipal</see> for authenticated user.</param>
        /// <returns>A string, representing the account id, or an empty string when the principal has no object id.</returns>
        public string GetAccountId(ClaimsPrincipal principal);
    }
}
```

- [ ] **Step 5: Change `SafeExchange.Core\Utilities\TokenHelper.cs`**

Edit 5a (usings). Before:
```csharp
    using System;
    using System.Configuration;
    using System.Net.Http.Headers;
    using System.Security.Claims;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Configuration;
```
After:
```csharp
    using System;
    using System.Configuration;
    using System.Security.Claims;
    using Microsoft.Extensions.Configuration;
```

Edit 5b (remove the HTTP member). Before:
```csharp
            return string.Empty;
        }

        /// <inheritdoc/>
        public AccountIdAndToken GetAccountIdAndToken(HttpRequestData request, ClaimsPrincipal principal)
        {
            var accessToken = GetAccessToken(request);
            var accountId = GetAccountId(principal);

            this.log.LogInformation($"{nameof(GetAccountIdAndToken)}: AccountId [{accountId}], token {(string.IsNullOrEmpty(accessToken) ? "is empty" : "extracted")}");

            return new AccountIdAndToken(accountId, accessToken);
        }

        public string? GetObjectId(ClaimsPrincipal? principal)
```
After:
```csharp
            return string.Empty;
        }

        public string? GetObjectId(ClaimsPrincipal? principal)
```

Edit 5c (remove `GetAccessToken`; make `GetAccountId` public. Its body does not change). Before:
```csharp
            return principal.HasClaim(claim => claim.Type.Equals(type));
        }

        private static string GetAccessToken(HttpRequestData request)
        {
            if (!request.Headers.TryGetValues("Authorization", out var headerValues) || 
                !AuthenticationHeaderValue.TryParse(headerValues.FirstOrDefault(), out var authHeader))
            {
                return string.Empty;
            }

            if (authHeader != null && authHeader.Scheme.ToLower() == "bearer" && !string.IsNullOrEmpty(authHeader.Parameter))
            {
                return authHeader.Parameter;
            }

            return string.Empty;
        }

        private string GetAccountId(ClaimsPrincipal principal)
```
After:
```csharp
            return principal.HasClaim(claim => claim.Type.Equals(type));
        }

        /// <inheritdoc/>
        public string GetAccountId(ClaimsPrincipal principal)
```

(The old `GetAccessToken` line `... out var headerValues) || ` ends with a trailing space. Match the before
snippet exactly.)

- [ ] **Step 6: Update the test doubles**

`SafeExchange.Tests\Utilities\TestTokenHelper.cs`. Before:
```csharp
namespace SafeExchange.Tests
{
    using Microsoft.Azure.Functions.Worker.Http;
    using SafeExchange.Core;
    using System.Security.Claims;

    public class TestTokenHelper : ITokenHelper
    {
        public TestTokenHelper()
        {
        }

        public AccountIdAndToken GetAccountIdAndToken(HttpRequestData request, ClaimsPrincipal principal)
        {
            var accountId = $"{this.GetTenantId(principal)}.{this.GetObjectId(principal)}";
            var accessToken = $"token:{accountId}";
            return new AccountIdAndToken(accountId, accessToken);
        }
```
After:
```csharp
namespace SafeExchange.Tests
{
    using SafeExchange.Core;
    using System.Security.Claims;

    public class TestTokenHelper : ITokenHelper
    {
        public TestTokenHelper()
        {
        }

        public string GetAccountId(ClaimsPrincipal principal)
        {
            return $"{this.GetTenantId(principal)}.{this.GetObjectId(principal)}";
        }
```

The account id keeps the old `{tid}.{oid}` order. `TestGraphDataProvider` keys its canned results on `AccountId`
only. The old fake token `token:{accountId}` is gone: the token now comes from the request's `Authorization`
header (empty for `TestFactory` requests), and no test reads it.

`SafeExchange.Tests\Tests\ApplicationSearchTests.cs`, nested `AppOnlyTokenHelper`. Before:
```csharp
            public string GetDisplayName(ClaimsPrincipal principal) => string.Empty;
            public AccountIdAndToken GetAccountIdAndToken(HttpRequestData request, ClaimsPrincipal principal)
                => new AccountIdAndToken(string.Empty, string.Empty);
        }
```
After:
```csharp
            public string GetDisplayName(ClaimsPrincipal principal) => string.Empty;
            public string GetAccountId(ClaimsPrincipal principal) => string.Empty;
        }
```

`SafeExchange.Tests\Tests\SecE1_NotificationIdorTests.cs`, nested `FakeTokenHelper`. Before:
```csharp
            public string? GetTenantId(ClaimsPrincipal? principal) => this.TenantId;
            public AccountIdAndToken GetAccountIdAndToken(HttpRequestData request, ClaimsPrincipal principal)
                => new AccountIdAndToken("account", "token");
        }
```
After:
```csharp
            public string? GetTenantId(ClaimsPrincipal? principal) => this.TenantId;
            public string GetAccountId(ClaimsPrincipal principal) => "account";
        }
```

Leave the other `using` lines in those two test files as they are. Both files still use Functions types elsewhere.

- [ ] **Step 7: Build and run the tests**

Run: `dotnet build SafeExchange.slnx --nologo -v q`
Expected: `0 Error(s)`.

Run: `git grep -n --untracked "GetAccountIdAndToken" -- "*.cs"`
(`--untracked` is needed because the two new files are not staged yet.)
Expected: only these 12 lines:
- `TokenHelperHttpExtensions.cs` (method and `nameof`)
- the three call sites in `SafeExchange.Core.Functions`
- 7 lines in `TokenHelperHttpExtensionsTests.cs`

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~TokenHelperHttpExtensionsTests|FullyQualifiedName~TokenHelperTests|FullyQualifiedName~ApplicationSearch|FullyQualifiedName~SecE1|FullyQualifiedName~SafeExchange.Tests.Contract"`
Expected: `Failed: 0, Passed: 241` (29 + 212).

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchange.Tests.Architecture"`
Expected: `Failed: 1, Passed: 1`. `CoreAssembly_ReferencesNoAzureFunctionsAssembly` now passes because Core
no longer uses any Functions type. `CoreProject_HasNoAzureFunctionsPackageReference` still fails with both
package names.

---

### Task 6: Remove the Functions packages from Core

**Files:**
- Modify: `SafeExchange.Core\SafeExchange.Core.csproj`

- [ ] **Step 1: Remove the two package references**

Before:
```xml
    <PackageReference Include="Azure.Core" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Core" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Http" />
    <PackageReference Include="Microsoft.Extensions.Caching.Memory" />
```
After:
```xml
    <PackageReference Include="Azure.Core" />
    <PackageReference Include="Microsoft.Extensions.Caching.Memory" />
```

Do not touch `Directory.Packages.props`. The new project and `SafeExchange.Functions` still use both versions.

- [ ] **Step 2: Build**

Run: `dotnet build SafeExchange.slnx --nologo -v q`
Expected: `0 Error(s)`. If Core fails here with `CS0246` on a `Microsoft.Azure.Functions` type, a Functions-typed
file was missed in Task 4. Move it with `git mv` like the others. Do not re-add the package.

- [ ] **Step 3: Run the architecture tests and watch them pass**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchange.Tests.Architecture"`
Expected: `Failed: 0, Passed: 2`.

---

### Task 7: Docs and CI check

**Files:**
- Modify: `README.md`, `docs\architecture.md`
- Check (no change): `.github\workflows\ci.yml`

- [ ] **Step 1: `README.md` project structure**

Before:
```
SafeExchange.Core/        — Domain models, business logic, permissions, and Azure integrations
SafeExchange.Functions/   — Azure Functions HTTP endpoints (the API layer)
```
After:
```
SafeExchange.Core/        — Domain models, business logic, permissions, and Azure integrations
SafeExchange.Core.Functions/ — Azure Functions-typed handlers, middleware and filters built on SafeExchange.Core
SafeExchange.Functions/   — Azure Functions HTTP endpoints (the API layer)
```

- [ ] **Step 2: `docs\architecture.md`**

Before (line 47):
```
Functions are thin wrappers — they authenticate the caller, then delegate to business logic handlers in `SafeExchange.Core`.
```
After:
```
Functions are thin wrappers — they authenticate the caller, then delegate to business logic handlers in `SafeExchange.Core.Functions`, which build on the host-agnostic domain logic in `SafeExchange.Core`.
```

Before (Project Structure, inside the `SafeExchange.Core/` tree):
```
├── DelayedTasks/          — Queue-based async task scheduling
├── Filters/               — Global authorization filters
├── Functions/             — Business logic handlers for each endpoint
├── Graph/                 — Microsoft Graph API client (user/group lookup)
├── Middleware/            — Authentication middleware (JWT validation)
├── Model/                — Domain entity classes
├── Permissions/           — Permission evaluation engine
├── Purging/              — Expiration and data cleanup logic
└── Requests/             — Request/response DTOs
```
After:
```
├── DelayedTasks/          — Queue-based async task scheduling
├── Functions/             — Timer/queue handlers (purge, webhook notifications)
├── Graph/                 — Microsoft Graph API client (user/group lookup)
├── Model/                — Domain entity classes
├── Permissions/           — Permission evaluation engine
├── Purging/              — Expiration and data cleanup logic
└── Requests/             — Request/response DTOs

SafeExchange.Core.Functions/  — Azure Functions-typed code (references SafeExchange.Core, never the reverse)
├── Functions/             — HTTP business logic handlers for each endpoint
├── Middleware/            — Authentication middleware (JWT validation)
├── Requests/Filters/      — Global authorization filters
├── Utilities/             — ActionResults, TokenHelperHttpExtensions
└── SafeExchangeFunctionsStartup.cs — Worker middleware and Functions-typed DI registrations
```

The full consumer migration note is phase 7's job (`docs/mcp.md` and related docs). This phase only keeps the
existing structure sections accurate.

- [ ] **Step 3: Confirm CI needs no change**

Run: `Select-String -Path .github\workflows\ci.yml -Pattern "dotnet (restore|build|test|publish)"`
Expected exactly these four commands:
- `dotnet restore SafeExchange.slnx`
- `dotnet build SafeExchange.slnx -c Release --no-restore`
- `dotnet test SafeExchange.Tests/SafeExchange.Tests.csproj -c Release --no-build`
- `dotnet publish SafeExchange.Functions -c Release -o publish`

All four reach the new project through the `.slnx` or the project references, so `ci.yml` stays as it is.
Mirror the CI build locally:

Run: `dotnet build SafeExchange.slnx -c Release --nologo -v q`
Expected: `0 Error(s)`.

---

### Task 8: Final verification and commit

**Files:** none new.

- [ ] **Step 1: Full rebuild, no new warnings**

Run: `dotnet build SafeExchange.slnx --nologo -v q --no-incremental`
Expected: `0 Error(s)` and the same warning count as Task 0 Step 2 (`705 Warning(s)`). If the count differs,
diff the unique warning lines against a baseline build of the Task 0 commit and fix every new warning in the changed
files.

- [ ] **Step 2: Contract and architecture tests**

Run: `dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchange.Tests.Contract|FullyQualifiedName~SafeExchange.Tests.Architecture"`
Expected: `Failed: 0, Passed: 214` (212 contract + 2 architecture).

- [ ] **Step 3: InMemory regression fixtures**

Run:
```powershell
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SecC|FullyQualifiedName~SecE|FullyQualifiedName~SecF|FullyQualifiedName~EffectivePermissions|FullyQualifiedName~TokenClassification|FullyQualifiedName~SecA"
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~SafeExchangeFunctionsStartupTests|FullyQualifiedName~TokenHelperHttpExtensionsTests|FullyQualifiedName~TokenHelperTests|FullyQualifiedName~SecB|FullyQualifiedName~ApplicationSearch|FullyQualifiedName~ApplicationOwnerInvariant|FullyQualifiedName~S2SAllowedTenantsLogging|FullyQualifiedName~S2SAppRegistrationAllowlist|FullyQualifiedName~RegistrarGuard|FullyQualifiedName~ActionResultsTests|FullyQualifiedName~ApiVersionTelemetryMiddleware|FullyQualifiedName~DefaultAuthenticationMiddleware"
```
Expected: `Passed: 66` and `Passed: 69` (62 baseline + 2 startup + 5 extension), all `Failed: 0`.

- [ ] **Step 4 (optional, broader): every non-Cosmos test**

Run:
```powershell
$cosmos = 'AdminSecretsTests','AdminUserDetailTests','AuditAnchorWriteTests','ContentCommitTests','GroupsTests','OrphanedSecretManagerTests','PinnedGroupsTests','PinnedSecretsTests','SecretAccessAuditTests','SecretAccessGiveUpTests','SecretAccessPatchTests','SecretAccessRequestTests','SecretAccessTests','SecretContentMetaTests','SecretMetaTests','SecretStreamTests','TelemetryIdBackfillMigrationTests','UserTests'
$filter = ($cosmos | ForEach-Object { "FullyQualifiedName!~SafeExchange.Tests.$_" }) -join '&'
dotnet test SafeExchange.Tests\SafeExchange.Tests.csproj --nologo --no-build --filter $filter
```
Expected: `Failed: 0, Passed: 618`. The baseline at `7745dfa` is 609, plus 9 new tests. The count includes
the 17 `CosmosCertificateValidationTests`, which need no emulator. The excluded fixtures need the Cosmos
emulator and run in the final phase.

- [ ] **Step 5: Review the staged change**

Run: `git add -A; git --no-pager diff --cached -M --name-status | Select-String "^R" | ForEach-Object { ($_ -split "\t")[0] } | Group-Object | Select-Object Name, Count`
Expected: one row, `R100 42`. The moves are pure.

Run: `git --no-pager diff --cached -M --stat | Select-Object -Last 1`
Expected: `61 files changed` (42 moves + 19 lines below), or `62 files changed` if this plan file is staged too.

Run: `git status --short | Select-String -NotMatch "^R "`
Expected exactly these 19 lines (`A` = new, `M` = modified):
```
M  README.md
A  SafeExchange.Core.Functions/SafeExchange.Core.Functions.csproj
A  SafeExchange.Core.Functions/SafeExchangeFunctionsStartup.cs
A  SafeExchange.Core.Functions/Utilities/TokenHelperHttpExtensions.cs
M  SafeExchange.Core/SafeExchange.Core.csproj
M  SafeExchange.Core/SafeExchangeStartup.cs
M  SafeExchange.Core/Utilities/ITokenHelper.cs
M  SafeExchange.Core/Utilities/TokenHelper.cs
M  SafeExchange.Functions/Program.cs
M  SafeExchange.Functions/SafeExchange.Functions.csproj
A  SafeExchange.Tests/Architecture/CoreHostAgnosticTests.cs
M  SafeExchange.Tests/SafeExchange.Tests.csproj
M  SafeExchange.Tests/Tests/ApplicationSearchTests.cs
A  SafeExchange.Tests/Tests/SafeExchangeFunctionsStartupTests.cs
M  SafeExchange.Tests/Tests/SecE1_NotificationIdorTests.cs
A  SafeExchange.Tests/Tests/TokenHelperHttpExtensionsTests.cs
M  SafeExchange.Tests/Utilities/TestTokenHelper.cs
M  SafeExchange.slnx
M  docs/architecture.md
```
This plan file (`docs/superpowers/plans/2026-11-21-mcp-p2-project-split.md`) is also listed as `A` if it was not
committed earlier. Commit it with the phase.

- [ ] **Step 6: Commit**

```powershell
git commit -m "refactor: move Functions-typed code from SafeExchange.Core into SafeExchange.Core.Functions" -m "New library SafeExchange.Core.Functions (references Core) holds the HTTP handlers, middleware, request filters, ActionResults and the Functions part of startup; 42 files moved with namespaces unchanged. Core drops the Microsoft.Azure.Functions.* packages; an architecture test enforces it. ITokenHelper.GetAccountIdAndToken becomes an HttpRequestData extension (same log category and message); ITokenHelper gains GetAccountId. Startup split keeps the DI graph, telemetry-initializer order and middleware order identical." -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

Run: `git --no-pager log --oneline -1`
Expected: `<sha> refactor: move Functions-typed code from SafeExchange.Core into SafeExchange.Core.Functions`.

---

## Self-review (done while writing)

- **Spec coverage:**
  - §Projects: Core has no Functions packages (Tasks 4 and 6), and Functions-typed code is in
    `SafeExchange.Core.Functions` (Task 4).
  - §Testing 2: architecture test (Tasks 1, 5 and 6).
  - Master plan decision 1: namespaces are unchanged (Task 4, R100).
  - Decision 2: Task 5.
  - Decision 3: Task 3, with the call-order deviation explained in D3.
  - Decision 4: Task 4, with the scope corrected in D4.
  - §Consumer migration note: a consumer adds a reference to `SafeExchange.Core.Functions`. Namespaces are
    unchanged, so in this phase it only also calls `SafeExchangeFunctionsStartup.ConfigureWorkerDefaults` and
    `ConfigureFunctionsServices`, or registers its own middleware. Phase 7 documents this.
- **Placeholders:** none. Every new file is given in full. Every edit has exact before/after text. Every move has
  its `git mv` line.
- **Type consistency:** `SafeExchangeFunctionsStartup.{IsHttpTrigger, ConfigureWorkerDefaults,
  ConfigureFunctionsServices}`, `ITokenHelper.GetAccountId(ClaimsPrincipal)` and
  `TokenHelperHttpExtensions.GetAccountIdAndToken(this ITokenHelper, HttpRequestData, ClaimsPrincipal)` are named
  the same in the code, the tests and `Program.cs`.

## Risks

- **Cosmos-backed fixtures were not run in this phase.** `UserTests` and `GroupsTests` reach
  `GetAccountIdAndToken` through `TokenMiddlewareCore` with `TestTokenHelper`. The account id format is
  unchanged (`{tid}.{oid}`) and the fake token was never read, so no change is expected. The full suite in the
  final phase confirms it.
- **Logger fallback.** In production the trace depends on `FunctionContext.InstanceServices` providing
  `ILoggerFactory`. The isolated worker always does, but a consumer host that builds `HttpRequestData` without
  it loses only this trace, silently.
- **Broad `InternalsVisibleTo`.** `SafeExchange.Core.Functions` can see all Core internals. Phase 4 moves logic
  into Core services and should shrink this surface. It is recorded here rather than fixed, because making the
  model `ToDto` methods public would widen Core's public API.
- **Telemetry initializer order depends on call order in `Program.cs`.** A consumer that calls `ConfigureServices`
  first gets a different, functionally equivalent order: the four initializers write disjoint fields.
  `SafeExchangeFunctionsStartupTests` pins our order.
