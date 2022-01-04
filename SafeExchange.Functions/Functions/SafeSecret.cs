/// <summary>
/// SafeSecret
/// </summary>

namespace SafeExchange.Functions
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Azure.WebJobs.Extensions.Http;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Filters;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class SafeSecret
    {
        private const string Version = "v2"; 

        private SafeExchangeSecretMeta metaHandler;

        private SafeExchangeSecretContentMeta contentMetaHandler;

        private SafeExchangeSecretStream contentHandler;

        public SafeSecret(IConfiguration configuration, SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, IPurger purger, IPermissionsManager permissionsManager, IBlobHelper blobHelper)
        {
            this.metaHandler = new SafeExchangeSecretMeta(configuration, dbContext, tokenHelper, globalFilters, purger, permissionsManager);
            this.contentMetaHandler = new SafeExchangeSecretContentMeta(configuration, dbContext, tokenHelper, globalFilters, purger, permissionsManager);
            this.contentHandler = new SafeExchangeSecretStream(configuration, dbContext, tokenHelper, globalFilters, purger, blobHelper, permissionsManager);
        }

        [FunctionName("SafeExchange-SecretMeta")]
        public async Task<IActionResult> RunSecret(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "get", "patch", "delete", Route = $"{Version}/secret/{{secretId}}")]
            HttpRequest req,
            string secretId, ClaimsPrincipal principal, ILogger log)
        {
            return await this.metaHandler.Run(req, secretId, principal, log);
        }

        [FunctionName("SafeExchange-ListSecretMeta")]
        public async Task<IActionResult> RunListSecret(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/secret-list")]
            HttpRequest req, ClaimsPrincipal principal, ILogger log)
        {
            return await this.metaHandler.RunList(req, principal, log);
        }

        [FunctionName("SafeExchange-SecretContentMetaCreate")]
        public async Task<IActionResult> RunCreateContent(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = $"{Version}/secret/{{secretId}}/content")]
            HttpRequest req,
            string secretId, ClaimsPrincipal principal, ILogger log)
        {
            return await this.contentMetaHandler.Run(req, secretId, string.Empty, principal, log);
        }

        [FunctionName("SafeExchange-SecretContentMeta")]
        public async Task<IActionResult> RunContent(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", "delete", Route = $"{Version}/secret/{{secretId}}/content/{{contentId}}")]
            HttpRequest req,
            string secretId, string contentId, ClaimsPrincipal principal, ILogger log)
        {
            return await this.contentMetaHandler.Run(req, secretId, contentId, principal, log);
        }

        [FunctionName("SafeExchange-SecretContentMetaDrop")]
        public async Task<IActionResult> RunDrop(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = $"{Version}/secret/{{secretId}}/content/{{contentId}}/drop")]
            HttpRequest req,
            string secretId, string contentId, ClaimsPrincipal principal, ILogger log)
        {
            return await this.contentMetaHandler.RunDrop(req, secretId, contentId, principal, log);
        }

        [FunctionName("SafeExchange-SecretStreamUpload")]
        public async Task<IActionResult> RunUploadStream(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = $"{Version}/secret/{{secretId}}/content/{{contentId}}/chunk")]
            HttpRequest req,
            string secretId, string contentId, ClaimsPrincipal principal, ILogger log)
        {
            return await this.contentHandler.Run(req, secretId, contentId, string.Empty, principal, log);
        }

        [FunctionName("SafeExchange-SecretStreamDownload")]
        public async Task<IActionResult> RunDownloadStream(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/secret/{{secretId}}/content/{{contentId}}/chunk/{{chunkId}}")]
            HttpRequest req,
            string secretId, string contentId, string chunkId, ClaimsPrincipal principal, ILogger log)
        {
            return await this.contentHandler.Run(req, secretId, contentId, chunkId, principal, log);
        }

        [FunctionName("SafeExchange-SecretStreamContentDownload")]
        public async Task<IActionResult> RunContentDownloadStream(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/secret/{{secretId}}/content/{{contentId}}/all")]
            HttpRequest req,
            string secretId, string contentId, ClaimsPrincipal principal, ILogger log)
        {
            return await this.contentHandler.RunContentDownload(req, secretId, contentId, principal, log);
        }
    }
}
