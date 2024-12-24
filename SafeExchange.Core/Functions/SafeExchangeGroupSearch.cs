
namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Graph;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using System;
    using System.Net;
    using System.Security.Claims;

    public class SafeExchangeGroupSearch
    {
        private const int DefaultMaxDegreeOfParallelism = 5;

        private readonly Features features;

        private readonly SafeExchangeDbContext dbContext;

        private readonly IGraphDataProvider graphDataProvider;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        private readonly int maxDegreeOfParallelism;

        public SafeExchangeGroupSearch(IConfiguration configuration, SafeExchangeDbContext dbContext, IGraphDataProvider graphDataProvider, ITokenHelper tokenHelper, GlobalFilters globalFilters)
            : this(configuration, dbContext, graphDataProvider, tokenHelper, globalFilters, DefaultMaxDegreeOfParallelism)
        {
        }

        public SafeExchangeGroupSearch(IConfiguration configuration, SafeExchangeDbContext dbContext, IGraphDataProvider graphDataProvider, ITokenHelper tokenHelper, GlobalFilters globalFilters, int maxDegreeOfParallelism)
        {
            this.features = new Features();
            configuration.GetSection("Features").Bind(this.features);

            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.graphDataProvider = graphDataProvider ?? throw new ArgumentNullException(nameof(graphDataProvider));
            this.maxDegreeOfParallelism = maxDegreeOfParallelism;
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
                await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Forbidden,
                    new BaseResponseObject<object> { Status = "forbidden", Error = "Applications cannot use this API." });
            }

            log.LogInformation($"{nameof(SafeExchangeGroupSearch)} triggered by {subjectType} {subjectId}, [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "post":
                    return await this.HandleSearchGroup(request, principal, log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = $"Request method '{request.Method}' not allowed." });
            }
        }

        private async Task<HttpResponseData> HandleSearchGroup(HttpRequestData request, ClaimsPrincipal principal, ILogger log)
            => await TryCatch(request, async () =>
            {
                if (!this.features.UseGraphGroupSearch)
                {
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.NoContent,
                        new BaseResponseObject<List<GraphGroupOutput>>
                        {
                            Status = "no_content",
                            Result = []
                        });
                }

                var searchInput = await this.TryGetSearchInputAsync(request, log);
                if (searchInput == null)
                {
                    log.LogInformation($"Search data for is not provided.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Search data is not provided." });
                }

                var accountIdAndToken = this.tokenHelper.GetAccountIdAndToken(request, principal);
                var foundGroups = await this.graphDataProvider.TryFindGroupsAsync(accountIdAndToken, searchInput.SearchString);
                if (!foundGroups.Success)
                {
                    var errorMessage = foundGroups.ConsentRequired ? "Group search was unsuccessful, consent required." : "Group search was unsuccessful.";
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.Forbidden,
                        ActionResults.InsufficientPermissions(errorMessage, foundGroups.ConsentRequired ? GraphDataProvider.ConsentRequiredSubStatus : string.Empty));
                }

                var result = new List<GraphGroupOutput>(foundGroups.Groups.Count);
                await Parallel.ForEachAsync(
                    foundGroups.Groups,
                    new ParallelOptions() { MaxDegreeOfParallelism = this.maxDegreeOfParallelism },
                    async (g, ct) =>
                {
                    var foundGroupItem = await this.dbContext.GroupDictionary.FindAsync([g.Id], cancellationToken: ct);
                    var isPersisted = foundGroupItem != default;
                    result.Add(g.ToDto(isPersisted));
                });

                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK,
                    new BaseResponseObject<List<GraphGroupOutput>>
                    {
                        Status = "ok",
                        Result = result
                    });
            }, nameof(HandleSearchGroup), log);

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
