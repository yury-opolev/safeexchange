
namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using System;
    using System.Net;
    using System.Security.Claims;

    public class SafeExchangePinnedGroupsList
    {
        private const int DefaultMaxDegreeOfParallelism = 3;

        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        private readonly int maxDegreeOfParallelism;

        public SafeExchangePinnedGroupsList(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters)
            : this(dbContext, tokenHelper, globalFilters, DefaultMaxDegreeOfParallelism)
        {
        }

        public SafeExchangePinnedGroupsList(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, int maxDegreeOfParallelism)
        {
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.maxDegreeOfParallelism = maxDegreeOfParallelism;
        }

        public async Task<HttpResponseData> RunList(
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
                await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Forbidden,
                    new BaseResponseObject<object> { Status = "forbidden", Error = "Applications cannot use this API." });
            }

            log.LogInformation($"{nameof(SafeExchangePinnedGroupsList)} triggered by {subjectType} {subjectId}, [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "get":
                    return await this.HandleListPinnedGroups(request, subjectType, subjectId, log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = $"Request method '{request.Method}' not allowed." });
            }
        }

        private async Task<HttpResponseData> HandleListPinnedGroups(HttpRequestData request, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () =>
            {
                var existingPinnedGroups = await this.dbContext.PinnedGroups.Where(pg => pg.UserId.Equals(subjectId)).ToListAsync();
                if (existingPinnedGroups.Count == 0)
                {
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.NoContent,
                        new BaseResponseObject<List<PinnedGroupOutput>>
                        {
                            Status = "no_content",
                            Result = Array.Empty<PinnedGroupOutput>().ToList()
                        });
                }

                var result = new List<PinnedGroupOutput>(existingPinnedGroups.Count);
                await Parallel.ForEachAsync(
                    existingPinnedGroups,
                    new ParallelOptions() { MaxDegreeOfParallelism = this.maxDegreeOfParallelism },
                    async (pg, ct) =>
                    {
                        var groupItem = await this.dbContext.GroupDictionary.FindAsync([pg.GroupItemId], cancellationToken: ct);
                        result.Add(groupItem.ToPinnedGroupDto());
                    });

                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK,
                    new BaseResponseObject<List<PinnedGroupOutput>>
                    {
                        Status = "ok",
                        Result = result
                    });
            }, nameof(HandleListPinnedGroups), log);

        private static async Task<HttpResponseData> TryCatch(HttpRequestData request, Func<Task<HttpResponseData>> action, string actionName, ILogger log)
        {
            try
            {
                return await action();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, $"Exception in {actionName}: {ex.GetType()}: {ex.Message}");

                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.InternalServerError,
                    new BaseResponseObject<object> { Status = "error", SubStatus = "internal_exception", Error = $"{ex.GetType()}: {ex.Message ?? "Unknown exception."}" });
            }
        }
    }
}
