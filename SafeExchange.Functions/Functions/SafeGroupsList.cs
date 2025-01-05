/// <summary>
/// SafeGroupsList
/// </summary>

namespace SafeExchange.Functions.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core;
    using SafeExchange.Core.Functions;

    public class SafeGroupsList
    {
        private const string Version = "v2";

        private SafeExchangeGroupsList safeExchangeGroupsListHandler;

        private readonly ILogger log;

        public SafeGroupsList(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, ILogger<SafeGroupsList> log)
        {
            this.safeExchangeGroupsListHandler = new SafeExchangeGroupsList(dbContext, tokenHelper, globalFilters);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-GroupsList")]
        public async Task<HttpResponseData> RunListGroups(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/groups-list")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.safeExchangeGroupsListHandler.RunList(request, principal, this.log);
        }
    }
}
