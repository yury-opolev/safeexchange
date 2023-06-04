/// <summary>
/// SafeSecret
/// </summary>

namespace SafeExchange.Functions
{
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Filters;
    using System.Security.Claims;
    using System.Threading.Tasks;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;

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

        [Function("SafeExchange-SecretMeta")]
        public async Task<HttpResponseData> RunSecret(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "get", "patch", "delete", Route = $"{Version}/secret/{{secretId}}")]
            HttpRequestData req,
            string secretId, ClaimsPrincipal principal, ILogger log)
        {
            return await this.metaHandler.Run(req, secretId, principal, log);
        }

        [Function("SafeExchange-ListSecretMeta")]
        public async Task<HttpResponseData> RunListSecret(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/secret-list")]
            HttpRequestData req, ClaimsPrincipal principal, ILogger log)
        {
            return await this.metaHandler.RunList(req, principal, log);
        }

        [Function("SafeExchange-SecretContentMetaCreate")]
        public async Task<HttpResponseData> RunCreateContent(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = $"{Version}/secret/{{secretId}}/content")]
            HttpRequestData req,
            string secretId, ClaimsPrincipal principal, ILogger log)
        {
            return await this.contentMetaHandler.Run(req, secretId, string.Empty, principal, log);
        }

        [Function("SafeExchange-SecretContentMeta")]
        public async Task<HttpResponseData> RunContent(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", "delete", Route = $"{Version}/secret/{{secretId}}/content/{{contentId}}")]
            HttpRequestData req,
            string secretId, string contentId, ClaimsPrincipal principal, ILogger log)
        {
            return await this.contentMetaHandler.Run(req, secretId, contentId, principal, log);
        }

        [Function("SafeExchange-SecretContentMetaDrop")]
        public async Task<HttpResponseData> RunDrop(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = $"{Version}/secret/{{secretId}}/content/{{contentId}}/drop")]
            HttpRequestData req,
            string secretId, string contentId, ClaimsPrincipal principal, ILogger log)
        {
            return await this.contentMetaHandler.RunDrop(req, secretId, contentId, principal, log);
        }

        [Function("SafeExchange-SecretStreamUpload")]
        public async Task<HttpResponseData> RunUploadStream(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = $"{Version}/secret/{{secretId}}/content/{{contentId}}/chunk")]
            HttpRequestData req,
            string secretId, string contentId, ClaimsPrincipal principal, ILogger log)
        {
            return await this.contentHandler.Run(req, secretId, contentId, string.Empty, principal, log);
        }
        
        [Function("SafeExchange-SecretStreamDownload")]
        public async Task<HttpResponseData> RunDownloadStream(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/secret/{{secretId}}/content/{{contentId}}/chunk/{{chunkId}}")]
            HttpRequestData req,
            string secretId, string contentId, string chunkId, ClaimsPrincipal principal, ILogger log)
        {
            return await this.contentHandler.Run(req, secretId, contentId, chunkId, principal, log);
        }

        [Function("SafeExchange-SecretStreamContentDownload")]
        public async Task<HttpResponseData> RunContentDownloadStream(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/secret/{{secretId}}/content/{{contentId}}/all")]
            HttpRequestData req,
            string secretId, string contentId, ClaimsPrincipal principal, ILogger log)
        {
            return await this.contentHandler.RunContentDownload(req, secretId, contentId, principal, log);
        }
    }
}
