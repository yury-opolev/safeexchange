/// <summary>
/// SafeExchangeApplicationSearch — POST /v2/application-search. Lets an authenticated
/// USER search the locally-registered S2S applications by display name so they can add
/// one to a secret's access list. Mirrors the user/group search endpoints (same auth
/// gate, same SearchInput contract, applications forbidden as callers) but reads the
/// local Applications collection instead of Microsoft Graph.
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Telemetry;
    using SafeExchange.Core.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;

    public class SafeExchangeApplicationSearch
    {
        /// <summary>Upper bound on returned hits — the picker is for narrowing by name, not bulk listing.</summary>
        public const int MaxResults = 50;

        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        public SafeExchangeApplicationSearch(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters)
        {
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public async Task<HttpResponseData> RunSearch(
            HttpRequestData request, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            (SubjectType subjectType, string subjectId) = await SubjectHelper.GetSubjectInfoAsync(this.tokenHelper, principal, this.dbContext);
            if (SubjectType.Application.Equals(subjectType))
            {
                return await ActionResults.ForbiddenAsync(request, "Applications cannot use this API.");
            }

            log.LogInformation($"{nameof(SafeExchangeApplicationSearch)} triggered by {subjectType} (tid {TelemetryContext.Current}), [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "post":
                    return await this.HandleSearchApplication(request, log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = $"Request method '{request.Method}' not allowed." });
            }
        }

        private async Task<HttpResponseData> HandleSearchApplication(HttpRequestData request, ILogger log)
            => await ActionResults.TryCatchAsync(request, async () =>
            {
                var searchInput = await this.TryGetSearchInputAsync(request, log);
                if (searchInput == null)
                {
                    log.LogInformation($"Search data is not provided.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Search data is not provided." });
                }

                if (!SearchStringValidator.TryValidate(searchInput.SearchString, out var validationError))
                {
                    log.LogWarning("Rejected application-search input: {Reason}", validationError);
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = validationError });
                }

                var term = searchInput.SearchString.Trim().ToLower();

                // Filter server-side (enabled + name match), then order/cap in memory to
                // avoid Cosmos composite-index requirements for OrderBy. The name match
                // mirrors the admin application search.
                var matches = await this.dbContext.Applications
                    .Where(a => a.Enabled && a.DisplayName.ToLower().Contains(term))
                    .ToListAsync();

                var results = matches
                    .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxResults)
                    .Select(a => new ApplicationSearchOutput
                    {
                        DisplayName = a.DisplayName,
                        AadClientId = a.AadClientId,
                        AadTenantId = a.AadTenantId,
                    })
                    .ToList();

                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK,
                    new BaseResponseObject<List<ApplicationSearchOutput>> { Status = "ok", Result = results });
            }, nameof(HandleSearchApplication), log);

        private async Task<SearchInput?> TryGetSearchInputAsync(HttpRequestData request, ILogger log)
        {
            var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            try
            {
                return DefaultJsonSerializer.Deserialize<SearchInput>(requestBody);
            }
            catch (Exception exception)
            {
                log.LogWarning(exception, "Could not parse input data for search input.");
                return null;
            }
        }
    }
}
