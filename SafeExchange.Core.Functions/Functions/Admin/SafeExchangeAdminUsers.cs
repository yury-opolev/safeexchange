/// <summary>
/// SafeExchangeAdminUsers — admin-only paginated user listing + enable/disable
/// toggle. Search is case-insensitive substring against AadUpn and DisplayName.
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

    public class SafeExchangeAdminUsers
    {
        private static readonly System.Text.RegularExpressions.Regex TelemetryIdPattern =
            new("^[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.Compiled);

        private readonly SafeExchangeDbContext dbContext;
        private readonly ITokenHelper tokenHelper;
        private readonly GlobalFilters globalFilters;
        private readonly IOptionsMonitor<Limits> limits;

        public SafeExchangeAdminUsers(
            SafeExchangeDbContext dbContext,
            ITokenHelper tokenHelper,
            GlobalFilters globalFilters,
            IOptionsMonitor<Limits> limits)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
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

                IQueryable<User> baseQuery = this.dbContext.Users;
                if (!string.IsNullOrWhiteSpace(q))
                {
                    var qLower = q.ToLowerInvariant();
                    baseQuery = baseQuery.Where(u =>
                        u.AadUpn.ToLower().Contains(qLower) ||
                        u.DisplayName.ToLower().Contains(qLower));
                }

                var total = await baseQuery.CountAsync();
                var page0 = Math.Max(0, page);
                var items = await baseQuery
                    .OrderBy(u => u.AadUpn)
                    .Skip(page0 * pageSize)
                    .Take(pageSize)
                    .Select(u => new UserOverviewOutput
                    {
                        AadUpn = u.AadUpn,
                        DisplayName = u.DisplayName,
                        ContactEmail = u.ContactEmail,
                        Enabled = u.Enabled,
                    })
                    .ToListAsync();

                var result = new PaginatedResult<UserOverviewOutput>
                {
                    Items = items, Page = page0, PageSize = pageSize, Total = total,
                    HasMore = (page0 + 1) * pageSize < total,
                };
                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<PaginatedResult<UserOverviewOutput>> { Status = "ok", Result = result });
            }, nameof(RunList), log);
        }

        public async Task<HttpResponseData> RunDetail(HttpRequestData request, string upn, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetAdminFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var user = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn == upn);
                if (user is null)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = $"User '{upn}' not found." });
                }

                var detail = await this.BuildUserDetailAsync(user);
                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<UserDetailOutput> { Status = "ok", Result = detail });
            }, nameof(RunDetail), log);
        }

        public async Task<HttpResponseData> RunByTelemetryId(HttpRequestData request, string telemetryId, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetAdminFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var id = (telemetryId ?? string.Empty).Trim().ToLowerInvariant();
                if (!TelemetryIdPattern.IsMatch(id))
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Telemetry id must be 32 hex characters." });
                }

                // Current id first (lives on the user); then the retention map (cross-partition).
                var user = await this.dbContext.Users.FirstOrDefaultAsync(u => u.TelemetryId == id);
                if (user is null)
                {
                    var entry = await this.dbContext.Set<TelemetryIdMapEntry>().FirstOrDefaultAsync(e => e.id == id);
                    if (entry is not null)
                    {
                        user = await this.dbContext.Users.FirstOrDefaultAsync(u => u.Id == entry.UserId);
                    }
                }

                if (user is null)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = "No user resolves to that telemetry id (it may be older than the retention window)." });
                }

                var detail = await this.BuildUserDetailAsync(user);
                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<UserDetailOutput> { Status = "ok", Result = detail });
            }, nameof(RunByTelemetryId), log);
        }

        private async Task<UserDetailOutput> BuildUserDetailAsync(User user)
        {
            var recent = await this.dbContext.Set<TelemetryIdMapEntry>()
                .Where(e => e.UserId == user.Id)
                .OrderByDescending(e => e.ValidToUtc)
                .Select(e => new TelemetryIdWindowOutput
                {
                    Id = e.id,
                    ValidFromUtc = e.ValidFromUtc,
                    ValidToUtc = e.ValidToUtc,
                })
                .ToListAsync();

            return new UserDetailOutput
            {
                AadUpn = user.AadUpn,
                DisplayName = user.DisplayName,
                ContactEmail = user.ContactEmail,
                Enabled = user.Enabled,
                Id = user.Id,
                AadObjectId = user.AadObjectId,
                AadTenantId = user.AadTenantId,
                CreatedAt = user.CreatedAt,
                ModifiedAt = user.ModifiedAt,
                ReceiveExternalNotifications = user.ReceiveExternalNotifications,
                ConsentRequired = user.ConsentRequired,
                CurrentTelemetryId = user.TelemetryId,
                TelemetryIdActiveSinceUtc = user.TelemetryIdIssuedAt,
                TelemetryIdRotatesAtUtc = user.TelemetryIdExpiresAt,
                RecentTelemetryIds = recent,
            };
        }

        public async Task<HttpResponseData> RunToggleEnabled(HttpRequestData request, string upn, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetAdminFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var user = await this.dbContext.Users.FirstOrDefaultAsync(u => u.AadUpn == upn);
                if (user is null)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = $"User '{upn}' not found." });
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

                user.Enabled = input.Enabled;
                user.ModifiedAt = DateTimeProvider.UtcNow;

                // The user document may be updated concurrently (e.g. the user's own request
                // rotating its telemetry id under the etag concurrency token). The admin intent
                // is authoritative, so on a conflict reload the latest document, re-apply the
                // enabled flag, and save again.
                const int maxAttempts = 4;
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        await this.dbContext.SaveChangesAsync();
                        break;
                    }
                    catch (DbUpdateConcurrencyException) when (attempt < maxAttempts)
                    {
                        await this.dbContext.Entry(user).ReloadAsync();
                        user.Enabled = input.Enabled;
                        user.ModifiedAt = DateTimeProvider.UtcNow;
                    }
                }

                log.LogInformation("Admin toggled User '{Upn}' Enabled to {Enabled}.", upn, input.Enabled);
                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<UserOverviewOutput>
                    {
                        Status = "ok",
                        Result = new UserOverviewOutput
                        {
                            AadUpn = user.AadUpn, DisplayName = user.DisplayName,
                            ContactEmail = user.ContactEmail, Enabled = user.Enabled,
                        },
                    });
            }, nameof(RunToggleEnabled), log);
        }
    }
}
