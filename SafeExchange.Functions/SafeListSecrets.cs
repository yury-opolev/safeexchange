﻿/// <summary>
/// SafeListSecrets
/// </summary>

namespace SpaceOyster.SafeExchange.Functions
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Azure.Cosmos.Table;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Azure.WebJobs.Extensions.Http;
    using Microsoft.Extensions.Logging;
    using SpaceOyster.SafeExchange.Core;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class SafeListSecrets
    {
        private SafeExchangeListSecrets listSecretsHandler;

        public SafeListSecrets(IGraphClientProvider graphClientProvider)
        {
            this.listSecretsHandler = new SafeExchangeListSecrets();
        }

        [FunctionName("SafeExchange-ListSecrets")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "secrets")]
            HttpRequest req,
            [Table("SubjectPermissions")]
            CloudTable subjectPermissionsTable,
            ClaimsPrincipal principal, ILogger log)
        {
            return await this.listSecretsHandler.Run(req, subjectPermissionsTable, principal, log);
        }
    }
}
