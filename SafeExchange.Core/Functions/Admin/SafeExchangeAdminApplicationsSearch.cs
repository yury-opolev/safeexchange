/// <summary>
/// SafeExchangeAdminApplicationsSearch — admin paginated app search + focused
/// enable/disable toggle. Filter `q` matches DisplayName OR AadClientId via
/// case-insensitive substring (lets the admin paste either a partial name or
/// a GUID and get a result either way).
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
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class SafeExchangeAdminApplicationsSearch
    {
        private readonly SafeExchangeDbContext dbContext;
        private readonly GlobalFilters globalFilters;
        private readonly IOptionsMonitor<Limits> limits;

        public SafeExchangeAdminApplicationsSearch(
            SafeExchangeDbContext dbContext,
            GlobalFilters globalFilters,
            IOptionsMonitor<Limits> limits)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        }

        public async Task<HttpResponseData> RunList(HttpRequestData request, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetAdminFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn) return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var q = request.Query["q"];
                var (page, pageSize) = PaginationHelper.Parse(request, this.limits.CurrentValue);

                IQueryable<Application> baseQuery = this.dbContext.Applications;
                if (!string.IsNullOrWhiteSpace(q))
                {
                    var qLower = q.ToLowerInvariant();
                    baseQuery = baseQuery.Where(a =>
                        a.DisplayName.ToLower().Contains(qLower) ||
                        a.AadClientId.ToLower().Contains(qLower));
                }

                var total = await baseQuery.CountAsync();
                var page0 = Math.Max(0, page);
                var apps = await baseQuery
                    .OrderBy(a => a.DisplayName)
                    .Skip(page0 * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                // Compute owner counts as a second pass — Cosmos EF can't do joins
                // efficiently. Bounded by pageSize so it's small.
                var appIds = apps.Select(a => a.Id).ToList();
                var ownerCounts = await this.dbContext.ApplicationOwners
                    .Where(o => appIds.Contains(o.ApplicationId))
                    .GroupBy(o => o.ApplicationId)
                    .Select(g => new { ApplicationId = g.Key, Count = g.Count() })
                    .ToListAsync();
                var countByApp = ownerCounts.ToDictionary(x => x.ApplicationId, x => x.Count);

                var items = apps.Select(a => new ApplicationAdminOverviewOutput
                {
                    DisplayName = a.DisplayName,
                    AadClientId = a.AadClientId,
                    AadTenantId = a.AadTenantId,
                    ContactEmail = a.ContactEmail,
                    Enabled = a.Enabled,
                    OwnerCount = countByApp.TryGetValue(a.Id, out var c) ? c : 0,
                    OwnersAttentionRequired = (countByApp.TryGetValue(a.Id, out var c2) ? c2 : 0) < 2,
                }).ToList();

                var result = new PaginatedResult<ApplicationAdminOverviewOutput>
                {
                    Items = items, Page = page0, PageSize = pageSize, Total = total,
                    HasMore = (page0 + 1) * pageSize < total,
                };
                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<PaginatedResult<ApplicationAdminOverviewOutput>> { Status = "ok", Result = result });
            }, nameof(RunList), log);
        }

        public async Task<HttpResponseData> RunToggleEnabled(HttpRequestData request, string displayName, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetAdminFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn) return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var app = await this.dbContext.Applications.FirstOrDefaultAsync(a => a.DisplayName == displayName);
                if (app is null)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = $"Application '{displayName}' not found." });
                }

                var body = await new StreamReader(request.Body).ReadToEndAsync();
                EnabledToggleInput? input;
                try { input = DefaultJsonSerializer.Deserialize<EnabledToggleInput>(body); }
                catch
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Body must be { \"enabled\": bool }." });
                }
                if (input is null)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Body is required." });
                }

                app.Enabled = input.Enabled;
                app.ModifiedAt = DateTimeProvider.UtcNow;
                await this.dbContext.SaveChangesAsync();

                log.LogInformation("Admin toggled Application '{App}' Enabled to {Enabled}.", displayName, input.Enabled);
                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<string> { Status = "ok", Result = input.Enabled ? "enabled" : "disabled" });
            }, nameof(RunToggleEnabled), log);
        }
    }
}
