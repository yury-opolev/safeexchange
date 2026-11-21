# Consumer Migration Note

For a project that uses `SafeExchange.Core` with **its own Azure Functions app and its own authentication**. The MCP work split `SafeExchange.Core` in two, so such a consumer needs a few changes to keep building and running. Namespaces did not change, so most code only needs a new project reference.

| Project | Contents |
|---------|----------|
| `SafeExchange.Core` | Host-agnostic: model, DbContext, permissions, audit, storage, crypto, Graph, purger, configuration, `Callers/`, `Services/`, `Mcp/`. It no longer references any `Microsoft.Azure.Functions.*` package |
| `SafeExchange.Core.Functions` | Functions-typed: HTTP handlers (`Functions/`), middleware, request filters, `ActionResults` and other HTTP utilities, `SafeExchangeFunctionsStartup`, `McpHandler`, `OAuthProtectedResourceHandler` |

## 1. Project references

- Add a reference to `SafeExchange.Core.Functions` (it brings `SafeExchange.Core` and the Functions worker packages `Microsoft.Azure.Functions.Worker.Core` and `Microsoft.Azure.Functions.Worker.Extensions.Http`).
- Keep your own Functions worker packages; `SafeExchange.Core` no longer provides them. It now references `ModelContextProtocol.Core`.
- Handlers, middleware, filters, `ActionResults` and `GlobalFilters` keep their namespaces (`SafeExchange.Core.Functions`, `SafeExchange.Core.Middleware`, `SafeExchange.Core.Filters`, `SafeExchange.Core`), so `using` directives stay as they are.

## 2. Startup order

`SafeExchangeStartup.ConfigureWorkerDefaults` and `IsHttpTrigger` moved to `SafeExchangeFunctionsStartup` (`SafeExchange.Core.Functions`, namespace `SafeExchange.Core`). Our `Program.cs`:

```csharp
var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults(SafeExchangeFunctionsStartup.ConfigureWorkerDefaults)
    .ConfigureLogging(SafeExchangeStartup.ConfigureWorkerLogging)
    .ConfigureAppConfiguration(SafeExchangeStartup.ConfigureAppConfiguration)
    .ConfigureServices((hostBuilderContext, serviceCollection) =>
    {
        SafeExchangeFunctionsStartup.ConfigureFunctionsServices(serviceCollection);
        SafeExchangeStartup.ConfigureServices(hostBuilderContext.Configuration, serviceCollection);
    })
    .Build();
```

Call **`ConfigureFunctionsServices` before `SafeExchangeStartup.ConfigureServices`**. `ConfigureFunctionsServices` registers what Core no longer can:

- `ITokenMiddlewareCore`, `GlobalFilters`;
- the six service-backed handlers (`SafeExchangeSecretMeta`, `SafeExchangeSecretContentMeta`, `SafeExchangeSecretStream`, `SafeExchangeContentCommit`, `SafeExchangeAccess`, `SafeExchangeSecretAudit`) and `McpHandler` / `OAuthProtectedResourceHandler`;
- the `SessionCorrelationTelemetryInitializer` and `ApiVersionTelemetryInitializer` telemetry initializers.

Telemetry initializers run in registration order, and these two are meant to run ahead of the Core ones. Calling `ConfigureServices` first still works (the initializers write disjoint fields) but changes the order. `SafeExchangeFunctionsStartupTests` pins ours.

## 3. Middleware composition

`SafeExchangeFunctionsStartup.ConfigureWorkerDefaults` is two steps, each also callable on its own:

| Method | Registers (in this order) |
|--------|---------------------------|
| `UseSafeExchangeAuthentication(builder)` | `DefaultAuthenticationMiddleware`, validates the JWT |
| `UseSafeExchangeMiddleware(builder)` | `TokenFilterMiddleware` (post-authentication token filters), `SessionCorrelationMiddleware`, `ApiVersionTelemetryMiddleware` |

`ConfigureWorkerDefaults(context, builder)` calls both, so a host that uses our authentication keeps passing it to `ConfigureFunctionsWorkerDefaults`. The registration is equivalent to:

