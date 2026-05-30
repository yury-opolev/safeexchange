/// <summary>
/// SafeExchangeAdminSecrets — admin-only read-only surface for ObjectMetadata.
///
/// * RunList   — GET v2/admin/secret-list
///               Paginated; supports q (name search), page, pageSize,
///               sortBy (name|created|lastAccessed, default created),
///               sortDir (asc|desc, default desc),
///               accessedBefore (ISO date).
///               Fully server-side paginated — no client-side materialisation.
///
/// * RunDetail — GET v2/admin/secret/{secretName}
///               Full metadata (no content bytes or chunk data).
///
/// * RunAccess — GET v2/admin/secret/{secretName}/access
///               All SubjectPermissions rows for the secret.
///
/// Never returns content bytes, chunk data, or access tickets.
/// </summary>

namespace SafeExchange.Core.Functions.Admin
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using System;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class SafeExchangeAdminSecrets
    {
        private static readonly DateTime DefaultDateTime = default;

        private readonly SafeExchangeDbContext dbContext;
        private readonly GlobalFilters globalFilters;
        private readonly IOptionsMonitor<Limits> limits;

        public SafeExchangeAdminSecrets(
            SafeExchangeDbContext dbContext,
            GlobalFilters globalFilters,
            IOptionsMonitor<Limits> limits)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        }

        /// <summary>GET v2/admin/secret-list — paginated list of secrets (server-side).</summary>
        public async Task<HttpResponseData> RunList(HttpRequestData request, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetAdminFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var q = request.Query["q"];
                var sortBy = request.Query["sortBy"] ?? "created";
                var sortDir = request.Query["sortDir"] ?? "desc";
                var accessedBeforeRaw = request.Query["accessedBefore"];

                var (page, pageSize) = PaginationHelper.Parse(request, this.limits.CurrentValue);

                // Parse optional accessedBefore filter.
                DateTime? accessedBefore = null;
                if (!string.IsNullOrWhiteSpace(accessedBeforeRaw) &&
                    DateTime.TryParse(accessedBeforeRaw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedDate))
                {
                    accessedBefore = parsedDate.ToUniversalTime();
                }

                // Build base query — only scalar predicates that Cosmos EF can translate.
                IQueryable<ObjectMetadata> baseQuery = this.dbContext.Objects;

                if (!string.IsNullOrWhiteSpace(q))
                {
                    var qLower = q.ToLowerInvariant();
                    baseQuery = baseQuery.Where(o => o.ObjectName.ToLower().Contains(qLower));
                }

                if (accessedBefore.HasValue)
                {
                    baseQuery = baseQuery.Where(o => o.LastAccessedAt < accessedBefore.Value);
                }

                // Apply sorting on scalar properties (supported by Cosmos EF).
                var isAsc = string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase);
                IQueryable<ObjectMetadata> sortedQuery = sortBy?.ToLowerInvariant() switch
                {
                    "name" => isAsc
                        ? baseQuery.OrderBy(o => o.ObjectName)
                        : baseQuery.OrderByDescending(o => o.ObjectName),
                    "lastaccessed" => isAsc
                        ? baseQuery.OrderBy(o => o.LastAccessedAt)
                        : baseQuery.OrderByDescending(o => o.LastAccessedAt),
                    _ => isAsc
                        ? baseQuery.OrderBy(o => o.CreatedAt)
                        : baseQuery.OrderByDescending(o => o.CreatedAt),
                };

                // Fully server-side: count then page — no full materialisation.
                var page0 = Math.Max(0, page);
                var total = await sortedQuery.CountAsync();
                var items = await sortedQuery
                    .Skip(page0 * pageSize)
                    .Take(pageSize)
                    .Select(o => new SecretAdminOverviewOutput
                    {
                        ObjectName = o.ObjectName,
                        CreatedAt = o.CreatedAt,
                        LastAccessedAt = o.LastAccessedAt,
                    })
                    .ToListAsync();

                var result = new PaginatedResult<SecretAdminOverviewOutput>
                {
                    Items = items,
                    Page = page0,
                    PageSize = pageSize,
                    Total = total,
                    HasMore = (page0 + 1) * pageSize < total,
                };

                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<PaginatedResult<SecretAdminOverviewOutput>> { Status = "ok", Result = result });
            }, nameof(RunList), log);
        }

        /// <summary>GET v2/admin/secret/{secretName} — full metadata detail.</summary>
        public async Task<HttpResponseData> RunDetail(HttpRequestData request, string secretName, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetAdminFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var obj = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName == secretName);
                if (obj is null)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretName}' not found." });
                }

                var dto = this.ToDetailDto(obj);
                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<SecretAdminDetailOutput> { Status = "ok", Result = dto });
            }, nameof(RunDetail), log);
        }

        /// <summary>GET v2/admin/secret/{secretName}/access — list of access entries.</summary>
        public async Task<HttpResponseData> RunAccess(HttpRequestData request, string secretName, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetAdminFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var objectExists = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName == secretName);
                if (objectExists is null)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretName}' not found." });
                }

                var permissions = await this.dbContext.Permissions
                    .Where(p => p.SecretName == secretName)
                    .ToListAsync();

                var items = permissions.Select(p => new SecretAccessItemOutput
                {
                    SubjectName = p.SubjectName,
                    SubjectType = p.SubjectType.ToDto().ToString(),
                    CanRead = p.CanRead,
                    CanWrite = p.CanWrite,
                    CanGrantAccess = p.CanGrantAccess,
                    CanRevokeAccess = p.CanRevokeAccess,
                }).ToList();

                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<System.Collections.Generic.List<SecretAccessItemOutput>> { Status = "ok", Result = items });
            }, nameof(RunAccess), log);
        }

        private SecretAdminDetailOutput ToDetailDto(ObjectMetadata obj)
        {
            return new SecretAdminDetailOutput
            {
                ObjectName = obj.ObjectName,
                CreatedBy = obj.CreatedBy,
                CreatedAt = obj.CreatedAt,
                LastAccessedAt = obj.LastAccessedAt,
                ModifiedAt = obj.ModifiedAt,
                ModifiedBy = obj.ModifiedBy,
                ExpiresAt = this.NullIfDefault(obj.ExpirationMetadata?.ScheduleExpiration == true ? obj.ExpirationMetadata.ExpireAt : DefaultDateTime),
                IdleDeleteAt = this.NullIfDefault(obj.ExpirationMetadata?.ExpireOnIdleTime == true ? obj.ExpireIfUnusedAt : DefaultDateTime),
                AttachmentCount = obj.Content?.Count(c => !c.IsMain) ?? 0,
                Tags = obj.Tags?.ToList() ?? new System.Collections.Generic.List<string>(),
                AuditEnabled = obj.AuditEnabled,
                AuditInstanceId = obj.AuditInstanceId ?? string.Empty,
                KeepInStorage = obj.KeepInStorage,
            };
        }

        private DateTime? NullIfDefault(DateTime value)
        {
            return value == DefaultDateTime ? null : value;
        }
    }
}
