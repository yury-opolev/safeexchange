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

        private readonly ILogger log;

        public SafeSecret(IConfiguration configuration, SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, IPurger purger, IPermissionsManager permissionsManager, IBlobHelper blobHelper, ILogger<SafeSecret> log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.metaHandler = new SafeExchangeSecretMeta(configuration, dbContext, tokenHelper, globalFilters, purger, permissionsManager);
            this.contentMetaHandler = new SafeExchangeSecretContentMeta(configuration, dbContext, tokenHelper, globalFilters, purger, permissionsManager);
            this.contentHandler = new SafeExchangeSecretStream(configuration, dbContext, tokenHelper, globalFilters, purger, blobHelper, permissionsManager);
        }

        [Function("SafeExchange-SecretMeta")]
        public async Task<HttpResponseData> RunSecret(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "get", "patch", "delete", Route = $"{Version}/secret/{{secretId}}")]
            HttpRequestData request,
            string secretId)
        {
            var principal = new ClaimsPrincipal(request.Identities.FirstOrDefault() ?? new ClaimsIdentity());
            return await this.metaHandler.Run(request, secretId, principal, this.log);
        }

        [Function("SafeExchange-ListSecretMeta")]
        public async Task<HttpResponseData> RunListSecret(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/secret-list")]
            HttpRequestData request)
        {
            var principal = new ClaimsPrincipal(request.Identities.FirstOrDefault() ?? new ClaimsIdentity());
            return await this.metaHandler.RunList(request, principal, this.log);
        }

        [Function("SafeExchange-SecretContentMetaCreate")]
        public async Task<HttpResponseData> RunCreateContent(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = $"{Version}/secret/{{secretId}}/content")]
            HttpRequestData request,
            string secretId)
        {
            var principal = new ClaimsPrincipal(request.Identities.FirstOrDefault() ?? new ClaimsIdentity());
            return await this.contentMetaHandler.Run(request, secretId, string.Empty, principal, this.log);
        }

        [Function("SafeExchange-SecretContentMeta")]
        public async Task<HttpResponseData> RunContent(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", "delete", Route = $"{Version}/secret/{{secretId}}/content/{{contentId}}")]
            HttpRequestData request,
            string secretId, string contentId)
        {
            var principal = new ClaimsPrincipal(request.Identities.FirstOrDefault() ?? new ClaimsIdentity());
            return await this.contentMetaHandler.Run(request, secretId, contentId, principal, this.log);
        }

        [Function("SafeExchange-SecretContentMetaDrop")]
        public async Task<HttpResponseData> RunDrop(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = $"{Version}/secret/{{secretId}}/content/{{contentId}}/drop")]
            HttpRequestData request,
            string secretId, string contentId)
        {
            var principal = new ClaimsPrincipal(request.Identities.FirstOrDefault() ?? new ClaimsIdentity());
            return await this.contentMetaHandler.RunDrop(request, secretId, contentId, principal, this.log);
        }

        [Function("SafeExchange-SecretStreamUpload")]
        public async Task<HttpResponseData> RunUploadStream(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = $"{Version}/secret/{{secretId}}/content/{{contentId}}/chunk")]
            HttpRequestData request,
            string secretId, string contentId)
        {
            var principal = new ClaimsPrincipal(request.Identities.FirstOrDefault() ?? new ClaimsIdentity());
            return await this.contentHandler.Run(request, secretId, contentId, string.Empty, principal, this.log);
        }
        
        [Function("SafeExchange-SecretStreamDownload")]
        public async Task<HttpResponseData> RunDownloadStream(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/secret/{{secretId}}/content/{{contentId}}/chunk/{{chunkId}}")]
            HttpRequestData request,
            string secretId, string contentId, string chunkId)
        {
            var principal = new ClaimsPrincipal(request.Identities.FirstOrDefault() ?? new ClaimsIdentity());
            return await this.contentHandler.Run(request, secretId, contentId, chunkId, principal, this.log);
        }

        [Function("SafeExchange-SecretStreamContentDownload")]
        public async Task<HttpResponseData> RunContentDownloadStream(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/secret/{{secretId}}/content/{{contentId}}/all")]
            HttpRequestData request,
            string secretId, string contentId)
        {
            var principal = new ClaimsPrincipal(request.Identities.FirstOrDefault() ?? new ClaimsIdentity());
            return await this.contentHandler.RunContentDownload(request, secretId, contentId, principal, this.log);
        }
    }
}
