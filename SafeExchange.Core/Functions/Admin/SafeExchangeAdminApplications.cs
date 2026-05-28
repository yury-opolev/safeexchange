/// <summary>
/// SafeExchangeAdminApplications — admin-gated surface for Application
/// (a.k.a. S2S app) entities. Five reads/writes on the same entity:
///
/// * RunList   — GET    /v2/admin/applications (paged, q matches name or clientId)
/// * RunDetail — GET    /v2/admin/applications/{name} (incl. owners)
/// * RunToggleEnabled — PATCH /v2/admin/applications/{name}/enabled
/// * RunDelete — DELETE /v2/admin/applications/{name} (cascades owner rows)
/// * RunReplaceOwners — PUT /v2/admin/applications/{name}/owners
///                      (registrar guard: cannot remove the primary owner)
///
/// Registrar protection is identical to the self-service path — admin cannot
/// strip the registrar either (per design decision). Delete and Toggle-Enabled
/// remain available because they retire the whole app rather than mutating
/// its owner set.
/// </summary>

namespace SafeExchange.Core.Functions.Admin
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core.Applications;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class SafeExchangeAdminApplications
    {
        private readonly SafeExchangeDbContext dbContext;
        private readonly GlobalFilters globalFilters;
        private readonly IOptionsMonitor<Limits> limits;
        private readonly IApplicationOwnerService ownerService;

        public SafeExchangeAdminApplications(
            SafeExchangeDbContext dbContext,
            GlobalFilters globalFilters,
            IOptionsMonitor<Limits> limits,
            IApplicationOwnerService ownerService)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
            this.ownerService = ownerService ?? throw new ArgumentNullException(nameof(ownerService));
        }

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

                // Compute owner counts as a second pass — Cosmos EF can't translate
                // a server-side GroupBy, so we project the ApplicationId column and
                // group client-side. Bounded by pageSize × per-app owner count.
                var appIds = apps.Select(a => a.Id).ToList();
                var ownerAppIds = await this.dbContext.ApplicationOwners
                    .Where(o => appIds.Contains(o.ApplicationId))
                    .Select(o => o.ApplicationId)
                    .ToListAsync();
                var countByApp = ownerAppIds
                    .GroupBy(id => id)
                    .ToDictionary(g => g.Key, g => g.Count());

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

        public async Task<HttpResponseData> RunDetail(HttpRequestData request, string displayName, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetAdminFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var app = await this.dbContext.Applications.FirstOrDefaultAsync(a => a.DisplayName == displayName);
                if (app is null)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = $"Application '{displayName}' not found." });
                }

                var owners = await this.ownerService.ListOwnersAsync(app.Id);
                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<S2SAppOutput> { Status = "ok", Result = SafeExchangeS2SApps.ToDto(app, owners) });
            }, nameof(RunDetail), log);
        }

        public async Task<HttpResponseData> RunToggleEnabled(HttpRequestData request, string displayName, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetAdminFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

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
                try
                {
                    input = DefaultJsonSerializer.Deserialize<EnabledToggleInput>(body);
                }
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

        /// <summary>DELETE /admin/applications/{displayName} — admin-only; cascades owner rows.</summary>
        public async Task<HttpResponseData> RunDelete(HttpRequestData request, string displayName, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetAdminFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var app = await this.dbContext.Applications.FirstOrDefaultAsync(a => a.DisplayName == displayName);
                if (app is null)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                        new BaseResponseObject<string> { Status = "no_content", Result = "already absent" });
                }

                var owners = await this.dbContext.ApplicationOwners.Where(o => o.ApplicationId == app.Id).ToListAsync();
                this.dbContext.ApplicationOwners.RemoveRange(owners);
                this.dbContext.Applications.Remove(app);
                await this.dbContext.SaveChangesAsync();

                log.LogInformation("Admin deleted Application '{App}' ({OwnerCount} owners cascaded).",
                    displayName, owners.Count);
                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<string> { Status = "ok", Result = "deleted" });
            }, nameof(RunDelete), log);
        }

        /// <summary>PUT /admin/applications/{displayName}/owners — atomic owner-set reconcile.</summary>
        public async Task<HttpResponseData> RunReplaceOwners(HttpRequestData request, string displayName, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetAdminFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var app = await this.dbContext.Applications.FirstOrDefaultAsync(a => a.DisplayName == displayName);
                if (app is null)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = $"Application '{displayName}' not found." });
                }

                var body = await new StreamReader(request.Body).ReadToEndAsync();
                S2SAppReplaceOwnersInput? input;
                try
                {
                    input = DefaultJsonSerializer.Deserialize<S2SAppReplaceOwnersInput>(body);
                }
                catch
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Body is not valid JSON." });
                }

                if (input is null || input.Owners is null)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Owner list is required." });
                }

                var desired = input.Owners
                    .Where(o => o is not null && !string.IsNullOrWhiteSpace(o.SubjectId))
                    .Select(o => new ApplicationOwner(app.Id, o.SubjectType, o.SubjectId, addedBy: "admin", subjectName: o.SubjectName ?? string.Empty))
                    .ToList();

                if (!SafeExchangeS2SApps.HasRegistrarOwner(app, desired))
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.Conflict,
                        new BaseResponseObject<object> { Status = "conflict", Error = SafeExchangeS2SApps.RegistrarProtectionMessage });
                }

                try
                {
                    await this.ownerService.ReplaceOwnersAsync(app.Id, desired);
                }
                catch (ApplicationOwnerInvariantException ex)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.Conflict,
                        new BaseResponseObject<object> { Status = "conflict", Error = ex.Message });
                }

                log.LogInformation("Admin replaced owner set on app '{App}' (final count {Count}).",
                    app.DisplayName, desired.Count);

                var owners = await this.ownerService.ListOwnersAsync(app.Id);
                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<S2SAppOutput> { Status = "ok", Result = SafeExchangeS2SApps.ToDto(app, owners) });
            }, nameof(RunReplaceOwners), log);
        }
    }
}
