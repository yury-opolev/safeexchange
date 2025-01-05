/// <summary>
/// SafeGroupSearch
/// </summary>

namespace SafeExchange.Functions.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core;
    using System;
    using SafeExchange.Core.Graph;
    using Microsoft.Extensions.Configuration;

    public class SafeGroupSearch
    {
        private const string Version = "v2";

        private SafeExchangeGroupSearch safeExchangeGroupSearchHandler;

        private readonly ILogger log;

        public SafeGroupSearch(IConfiguration configuration, SafeExchangeDbContext dbContext, IGraphDataProvider graphDataProvider, ITokenHelper tokenHelper, GlobalFilters globalFilters, ILogger<SafeGroupSearch> log)
        {
            this.safeExchangeGroupSearchHandler = new SafeExchangeGroupSearch(configuration, dbContext, graphDataProvider, tokenHelper, globalFilters);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-GroupSearch")]
        public async Task<HttpResponseData> RunUserSearch(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = $"{Version}/group-search")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.safeExchangeGroupSearchHandler.RunSearch(request, principal, this.log);
        }
    }
}
