/// <summary>
/// SafeExchangeSecretAudit — GET /v2/secret/{secretId}/audit handler.
/// Returns the live audit instance's events for callers with Read on the secret.
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Services;
    using SafeExchange.Core.Telemetry;
    using System;
    using System.Security.Claims;
    using System.Threading.Tasks;

    /// <summary>
    /// HTTP adapter for <c>GET /secret/{secretId}/audit</c> over <see cref="AuditQueryService"/>.
    /// </summary>
    public class SafeExchangeSecretAudit
    {
        private readonly ICallerResolver callerResolver;

        private readonly AuditQueryService auditQueryService;

        public SafeExchangeSecretAudit(ICallerResolver callerResolver, AuditQueryService auditQueryService)
        {
            this.callerResolver = callerResolver ?? throw new ArgumentNullException(nameof(callerResolver));
            this.auditQueryService = auditQueryService ?? throw new ArgumentNullException(nameof(auditQueryService));
        }

        /// <summary>
        /// Pre-service constructor, kept for code that builds handlers by hand: resolves callers through
        /// <paramref name="globalFilters"/> exactly as before and builds the service itself.
        /// </summary>
        public SafeExchangeSecretAudit(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, IPermissionsManager permissionsManager)
            : this(
                  new GlobalFiltersCallerResolver(globalFilters, tokenHelper, dbContext),
                  new AuditQueryService(dbContext, permissionsManager))
        {
        }

        public async Task<HttpResponseData> Run(HttpRequestData request, string secretId, ClaimsPrincipal principal, ILogger log)
        {
            var resolution = await this.callerResolver.ResolveAsync(principal, CallerChannels.Rest, null);
            if (!resolution.IsAllowed)
            {
                return await resolution.Denial.ToResponseAsync(request);
            }

            var caller = resolution.Caller;
            log.LogInformation($"{nameof(SafeExchangeSecretAudit)} triggered for '{secretId}' by {caller.SubjectType} (tid {TelemetryContext.Current}).");

            return await ActionResults.ServiceCallAsync(
                request,
                async () => await (await this.auditQueryService.ReadAsync(caller, secretId, AuditPageQuery.Parse(request.Url?.Query))).ToResponseAsync(request),
                nameof(SafeExchangeSecretAudit), log);
        }

        // Shared with SafeExchangeAdminSecretAudit, which passes the raw request URL query.
        internal static Task<SecretAuditPageOutput> BuildAuditPageAsync(
            SafeExchangeDbContext dbContext, string auditInstanceId, string queryString)
            => AuditQueryService.BuildAuditPageAsync(dbContext, auditInstanceId, AuditPageQuery.Parse(queryString));
    }
}
