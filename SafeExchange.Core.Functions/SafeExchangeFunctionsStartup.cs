/// <summary>
/// SafeExchangeFunctionsStartup
/// </summary>

namespace SafeExchange.Core
{
    using Microsoft.ApplicationInsights.Extensibility;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Functions.Mcp;
    using SafeExchange.Core.Middleware;
    using SafeExchange.Core.Services;

    public static class SafeExchangeFunctionsStartup
    {
        public static bool IsHttpTrigger(FunctionContext context)
            => context.FunctionDefinition.InputBindings.Values
                .First(a => a.Type.EndsWith("Trigger")).Type == "httpTrigger";

        /// <summary>
        /// HTTP triggers that need a bearer token: all except the anonymous protected-resource metadata.
        /// </summary>
        public static bool IsAuthenticatedHttpTrigger(FunctionContext context)
            => IsHttpTrigger(context)
                && !string.Equals(context.FunctionDefinition.Name, McpRoutes.ProtectedResourceMetadataFunctionName, StringComparison.Ordinal);

        public static void ConfigureWorkerDefaults(HostBuilderContext context, IFunctionsWorkerApplicationBuilder builder)
        {
            UseSafeExchangeAuthentication(builder);
            UseSafeExchangeMiddleware(builder);
        }

        /// <summary>
        /// Adds the bearer-token validation (<see cref="DefaultAuthenticationMiddleware"/>). A host with its own
        /// authentication skips this and stores the validated principal under
        /// <see cref="DefaultAuthenticationMiddleware.ClaimsPrincipalKey"/> itself.
        /// </summary>
        public static void UseSafeExchangeAuthentication(IFunctionsWorkerApplicationBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.UseWhen<DefaultAuthenticationMiddleware>(IsAuthenticatedHttpTrigger);
        }

        /// <summary>
        /// Adds everything that follows authentication: token filters, session correlation, API version telemetry.
        /// </summary>
        public static void UseSafeExchangeMiddleware(IFunctionsWorkerApplicationBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.UseWhen<TokenFilterMiddleware>(IsAuthenticatedHttpTrigger);

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

            // Factories pick the service-based constructors; the pre-service ones would also be satisfiable.
            services.AddScoped(sp => new SafeExchangeSecretMeta(sp.GetRequiredService<ICallerResolver>(), sp.GetRequiredService<SecretService>()));
            services.AddScoped(sp => new SafeExchangeSecretContentMeta(sp.GetRequiredService<ICallerResolver>(), sp.GetRequiredService<ContentService>()));
            services.AddScoped(sp => new SafeExchangeSecretStream(sp.GetRequiredService<ICallerResolver>(), sp.GetRequiredService<ChunkService>()));
            services.AddScoped(sp => new SafeExchangeContentCommit(sp.GetRequiredService<ICallerResolver>(), sp.GetRequiredService<ContentService>()));
            services.AddScoped(sp => new SafeExchangeAccess(sp.GetRequiredService<ICallerResolver>(), sp.GetRequiredService<AccessService>()));
            services.AddScoped(sp => new SafeExchangeSecretAudit(sp.GetRequiredService<ICallerResolver>(), sp.GetRequiredService<AuditQueryService>()));
            services.AddScoped<McpHandler>();
            services.AddScoped<OAuthProtectedResourceHandler>();

            // Stamps customDimensions.saex.sessionId on every telemetry
            // item emitted within a request that carried the header. Works
            // in tandem with SessionCorrelationMiddleware (which sets the
            // AsyncLocal for the duration of the request handler).
            services.AddSingleton<ITelemetryInitializer, SessionCorrelationTelemetryInitializer>();
            services.AddSingleton<ITelemetryInitializer, ApiVersionTelemetryInitializer>();
        }
    }
}
