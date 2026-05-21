/// <summary>
/// SafePinnedSecrets
/// </summary>

namespace SafeExchange.Functions.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Permissions;
    using System;
    using System.Threading.Tasks;

    public class SafePinnedSecrets
    {
        private const string Version = "v2";

        private SafeExchangePinnedSecrets safeExchangePinnedSecretsHandler;

        private SafeExchangePinnedSecretsList safeExchangePinnedSecretsListHandler;

        private readonly ILogger log;

        public SafePinnedSecrets(
            SafeExchangeDbContext dbContext,
            ITokenHelper tokenHelper,
            GlobalFilters globalFilters,
            IPermissionsManager permissionsManager,
            IOptions<PinnedSecretsConfiguration> config,
            ILogger<SafePinnedSecrets> log)
        {
            this.safeExchangePinnedSecretsHandler = new SafeExchangePinnedSecrets(
                dbContext, tokenHelper, globalFilters, permissionsManager, config);
            this.safeExchangePinnedSecretsListHandler = new SafeExchangePinnedSecretsList(
                dbContext, tokenHelper, globalFilters);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-PinnedSecrets")]
        public async Task<HttpResponseData> RunPinnedSecrets(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "put", "delete", Route = $"{Version}/pinnedsecrets/{{secretId}}")]
            HttpRequestData request,
            string secretId)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.safeExchangePinnedSecretsHandler.Run(request, secretId, principal, this.log);
        }

        [Function("SafeExchange-PinnedSecretsList")]
        public async Task<HttpResponseData> RunListPinnedSecrets(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/pinnedsecrets-list")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.safeExchangePinnedSecretsListHandler.RunList(request, principal, this.log);
        }
    }
}
