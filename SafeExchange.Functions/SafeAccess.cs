/// <summary>
/// SafeAccess
/// </summary>

namespace SpaceOyster.SafeExchange.Functions
{
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Azure.WebJobs.Extensions.Http;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;
    using System.Security.Claims;
    using Microsoft.Azure.Cosmos.Table;
    using SpaceOyster.SafeExchange.Core;

    public class SafeAccess
    {
        private SafeExchangeAccess accessHandler;

        public SafeAccess(IGraphClientProvider graphClientProvider) 
        {
            this.accessHandler = new SafeExchangeAccess(graphClientProvider);
        }

        [FunctionName("SafeExchange-Access")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "get", "delete", Route = "access/{secretId}")]
            HttpRequest req,
            [Table("SubjectPermissions")]
            CloudTable subjectPermissionsTable,
            [Table("GroupDictionary")]
            CloudTable groupDictionaryTable,
            string secretId, ClaimsPrincipal principal, ILogger log)
        {
            return await this.accessHandler.Run(req, subjectPermissionsTable, groupDictionaryTable, secretId, principal, log);
        }
    }
}
