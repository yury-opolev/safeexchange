/// <summary>
/// SafeAccessRequest
/// </summary>

namespace SafeExchange.Functions.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core;
    using SafeExchange.Core.DelayedTasks;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using System.Threading.Tasks;

    public class SafeAccessRequest
    {
        private const string Version = "v2";

        private SafeExchangeAccessRequest accessRequestHandler;

        private readonly ILogger log;

        public SafeAccessRequest(IConfiguration configuration, SafeExchangeDbContext dbContext, GlobalFilters globalFilters, ITokenHelper tokenHelper, IPurger purger, IPermissionsManager permissionsMaanger, IDelayedTaskScheduler delayedTaskScheduler, ILogger<SafeAccessRequest> log)
        {
            this.accessRequestHandler = new SafeExchangeAccessRequest(configuration, dbContext, globalFilters, tokenHelper, purger, permissionsMaanger, delayedTaskScheduler);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-AccessRequest")]
        public async Task<HttpResponseData> RunSecret(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "patch", "delete", Route = $"{Version}/accessrequest/{{secretId}}")]
            HttpRequestData request,
            string secretId)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.accessRequestHandler.Run(request, secretId, principal, this.log);
        }

        [Function("SafeExchange-ListAccessRequests")]
        public async Task<HttpResponseData> RunListSecret(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/accessrequest-list")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.accessRequestHandler.RunList(request, principal, this.log);
        }
    }
}
