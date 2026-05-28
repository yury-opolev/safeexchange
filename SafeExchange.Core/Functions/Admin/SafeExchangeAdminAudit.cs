/// <summary>
/// SafeExchangeAdminAudit — admin-gated audit surface. Two reads:
///
/// * RunSearchAnchors (GET /admin/audit) — paged anchor search by secret-name
///   substring. Includes anchors of purged secrets (IsHistorical=true) until
///   retention expires; that's the whole reason anchors outlive secrets.
/// * RunInstance (GET /admin/audit/{auditInstanceId}) — drill-down to events
///   for one anchor. AuditInstanceId is the immutable identity of an audit
///   run — works for live and purged secrets, and avoids the ambiguity that
///   secret-name re-use after delete+recreate would introduce.
///
/// Per-secret IsAuthorizedAsync(Read) is intentionally omitted on the drill-
/// down — admins do oversight that may target secrets they shouldn't be
/// readers of.
/// </summary>

namespace SafeExchange.Core.Functions.Admin
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Model.Dto.Output;
    using System;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class SafeExchangeAdminAudit
    {
        private readonly SafeExchangeDbContext dbContext;
        private readonly GlobalFilters globalFilters;
        private readonly IOptionsMonitor<Limits> limits;

        public SafeExchangeAdminAudit(
            SafeExchangeDbContext dbContext,
            GlobalFilters globalFilters,
            IOptionsMonitor<Limits> limits)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        }

        public async Task<HttpResponseData> RunSearchAnchors(HttpRequestData request, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetAdminFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var secretName = request.Query["secretName"];
                var (page, pageSize) = PaginationHelper.Parse(request, this.limits.CurrentValue);

                IQueryable<Model.SecretAuditAnchor> baseQuery = this.dbContext.SecretAuditAnchors;
                if (!string.IsNullOrWhiteSpace(secretName))
                {
                    var qLower = secretName.ToLowerInvariant();
                    baseQuery = baseQuery.Where(a => a.SecretObjectName.ToLower().Contains(qLower));
                }

                var total = await baseQuery.CountAsync();
                var page0 = Math.Max(0, page);
                // Anchors of deleted (purged) secrets are explicitly included; the
                // UI distinguishes them via IsHistorical. Order newest-first so the
                // most recent activity (including recently deleted secrets) is first.
                var anchors = await baseQuery
                    .OrderByDescending(a => a.CreatedAt)
                    .Skip(page0 * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                var items = anchors.Select(a => new SecretAuditAnchorOutput
                {
                    AuditInstanceId = a.AuditInstanceId,
                    SecretObjectName = a.SecretObjectName,
                    CreatedAt = a.CreatedAt,
                    CreatedBy = a.CreatedBy,
                    DeletedAt = a.DeletedAt,
                    DeletedBy = a.DeletedBy,
                    RetentionExpiresAt = a.RetentionExpiresAt,
                    IsHistorical = a.DeletedAt.HasValue,
                }).ToList();

                var result = new PaginatedResult<SecretAuditAnchorOutput>
                {
                    Items = items, Page = page0, PageSize = pageSize, Total = total,
                    HasMore = (page0 + 1) * pageSize < total,
                };
                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<PaginatedResult<SecretAuditAnchorOutput>> { Status = "ok", Result = result });
            }, nameof(RunSearchAnchors), log);
        }

        public async Task<HttpResponseData> RunInstance(HttpRequestData request, string auditInstanceId, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetAdminFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResult ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            log.LogInformation($"{nameof(RunInstance)} triggered for instance '{auditInstanceId}'.");

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var anchor = await this.dbContext.SecretAuditAnchors
                    .FirstOrDefaultAsync(a => a.AuditInstanceId == auditInstanceId);
                if (anchor is null)
                {
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = $"Audit instance '{auditInstanceId}' not found." });
                }

                var page = await SafeExchangeSecretAudit.BuildAuditPageAsync(
                    this.dbContext, auditInstanceId, request.Url?.Query ?? string.Empty);

                var output = new AdminSecretAuditPageOutput
                {
                    AuditInstanceId = anchor.AuditInstanceId,
                    SecretObjectName = anchor.SecretObjectName,
                    CreatedAt = anchor.CreatedAt,
                    CreatedBy = anchor.CreatedBy,
                    DeletedAt = anchor.DeletedAt,
                    DeletedBy = anchor.DeletedBy,
                    RetentionExpiresAt = anchor.RetentionExpiresAt,
                    IsHistorical = anchor.DeletedAt.HasValue,
                    Page = page,
                };

                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<AdminSecretAuditPageOutput> { Status = "ok", Result = output });
            }, nameof(RunInstance), log);
        }
    }
}
