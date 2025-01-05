/// <summary>
/// SafeAdminApplications
/// </summary>

namespace SafeExchange.Functions.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core;
    using SafeExchange.Core.Functions;

    public class SafeApplicationsList
    {
        private const string Version = "v2";

        private SafeExchangeApplicationsList safeExchangeApplicationsListHandler;

        private readonly ILogger log;

        public SafeApplicationsList(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, ILogger<SafeApplicationsList> log)
        {
            this.safeExchangeApplicationsListHandler = new SafeExchangeApplicationsList(dbContext, tokenHelper, globalFilters);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-ApplicationList")]
        public async Task<HttpResponseData> RunListApplications(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/applications-list")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.safeExchangeApplicationsListHandler.RunList(request, principal, this.log);
        }
    }
}
