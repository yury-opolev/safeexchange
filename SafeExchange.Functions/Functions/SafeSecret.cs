/// <summary>
/// SafeSecret
/// </summary>

namespace SafeExchange.Functions
{
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core;
    using SafeExchange.Core.Functions;
    using System.Security.Claims;
    using System.Threading.Tasks;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;

    public class SafeSecret
    {
        private const string Version = "{apiVersion}";

        private SafeExchangeSecretMeta metaHandler;

        private SafeExchangeSecretContentMeta contentMetaHandler;

        private SafeExchangeSecretStream contentHandler;

        private SafeExchangeContentCommit contentCommitHandler;

        private SafeExchangeSecretAudit auditHandler;

        private readonly ILogger log;

        public SafeSecret(SafeExchangeSecretMeta metaHandler, SafeExchangeSecretContentMeta contentMetaHandler, SafeExchangeSecretStream contentHandler, SafeExchangeContentCommit contentCommitHandler, SafeExchangeSecretAudit auditHandler, ILogger<SafeSecret> log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.metaHandler = metaHandler ?? throw new ArgumentNullException(nameof(metaHandler));
            this.contentMetaHandler = contentMetaHandler ?? throw new ArgumentNullException(nameof(contentMetaHandler));
            this.contentHandler = contentHandler ?? throw new ArgumentNullException(nameof(contentHandler));
            this.contentCommitHandler = contentCommitHandler ?? throw new ArgumentNullException(nameof(contentCommitHandler));
            this.auditHandler = auditHandler ?? throw new ArgumentNullException(nameof(auditHandler));
        }

        [Function("SafeExchange-SecretAudit")]
        public async Task<HttpResponseData> RunAudit(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/secret/{{secretId}}/audit")]
            HttpRequestData request,
            string secretId)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.auditHandler.Run(request, secretId, principal, this.log);
        }

        [Function("SafeExchange-SecretMeta")]
        public async Task<HttpResponseData> RunSecret(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "get", "patch", "delete", Route = $"{Version}/secret/{{secretId}}")]
            HttpRequestData request,
            string secretId)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.metaHandler.Run(request, secretId, principal, this.log);
        }

        [Function("SafeExchange-ListSecretMeta")]
        public async Task<HttpResponseData> RunListSecret(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/secret-list")]
            HttpRequestData request, string apiVersion)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return apiVersion == "v3"
                ? await this.metaHandler.RunListV3(request, principal, this.log)
                : await this.metaHandler.RunList(request, principal, this.log);
        }

        [Function("SafeExchange-SecretContentMetaCreate")]
        public async Task<HttpResponseData> RunCreateContent(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = $"{Version}/secret/{{secretId}}/content")]
            HttpRequestData request,
            string secretId)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.contentMetaHandler.Run(request, secretId, string.Empty, principal, this.log);
        }

        [Function("SafeExchange-SecretContentMeta")]
        public async Task<HttpResponseData> RunContent(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", "delete", Route = $"{Version}/secret/{{secretId}}/content/{{contentId}}")]
            HttpRequestData request,
            string secretId, string contentId)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.contentMetaHandler.Run(request, secretId, contentId, principal, this.log);
        }

        [Function("SafeExchange-SecretContentMetaDrop")]
        public async Task<HttpResponseData> RunDrop(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = $"{Version}/secret/{{secretId}}/content/{{contentId}}/drop")]
            HttpRequestData request,
            string secretId, string contentId)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.contentMetaHandler.RunDrop(request, secretId, contentId, principal, this.log);
        }

        [Function("SafeExchange-SecretStreamUpload")]
        public async Task<HttpResponseData> RunUploadStream(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = $"{Version}/secret/{{secretId}}/content/{{contentId}}/chunk")]
            HttpRequestData request,
            string secretId, string contentId)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.contentHandler.Run(request, secretId, contentId, string.Empty, principal, this.log);
        }

        [Function("SafeExchange-SecretContentCommit")]
        public async Task<HttpResponseData> RunCommitContent(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = $"{Version}/secret/{{secretId}}/content/{{contentId}}/commit")]
            HttpRequestData request,
            string secretId, string contentId)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.contentCommitHandler.Run(request, secretId, contentId, principal, this.log);
        }
        
        [Function("SafeExchange-SecretStreamDownload")]
        public async Task<HttpResponseData> RunDownloadStream(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/secret/{{secretId}}/content/{{contentId}}/chunk/{{chunkId}}")]
            HttpRequestData request,
            string secretId, string contentId, string chunkId)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.contentHandler.Run(request, secretId, contentId, chunkId, principal, this.log);
        }

        [Function("SafeExchange-SecretStreamContentDownload")]
        public async Task<HttpResponseData> RunContentDownloadStream(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/secret/{{secretId}}/content/{{contentId}}/all")]
            HttpRequestData request,
            string secretId, string contentId)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.contentHandler.RunContentDownload(request, secretId, contentId, principal, this.log);
        }
    }
}
