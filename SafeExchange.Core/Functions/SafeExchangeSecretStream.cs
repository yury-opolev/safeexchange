/// <summary>
/// SafeExchangeSecretStream
/// </summary>

namespace SafeExchange.Core.Functions
{
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using System.Security.Claims;
    using System;
    using Microsoft.Extensions.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using System.Net.Mime;
    using System.Text;
    using Microsoft.Azure.Functions.Worker.Http;
    using System.Net;
    using Ganss.Xss;

    public class SafeExchangeSecretStream
    {
        public static readonly string AccessTicketHeaderName = "X-SafeExchange-Ticket";
        public static readonly string OperationTypeHeaderName = "X-SafeExchange-OpType";

        public static readonly string InterimOperationType = "interim";

        private static readonly string DefaultContentType = "text/html; charset=utf-8";

        private readonly Features features;

        private readonly AccessTicketConfiguration accessTicketConfiguration;

        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        private readonly IPurger purger;

        private readonly IBlobHelper blobHelper;

        private readonly IPermissionsManager permissionsManager;

        public SafeExchangeSecretStream(IConfiguration configuration, SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, IPurger purger, IBlobHelper blobHelper, IPermissionsManager permissionsManager)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            this.features = new Features();
            configuration.GetSection("Features").Bind(this.features);

            this.accessTicketConfiguration = new AccessTicketConfiguration();
            configuration.GetSection("AccessTickets").Bind(this.accessTicketConfiguration);

            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.purger = purger ?? throw new ArgumentNullException(nameof(purger));
            this.blobHelper = blobHelper ?? throw new ArgumentNullException(nameof(blobHelper));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
        }

        public async Task<HttpResponseData> Run(
            HttpRequestData request,
            string secretId, string contentId, string chunkId, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResult ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            (SubjectType subjectType, string subjectId) = await SubjectHelper.GetSubjectInfoAsync(this.tokenHelper, principal, this.dbContext);
            if (SubjectType.Application.Equals(subjectType) && string.IsNullOrEmpty(subjectId))
            {
                await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Forbidden,
                    new BaseResponseObject<object> { Status = "forbidden", Error = "Application is not registered or disabled." });
            }