```csharp
builder.UseWhen<DefaultAuthenticationMiddleware>(IsAuthenticatedHttpTrigger);   // validates the JWT
builder.UseWhen<TokenFilterMiddleware>(IsAuthenticatedHttpTrigger);             // post-authentication token filters
builder.UseWhen<SessionCorrelationMiddleware>(IsHttpTrigger);
builder.UseWhen<ApiVersionTelemetryMiddleware>(IsHttpTrigger);
```

`IsAuthenticatedHttpTrigger` is every HTTP trigger except the anonymous MCP protected-resource metadata function.

A consumer with its own authentication does **not** use `ConfigureWorkerDefaults` (it would add our `DefaultAuthenticationMiddleware`). It registers its own authentication middleware first and then calls `UseSafeExchangeMiddleware`:

```csharp
.ConfigureFunctionsWorkerDefaults(builder =>
{
    builder.UseWhen<MyAuthenticationMiddleware>(SafeExchangeFunctionsStartup.IsAuthenticatedHttpTrigger);
    SafeExchangeFunctionsStartup.UseSafeExchangeMiddleware(builder);
})
```

The one contract between the two halves: your middleware must store the validated `ClaimsPrincipal` in `FunctionContext.Items` under **`DefaultAuthenticationMiddleware.ClaimsPrincipalKey`** (`"ClaimsPrincipal"`). `TokenFilterMiddleware` and `context.GetPrincipal()` (used by the triggers) read it from there. On a `401`, build the response with `UnauthorizedResponse.CreateAsync(request, message, includeResourceMetadata)` to keep the REST body and, with MCP enabled, the `WWW-Authenticate` challenge.

`ConfigureFunctionsServices` must still run before `SafeExchangeStartup.ConfigureServices` (section 2), whichever way the middleware chain is composed.

Both authentication middleware constructors now take `IOptionsMonitor<Features>` (to decide whether the `401` carries the MCP challenge): `DefaultAuthenticationMiddleware(ITokenValidationParametersProvider, IOptionsMonitor<Features>, ILogger<…>)` and `TokenFilterMiddleware(IOptionsMonitor<Features>, ILogger<…>)`. The worker builds them from DI, so only code that constructs them by hand needs to change.

## 4. Handlers

The six handlers MCP needs are now thin adapters over services in `SafeExchange.Core.Services` (`SecretService`, `ContentService`, `ChunkService`, `AccessService`, `AuditQueryService`). Their `Run(request, …, principal, log)` signatures are unchanged. The other handlers moved unchanged.

| Constructor | Use |
|-------------|-----|
| DI constructor, for example `SafeExchangeSecretMeta(ICallerResolver, SecretService)` | **Recommended.** Resolve the handler from DI (`ConfigureFunctionsServices` registers it) or construct it with services from DI |
| Legacy convenience constructor (the previous parameter list) | Still compiles. It resolves callers with the same `CallerResolver` logic (the global-group gate comes from the `GlobalFilters` you pass, so its log lines keep the `GlobalFilters` category) and builds a service with `NullLogger`, so the service's log lines are dropped. Meant for tests and hand-built code |

Services take a `Caller` first and return `ServiceResult<T>`; the HTTP adapters map the result to the same status code and `BaseResponseObject<T>` JSON as before.

## 5. `ICallerResolver`

`ICallerResolver.ResolveAsync(principal, channel, clientLabel)` turns the authenticated principal into a `Caller` (subject type, subject id, display name, channel, client label), or into a denial answered like REST. The default `CallerResolver` (registered scoped by `SafeExchangeStartup.ConfigureServices`) applies the global access-group gate, resolves the subject with `SubjectHelper` and rejects unregistered or disabled applications.

If your tokens carry different claims, register your own implementation **after** `SafeExchangeStartup.ConfigureServices`; a registration made before it is overridden by the default.

```csharp
SafeExchangeStartup.ConfigureServices(configuration, services);
services.AddScoped<ICallerResolver, MyCallerResolver>();
```

## 6. Interface changes for implementers

If you implement these interfaces, update the implementations; callers are source-compatible.

