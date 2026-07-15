/// <summary>
/// SafeApplicationSearch — HTTP trigger for POST /v2/application-search. Thin wrapper
/// that delegates to the SafeExchangeApplicationSearch handler, mirroring SafeUserSearch.
/// </summary>

namespace SafeExchange.Functions.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using System;

    public class SafeApplicationSearch
    {
        private const string Version = "{apiVersion}";

        private readonly SafeExchangeApplicationSearch safeExchangeApplicationSearchHandler;

        private readonly ILogger log;

        public SafeApplicationSearch(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, ILogger<SafeApplicationSearch> log)
        {
            this.safeExchangeApplicationSearchHandler = new SafeExchangeApplicationSearch(dbContext, tokenHelper, globalFilters);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-ApplicationSearch")]
        public async Task<HttpResponseData> RunApplicationSearch(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = $"{Version}/application-search")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.safeExchangeApplicationSearchHandler.RunSearch(request, principal, this.log);
        }
    }
}