            log.LogInformation($"{nameof(SafeExchangeSecretStream)} triggered for '{secretId}' ({contentId}, {chunkId}) by {subjectType} {subjectId}, [{request.Method}].");

            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext);

            switch (request.Method.ToLower())
            {
                case "post":
                    return await this.HandleSecretStreamUpload(request, secretId, contentId, chunkId, subjectType, subjectId, log);

                case "get":
                    return await this.HandleSecretStreamDownload(request, secretId, contentId, chunkId, subjectType, subjectId, log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        public async Task<HttpResponseData> RunContentDownload(
            HttpRequestData request, string secretId, string contentId, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResult ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            (SubjectType subjectType, string subjectId) = await SubjectHelper.GetSubjectInfoAsync(this.tokenHelper, principal, this.dbContext);
            if (SubjectType.Application.Equals(subjectType) && string.IsNullOrEmpty(subjectId))
            {
                await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Forbidden,
                    new BaseResponseObject<object> { Status = "forbidden", Error = "Application is not registered or disabled." });
            }

            log.LogInformation($"{nameof(SafeExchangeSecretStream)}-ContentDownload triggered for '{secretId}' ({contentId}) by {subjectType} {subjectId}, [{request.Method}].");

            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext);

            switch (request.Method.ToLower())
            {
                case "get":
                    return await this.HandleSecretContentStreamDownload(request, secretId, contentId, subjectType, subjectId, log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        private static (string accessTicket, string operationType) TryGetOperationTypeAndTicket(HttpHeadersCollection headers)
        {
            var accessTicket = string.Empty;
            if (headers.TryGetValues(AccessTicketHeaderName, out var ticketHeaders))
            {
                accessTicket = ticketHeaders.FirstOrDefault() ?? string.Empty;
            }

            var type = string.Empty;
            if (headers.TryGetValues(OperationTypeHeaderName, out var typeHeaders))
            {
                type = typeHeaders.FirstOrDefault() ?? string.Empty;
            }

            return (accessTicket, type);
        }

        private async Task<HttpResponseData> HandleSecretStreamUpload(HttpRequestData request, string secretId, string contentId, string chunkId, SubjectType subjectType, string subjectId, ILogger log)
        {
            return await TryCatch(request, async () =>
            {
                var existingMetadata = await this.dbContext.Objects.FindAsync(secretId);
                if (existingMetadata == null)
                {
                    log.LogInformation($"Cannot upload content for secret '{secretId}', as secret not exists.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretId}' does not exist." });
                }

                if (!existingMetadata.KeepInStorage)
                {
                    log.LogInformation($"The data is not kept in storage, cannot use this api.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.UnprocessableEntity,
                        new BaseResponseObject<object> { Status = "unprocessable", Error = "Cannot use this endpoint for previous versions data." });
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(subjectType, subjectId, secretId, PermissionType.Write)))
                {
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.Forbidden,
                        ActionResults.InsufficientPermissions(PermissionType.Write, secretId, string.Empty));
                }

                var existingContent = existingMetadata.Content.FirstOrDefault(c => c.ContentName.Equals(contentId));
                if (existingContent == null)
                {
                    log.LogInformation($"Cannot upload content '{contentId}' for secret '{secretId}', as content not exists.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = $"Content '{contentId}' does not exist." });
                }

                if (!string.IsNullOrEmpty(chunkId))
                {
                    log.LogInformation($"Cannot upload content for secret '{secretId}' with specified chunk Id.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "bad_request", Error = $"Cannot specify chunk id on upload." });
                }

                (var accessTicket, var operationStatus) = TryGetOperationTypeAndTicket(request.Headers);

                var existingAccessTicket = await this.TryGetAccessTicketAsync(existingContent, log);
                if (!string.IsNullOrEmpty(existingAccessTicket) && !existingAccessTicket.Equals(accessTicket))
                {
                    log.LogInformation($"Cannot upload content for secret '{secretId}', is being changed by other user.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.UnprocessableEntity,
                        new BaseResponseObject<object> { Status = "unprocessable", Error = "Content is being updated." });
                }

                if (string.IsNullOrEmpty(accessTicket))
                {
                    log.LogInformation($"Setting access ticket to '{existingContent.ContentName}'.");
                    accessTicket = $"{Guid.NewGuid()}-{Random.Shared.NextInt64():00000000}";

                    existingContent.Status = ContentStatus.Updating;
                    existingContent.AccessTicket = accessTicket;
                    existingContent.AccessTicketSetAt = DateTimeProvider.UtcNow;

                    await this.dbContext.SaveChangesAsync();
                }

                var dataStream = request.Body;
                var newChunkName = $"{existingContent.ContentName}-{(existingContent.Chunks.Count):00000000}";

                var dataLength = 0L;
                if (existingContent.IsMain)
                {
                    dataLength = await this.UploadMainContentAsync(newChunkName, dataStream, log);
                }
                else
                {
                    await this.blobHelper.EncryptAndUploadBlobAsync(newChunkName, dataStream);

                    try
                    {
                        dataLength = dataStream.Length;
                    }
                    catch (NotSupportedException exception)
                    {
                        log.LogWarning(exception, $"Cannot get content length for '{secretId}': '{newChunkName}'.");
                    }
                }

                var newChunk = new ChunkMetadata()
                {
                    ChunkName = newChunkName,
                    Hash = string.Empty,
                    Length = dataLength
                };

                existingContent.Chunks.Add(newChunk);

                if (InterimOperationType.Equals(operationStatus, StringComparison.InvariantCultureIgnoreCase))
                {
                    existingContent.AccessTicketSetAt = DateTimeProvider.UtcNow;
                }
                else
                {
                    log.LogInformation($"Clearing access ticket from '{existingContent.ContentName}'.");
                    accessTicket = string.Empty;
                    existingContent.Status = ContentStatus.Ready;
                    existingContent.AccessTicket = string.Empty;
                    existingContent.AccessTicketSetAt = DateTime.MinValue;
                }

                existingMetadata.LastAccessedAt = DateTimeProvider.UtcNow;
                await this.dbContext.SaveChangesAsync();

                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK,
                    new BaseResponseObject<ChunkCreationOutput> { Status = "ok", Result = newChunk.ToCreationDto(accessTicket) });

            }, nameof(HandleSecretStreamUpload), log);
        }

        private async Task<HttpResponseData> HandleSecretStreamDownload(HttpRequestData request, string secretId, string contentId, string chunkId, SubjectType subjectType, string subjectId, ILogger log)
        {
            return await TryCatch(request, async () =>
            {
                var existingMetadata = await this.dbContext.Objects.FindAsync(secretId);
                if (existingMetadata == null)
                {
                    log.LogInformation($"Cannot download content for secret '{secretId}', as secret not exists.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretId}' does not exist." });
                }

                if (!existingMetadata.KeepInStorage)
                {
                    log.LogInformation($"The data is not kept in storage, cannot use this api.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.UnprocessableEntity,
                        new BaseResponseObject<object> { Status = "unprocessable", Error = "Cannot use this endpoint for previous versions data." });
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(subjectType, subjectId, secretId, PermissionType.Read)))
                {
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.Forbidden,
                        ActionResults.InsufficientPermissions(PermissionType.Read, secretId, string.Empty));
                }

                var existingContent = existingMetadata.Content.FirstOrDefault(c => c.ContentName.Equals(contentId));
                if (existingContent == null)
                {
                    log.LogInformation($"Cannot download content '{contentId}' for secret '{secretId}', as content not exists.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = $"Content '{contentId}' does not exist." });
                }

                var existingAccessTicket = await this.TryGetAccessTicketAsync(existingContent, log);
                if (!string.IsNullOrEmpty(existingAccessTicket))
                {
                    log.LogInformation($"Cannot download content '{contentId}' for secret '{secretId}', as content is being updated by other user.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.UnprocessableEntity,
                        new BaseResponseObject<object> { Status = "unprocessable", Error = $"Content '{contentId}' is being updated." });
                }

                if (existingContent.Status != ContentStatus.Ready)
                {
                    log.LogInformation($"Cannot download content '{contentId}' for secret '{secretId}', as content is not ready.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.UnprocessableEntity,
                        new BaseResponseObject<object> { Status = "unprocessable", Error = $"Content '{contentId}' is not ready." });
                }

                if (string.IsNullOrEmpty(chunkId))
                {
                    log.LogInformation($"Cannot download content for secret '{secretId}' without specified chunk Id.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "bad_request", Error = $"Must specify chunk id on download." });
                }

                var existingChunk = existingContent.Chunks.FirstOrDefault(c => c.ChunkName.Equals(chunkId));
                if (existingChunk == null)
                {
                    log.LogInformation($"Cannot download content for secret '{secretId}', as chunk '{chunkId}' for content '{contentId}' not exists.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = $"Chunk '{chunkId}' does not exist." });
                }

                var response = request.CreateResponse(HttpStatusCode.OK);
                response.Headers.Add("Content-Type", string.IsNullOrEmpty(existingContent.ContentType) ? DefaultContentType : existingContent.ContentType);
                response.Headers.Add("Content-Length", $"{existingChunk.Length}");

                var dataStream = await this.blobHelper.DownloadAndDecryptBlobAsync(existingChunk.ChunkName);
                await dataStream.CopyToAsync(response.Body);

                existingMetadata.LastAccessedAt = DateTimeProvider.UtcNow;
                await this.dbContext.SaveChangesAsync();

                return response;

            }, nameof(HandleSecretStreamDownload), log);
        }

        private async Task<HttpResponseData> HandleSecretContentStreamDownload(HttpRequestData request, string secretId, string contentId, SubjectType subjectType, string subjectId, ILogger log)
        {
            return await TryCatch(request, async () =>
            {
                var existingMetadata = await this.dbContext.Objects.FindAsync(secretId);
                if (existingMetadata == null)
                {
                    log.LogInformation($"Cannot download content for secret '{secretId}', as secret not exists.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretId}' does not exist." });
                }

                if (!existingMetadata.KeepInStorage)
                {
                    log.LogInformation($"The data is not kept in storage, cannot use this api.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.UnprocessableEntity,
                        new BaseResponseObject<object> { Status = "unprocessable", Error = "Cannot use this endpoint for previous versions data." });
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(subjectType, subjectId, secretId, PermissionType.Read)))
                {
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.Forbidden,
                        ActionResults.InsufficientPermissions(PermissionType.Read, secretId, string.Empty));
                }

                var existingContent = existingMetadata.Content.FirstOrDefault(c => c.ContentName.Equals(contentId));
                if (existingContent == null)
                {
                    log.LogInformation($"Cannot download content '{contentId}' for secret '{secretId}', as content not exists.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = $"Content '{contentId}' not exists." });
                }

                var existingAccessTicket = await this.TryGetAccessTicketAsync(existingContent, log);
                if (!string.IsNullOrEmpty(existingAccessTicket))
                {
                    log.LogInformation($"Cannot download content '{contentId}' for secret '{secretId}', as content is being updated by other user.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.UnprocessableEntity,
                        new BaseResponseObject<object> { Status = "unprocessable", Error = $"Content '{contentId}' is being updated." });
                }

                if (existingContent.Status != ContentStatus.Ready)
                {
                    log.LogInformation($"Cannot download content '{contentId}' for secret '{secretId}', as content is not ready.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.UnprocessableEntity,
                        new BaseResponseObject<object> { Status = "unprocessable", Error = $"Content '{contentId}' is not ready." });
                }

                var contentLength = 0L;
                foreach (var chunk in existingContent.Chunks)
                {
                    contentLength += chunk.Length;
                }

                var response = request.CreateResponse(HttpStatusCode.OK);
                response.Headers.Add("Content-Type", string.IsNullOrEmpty(existingContent.ContentType) ? DefaultContentType : existingContent.ContentType);
                response.Headers.Add("Content-Length", $"{contentLength}");
                response.Headers.Add("Content-Disposition", (new ContentDisposition { FileName = existingContent.FileName }).ToString());

                foreach (var chunk in existingContent.Chunks)
                {
                    var chunkStream = await this.blobHelper.DownloadAndDecryptBlobAsync(chunk.ChunkName);
                    await chunkStream.CopyToAsync(response.Body);
                }

                existingMetadata.LastAccessedAt = DateTimeProvider.UtcNow;
                await this.dbContext.SaveChangesAsync();

                return response;

            }, nameof(HandleSecretStreamDownload), log);
        }

        private async ValueTask<string> TryGetAccessTicketAsync(ContentMetadata content, ILogger log)
        {
            if (string.IsNullOrEmpty(content.AccessTicket))
            {
                return String.Empty;
            }

            var utcNow = DateTimeProvider.UtcNow;
            if ((this.accessTicketConfiguration.AccessTicketTimeout > TimeSpan.Zero)
                && utcNow > (content.AccessTicketSetAt + this.accessTicketConfiguration.AccessTicketTimeout))
            {
                log.LogInformation($"Access ticket '{content.AccessTicket}' expired (was set at {content.AccessTicketSetAt}) and is to be dropped. All data will be erased.");

                await this.purger.DeleteContentDataAsync(content);

                content.Chunks.Clear();
                content.Status = ContentStatus.Blank;

                content.AccessTicket = String.Empty;
                content.AccessTicketSetAt = DateTime.MinValue;

                await this.dbContext.SaveChangesAsync();
            }

            return content.AccessTicket;
        }

        private async Task<long> UploadMainContentAsync(string chunkName, Stream inputStream, ILogger log)
        {
            log.LogInformation("Sanitizing main content.");

            string content;
            using (var reader = new StreamReader(inputStream))
            {
                content = await reader.ReadToEndAsync();
            }

            var lengthBefore = content.Length;

            var sanitizer = new HtmlSanitizer();
            sanitizer.AllowedSchemes.Add("data");
            sanitizer.AllowedAttributes.Add("class");
            sanitizer.AllowedAttributes.Add("data-value");
            sanitizer.AllowedAttributes.Add("data-bs-toggle");
            sanitizer.AllowedAttributes.Add("data-bs-placement");
            sanitizer.AllowedAttributes.Add("title");

            content = sanitizer.Sanitize(content);

            log.LogInformation($"Lenght before sanitizing: {lengthBefore}, after sanitizing: {content.Length}.");

            using (var dataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)))
            {
                await this.blobHelper.EncryptAndUploadBlobAsync(chunkName, dataStream);
                return dataStream.Length;
            }
        }

        private static async Task<HttpResponseData> TryCatch(HttpRequestData request, Func<Task<HttpResponseData>> action, string actionName, ILogger log)
        {
            try
            {
                return await action();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, $"{actionName} had exception {ex.GetType()}: {ex.Message}");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.InternalServerError,
                    new BaseResponseObject<object> { Status = "error", Error = $"{ex.GetType()}: {ex.Message}" });
            }
        }
    }
}