| Interface | Change |
|-----------|--------|
| `IAuditWriter.AppendAsync` | New optional parameter `AuditOrigin? origin = null`, placed **before** `CancellationToken ct`. A caller passing the token positionally must now pass it by name (`ct: token`) |
| `IPurger.PurgeAsync(secretId, dbContext, auditWriter, actorType, actorId, auditRetentionDays, …)` | New optional parameter `AuditOrigin? origin = null` |
| `ITokenHelper` | `GetAccountIdAndToken(HttpRequestData, ClaimsPrincipal)` is no longer a member (Core has no HTTP types); it is the extension method `TokenHelperHttpExtensions.GetAccountIdAndToken` in `SafeExchange.Core.Functions`. New member `string GetAccountId(ClaimsPrincipal)` returns `{ObjectId}.{TenantId}` (empty when the principal has no object id) |

`AuditOrigin` (`SafeExchange.Core.Audit`) carries the channel and client label of an audited action. `AuditOrigin.None` (the default) leaves events and their hashes exactly as before; only MCP callers record an origin.

## 7. Configuration

`Features.McpEndpoint` (default `false`) and `Authentication:McpScopes` (optional) are new; see [mcp.md](mcp.md#enabling). `SafeExchangeStartup.ConfigureServices` already registers the MCP tools, dispatcher and protected-resource facts (`AddSafeExchangeMcp()`).

## 8. Exposing MCP (optional)

Add the two triggers to your Functions app. `McpHandler` and `OAuthProtectedResourceHandler` come from DI (`ConfigureFunctionsServices`); the route constants are in `SafeExchange.Core.Functions.Mcp.McpRoutes`.

```csharp
public class SafeMcp
{
    private readonly McpHandler handler;
    private readonly ILogger log;

    public SafeMcp(McpHandler handler, ILogger<SafeMcp> log)
    {
        this.handler = handler;
        this.log = log;
    }

    // GET and DELETE are routed here only to answer 405.
    [Function(McpRoutes.McpFunctionName)]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", "get", "delete", Route = McpRoutes.Mcp)]
        HttpRequestData request)
    {
        var principal = request.FunctionContext.GetPrincipal();
        return await this.handler.Run(request, principal, this.log);
    }
}

public class SafeOAuthMetadata
{
    private readonly OAuthProtectedResourceHandler handler;

    public SafeOAuthMetadata(OAuthProtectedResourceHandler handler)
    {
        this.handler = handler;
    }

    // Anonymous: SafeExchangeFunctionsStartup.IsAuthenticatedHttpTrigger excludes this function from authentication.
    [Function(McpRoutes.ProtectedResourceMetadataFunctionName)]
    public Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = McpRoutes.ProtectedResourceMetadata)]
        HttpRequestData request)
        => this.handler.Run(request);
}
```

Requirements:

- Your authentication middleware must skip the metadata function (`McpRoutes.ProtectedResourceMetadataFunctionName`), which is what `IsAuthenticatedHttpTrigger` does.
- The metadata is derived from the `Authentication` section (`Authority`, `ValidAudiences`, `McpScopes`).
- `McpRoutes.RoutePrefix` is `api` (the Functions default). If your `host.json` sets another route prefix, the challenge URL and `file_transfer_info` URLs will be wrong.
- If your principals' claims differ from ours, register your own `ICallerResolver` (section 5); the MCP handler resolves the caller through it for every request.

## Migration checklist

1. Reference `SafeExchange.Core.Functions`; keep your own Functions worker packages.
2. Replace `SafeExchangeStartup.ConfigureWorkerDefaults` / `IsHttpTrigger` with the `SafeExchangeFunctionsStartup` equivalents, or, with your own authentication, register it and call `UseSafeExchangeMiddleware` (section 3).
3. Call `SafeExchangeFunctionsStartup.ConfigureFunctionsServices` before `SafeExchangeStartup.ConfigureServices`.
4. Store the principal under `DefaultAuthenticationMiddleware.ClaimsPrincipalKey` in your authentication middleware.
5. Resolve handlers from DI instead of constructing them by hand.
6. Register your own `ICallerResolver` after `ConfigureServices` if your claims differ.
7. Update custom `IAuditWriter`, `IPurger` and `ITokenHelper` implementations (section 6).
8. Optionally add the two MCP triggers and enable `Features.McpEndpoint`.
