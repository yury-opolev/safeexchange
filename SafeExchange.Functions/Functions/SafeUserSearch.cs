/// <summary>
/// SafeUserSearch
/// </summary>

namespace SafeExchange.Functions.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core;
    using SafeExchange.Functions.AdminFunctions;
    using System;
    using SafeExchange.Core.Graph;

    public class SafeUserSearch
    {
        private const string Version = "v2";

        private SafeExchangeUserSearch safeExchangeUserSearchHandler;

        private readonly ILogger log;

        public SafeUserSearch(SafeExchangeDbContext dbContext, IGraphDataProvider graphDataProvider, ITokenHelper tokenHelper, GlobalFilters globalFilters, ILogger<SafeAdminApplications> log)
        {
            this.safeExchangeUserSearchHandler = new SafeExchangeUserSearch(dbContext, graphDataProvider, tokenHelper, globalFilters);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-UserSearch")]
        public async Task<HttpResponseData> RunUserSearch(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = $"{Version}/user-search")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.safeExchangeUserSearchHandler.RunSearch(request, principal, this.log);
        }
    }
}
