/// <summary>
/// SafeAdminApplications
/// </summary>

namespace SafeExchange.Functions.AdminFunctions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions.Admin;
    using SafeExchange.Core;

    public class SafeAdminApplications
    {
        private const string Version = "v2";

        private SafeExchangeApplications safeExchangeApplicationsHandler;

        private readonly ILogger log;

        public SafeAdminApplications(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, ILogger<SafeAdminApplications> log)
        {
            this.safeExchangeApplicationsHandler = new SafeExchangeApplications(dbContext, tokenHelper, globalFilters);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-Application")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "get", "patch", "delete", Route = $"{Version}/applications/{{applicationId}}")]
            HttpRequestData request,
            string applicationId)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.safeExchangeApplicationsHandler.Run(request, applicationId, principal, this.log);
        }
    }
}
