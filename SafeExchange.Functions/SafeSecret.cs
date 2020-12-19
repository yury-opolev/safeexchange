/// <summary>
/// SafeSecret
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

    public class SafeSecret
    {
        private SafeExchangeSecret secretHandler;

        public SafeSecret(IGraphClientProvider graphClientProvider)
        {
            this.secretHandler = new SafeExchangeSecret(graphClientProvider);
        }

        [FunctionName("SafeExchange-Secret")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "get", "patch", "delete", Route = "secrets/{secretId}")]
            HttpRequest req,
            [Table("SubjectPermissions")]
            CloudTable subjectPermissionsTable,
            [Table("ObjectMetadata")]
            CloudTable objectMetadataTable,
            [Table("GroupDictionary")]
            CloudTable groupDictionaryTable,
            string secretId, ClaimsPrincipal principal, ILogger log)
        {
            return await this.secretHandler.Run(req, subjectPermissionsTable, objectMetadataTable, groupDictionaryTable, secretId, principal, log);
        }
    }
}
