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
    using Microsoft.Extensions.Logging.Abstractions;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Services;
    using SafeExchange.Core.Telemetry;
    using Microsoft.Azure.Functions.Worker.Http;
    using System.Net;

    /// <summary>
    /// HTTP adapter for chunk upload and download (<c>/secret/{secretId}/content/{contentId}/chunk[/{chunkId}]</c>,
    /// <c>.../all</c>) over <see cref="ChunkService"/>.
    /// </summary>
    public class SafeExchangeSecretStream
    {
        public static readonly string AccessTicketHeaderName = "X-SafeExchange-Ticket";
        public static readonly string OperationTypeHeaderName = "X-SafeExchange-OpType";
        public static readonly string ChunkHashHeaderName = "X-SafeExchange-Chunk-Hash";

        public static readonly string InterimOperationType = ChunkService.InterimOperationType;

        private readonly ICallerResolver callerResolver;

        private readonly ChunkService chunkService;

        public SafeExchangeSecretStream(ICallerResolver callerResolver, ChunkService chunkService)
        {
            this.callerResolver = callerResolver ?? throw new ArgumentNullException(nameof(callerResolver));
            this.chunkService = chunkService ?? throw new ArgumentNullException(nameof(chunkService));
        }

        /// <summary>
        /// Pre-service constructor, kept for code that builds handlers by hand: resolves callers through
        /// <paramref name="globalFilters"/> exactly as before and builds the service itself (without logging).
        /// </summary>
        public SafeExchangeSecretStream(IConfiguration configuration, SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, IPurger purger, IBlobHelper blobHelper, IPermissionsManager permissionsManager, IAuditWriter auditWriter)
            : this(
                  new GlobalFiltersCallerResolver(globalFilters, tokenHelper, dbContext),
                  new ChunkService(configuration, dbContext, purger, blobHelper, permissionsManager, auditWriter, NullLogger<ChunkService>.Instance))
        {
        }

        public async Task<HttpResponseData> Run(
            HttpRequestData request,
            string secretId, string contentId, string chunkId, ClaimsPrincipal principal, ILogger log)
        {
            var resolution = await this.callerResolver.ResolveAsync(principal, CallerChannels.Rest, null);
            if (!resolution.IsAllowed)
            {
                return await resolution.Denial.ToResponseAsync(request);
            }

            var caller = resolution.Caller;
            log.LogInformation($"{nameof(SafeExchangeSecretStream)} triggered for '{secretId}' ({contentId}, {chunkId}) by {caller.SubjectType} (tid {TelemetryContext.Current}), [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "post":
                    return await ActionResults.ServiceCallAsync(
                        request,
                        async () => await (await this.chunkService.UploadChunkAsync(caller, secretId, contentId, request.Body, ToUploadRequest(request.Headers, chunkId))).ToResponseAsync(request),
                        "HandleSecretStreamUpload", log);

                case "get":
                    return await ActionResults.ServiceCallAsync(
                        request,
                        async () => await (await this.chunkService.DownloadChunkAsync(caller, secretId, contentId, chunkId)).ToDownloadResponseAsync(request),
                        "HandleSecretStreamDownload", log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        public async Task<HttpResponseData> RunContentDownload(
            HttpRequestData request, string secretId, string contentId, ClaimsPrincipal principal, ILogger log)
        {
            var resolution = await this.callerResolver.ResolveAsync(principal, CallerChannels.Rest, null);
            if (!resolution.IsAllowed)
            {
                return await resolution.Denial.ToResponseAsync(request);
            }

            var caller = resolution.Caller;
            log.LogInformation($"{nameof(SafeExchangeSecretStream)}-ContentDownload triggered for '{secretId}' ({contentId}) by {caller.SubjectType} (tid {TelemetryContext.Current}), [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "get":
                    return await ActionResults.ServiceCallAsync(
                        request,
                        async () => await (await this.chunkService.DownloadContentAsync(caller, secretId, contentId)).ToDownloadResponseAsync(request),
                        "HandleSecretStreamDownload", log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        private static ChunkUploadRequest ToUploadRequest(HttpHeadersCollection headers, string chunkId)
        {
            var declaredLength = 0L;
            if (headers.TryGetValues("Content-Length", out var lengthHeaders) &&
                long.TryParse(lengthHeaders.FirstOrDefault(), out var clen))
            {
                declaredLength = clen;
            }

            return new ChunkUploadRequest
            {
                ChunkId = chunkId,
                AccessTicket = FirstHeaderValue(headers, AccessTicketHeaderName) ?? string.Empty,
                OperationType = FirstHeaderValue(headers, OperationTypeHeaderName) ?? string.Empty,
                ChunkHash = FirstHeaderValue(headers, ChunkHashHeaderName),
                DeclaredLength = declaredLength,
            };
        }

        private static string? FirstHeaderValue(HttpHeadersCollection headers, string name)
            => headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    }
}
