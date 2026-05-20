/// <summary>
/// SafeAccessGiveUp
/// </summary>

namespace SafeExchange.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Permissions;
    using System;
    using System.Threading.Tasks;

    public class SafeAccessGiveUp
    {
        private const string Version = "v2";

        private readonly SafeExchangeAccessGiveUp giveUpHandler;

        private readonly ILogger log;

        public SafeAccessGiveUp(
            SafeExchangeDbContext dbContext,
            ITokenHelper tokenHelper,
            GlobalFilters globalFilters,
            IPermissionsManager permissionsManager,
            IOrphanedSecretManager orphanedSecretManager,
            IAuditWriter auditWriter,
            IOptionsMonitor<Features> features,
            ILogger<SafeAccessGiveUp> log)
        {
            this.giveUpHandler = new SafeExchangeAccessGiveUp(
                dbContext, tokenHelper, globalFilters,
                permissionsManager, orphanedSecretManager, auditWriter, features);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-AccessGiveUp")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "delete", Route = $"{Version}/access-giveup/{{secretId}}")]
            HttpRequestData request,
            string secretId)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.giveUpHandler.Run(request, secretId, principal, this.log);
        }
    }
}
