/// <summary>
/// SafeExchangeAdminSecretAudit — admin-gated audit-detail endpoint.
/// Returns paged audit events for any secret where audit is enabled,
/// regardless of whether the admin has Read on the secret. The per-secret
/// permission check is intentionally omitted (admins do the oversight job
/// that may target secrets they shouldn't be readers of).
///
/// Shares pagination/merger logic with the user-side SafeExchangeSecretAudit
/// via SafeExchangeSecretAudit.BuildAuditPageAsync.
/// </summary>

namespace SafeExchange.Core.Functions.Admin
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Model.Dto.Output;
    using System;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class SafeExchangeAdminSecretAudit
    {
        private readonly SafeExchangeDbContext dbContext;
        private readonly GlobalFilters globalFilters;

        public SafeExchangeAdminSecretAudit(SafeExchangeDbContext dbContext, GlobalFilters globalFilters)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
        }

        public async Task<HttpResponseData> Run(HttpRequestData request, string secretId, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetAdminFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResult ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            log.LogInformation($"{nameof(SafeExchangeAdminSecretAudit)} triggered for '{secretId}'.");

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var metadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
                if (metadata is null)
                {
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretId}' does not exist." });
                }

                var page = await SafeExchangeSecretAudit.BuildAuditPageAsync(this.dbContext, metadata, request);
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK,
                    new BaseResponseObject<SecretAuditPageOutput> { Status = "ok", Result = page });
            }, nameof(SafeExchangeAdminSecretAudit), log);
        }
    }
}
