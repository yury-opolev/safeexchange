/// <summary>
/// SafeAccess
/// </summary>

namespace SafeExchange.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Groups;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using System.Threading.Tasks;

    public class SafeAccess
    {
        private const string Version = "v2";

        private SafeExchangeAccess accessHandler;

        private readonly ILogger log;

        public SafeAccess(SafeExchangeDbContext dbContext, IGroupsManager groupsManager, ITokenHelper tokenHelper, GlobalFilters globalFilters, IPurger purger, IPermissionsManager permissionsManager, ILogger<SafeAccess> log)
        {
            this.accessHandler = new SafeExchangeAccess(dbContext, groupsManager, tokenHelper, globalFilters, purger, permissionsManager);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-Access")]
        public async Task<HttpResponseData> RunSecret(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "get", "delete", Route = $"{Version}/access/{{secretId}}")]
            HttpRequestData request,
            string secretId)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.accessHandler.Run(request, secretId, principal, this.log);
        }
    }
}
