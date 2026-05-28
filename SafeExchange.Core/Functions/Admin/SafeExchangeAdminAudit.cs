/// <summary>
/// SafeExchangeAdminAudit — admin search of audit anchors by secret name
/// substring. Anchors survive secret deletion (DeletedAt is set on purge),
/// so the search naturally returns historical entries; the IsHistorical flag
/// on the output makes that explicit for the UI.
/// </summary>

namespace SafeExchange.Core.Functions.Admin
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
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
    }
}
