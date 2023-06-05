/// <summary>
/// SafeAdminOperations
/// </summary>

namespace SafeExchange.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core;
    using SafeExchange.Core.Crypto;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions.Admin;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class SafeAdminOperations
    {
        private const string Version = "v2";

        private SafeExchangeAdminOperations adminOperationsHandler;

        private readonly ILogger log;

        public SafeAdminOperations(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, ICryptoHelper cryptoHelper, GlobalFilters globalFilters, IPurger purger, IPermissionsManager permissionsManager, ILogger<SafeAdminOperations> log)
        {
            this.adminOperationsHandler = new SafeExchangeAdminOperations(dbContext, tokenHelper, cryptoHelper, globalFilters);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-AdminOperations")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = $"{Version}/admops/{{operationName}}")]
            HttpRequestData request,
            string operationName)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.adminOperationsHandler.Run(request, operationName, principal, this.log);
        }
    }
}
