/// <summary>
/// SafePinnedGroups
/// </summary>

namespace SafeExchange.Functions.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core;
    using SafeExchange.Core.Functions;

    public class SafePinnedGroups
    {
        private const string Version = "v2";

        private SafeExchangePinnedGroups safeExchangePinnedGroupsHandler;

        private SafeExchangePinnedGroupsList safeExchangePinnedGroupsListHandler;

        private readonly ILogger log;

        public SafePinnedGroups(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, ILogger<SafePinnedGroups> log)
        {
            this.safeExchangePinnedGroupsHandler = new SafeExchangePinnedGroups(dbContext, tokenHelper, globalFilters);
            this.safeExchangePinnedGroupsListHandler = new SafeExchangePinnedGroupsList(dbContext, tokenHelper, globalFilters);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-PinnedGroups")]
        public async Task<HttpResponseData> RunGroups(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "put", "delete", Route = $"{Version}/pinnedgroups/{{pinnedGroupId}}")]
            HttpRequestData request,
            string pinnedGroupId)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.safeExchangePinnedGroupsHandler.Run(request, pinnedGroupId, principal, this.log);
        }

        [Function("SafeExchange-PinnedGroupsList")]
        public async Task<HttpResponseData> RunListGroups(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/pinnedgroups-list")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.safeExchangePinnedGroupsListHandler.RunList(request, principal, this.log);
        }
    }
}
