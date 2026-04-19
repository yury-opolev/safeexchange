/// <summary>
/// SafeExchangeContentCommit
/// </summary>

namespace SafeExchange.Core.Functions
{
    using System;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Crypto;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;

    public class SafeExchangeContentCommit
    {
        private static readonly Regex HexRegex = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        private readonly IPurger purger;

        private readonly IPermissionsManager permissionsManager;

        public SafeExchangeContentCommit(IConfiguration configuration, SafeExchangeDbContext dbContext,
            ITokenHelper tokenHelper, GlobalFilters globalFilters, IPurger purger, IPermissionsManager permissionsManager)
        {
            if (configuration is null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.purger = purger ?? throw new ArgumentNullException(nameof(purger));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
        }

        public async Task<HttpResponseData> Run(HttpRequestData request, string secretId, string contentId,
            ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResult ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            (SubjectType subjectType, string subjectId) = await SubjectHelper.GetSubjectInfoAsync(this.tokenHelper, principal, this.dbContext);
            if (SubjectType.Application.Equals(subjectType) && string.IsNullOrEmpty(subjectId))
            {
                return await ActionResults.ForbiddenAsync(request, "Application is not registered or disabled.");
            }

            log.LogInformation($"{nameof(SafeExchangeContentCommit)} triggered for '{secretId}' ({contentId}) by {subjectType} {subjectId}.");
            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext);

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var existingMetadata = await this.dbContext.Objects.FindAsync(secretId);
                if (existingMetadata is null)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretId}' does not exist." });
                }

                if (!existingMetadata.KeepInStorage)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.UnprocessableEntity,
                        new BaseResponseObject<object> { Status = "unprocessable", Error = "Cannot commit data for previous-version secrets." });
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(subjectType, subjectId, secretId, PermissionType.Write)))
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.Forbidden,
                        ActionResults.InsufficientPermissions(PermissionType.Write, secretId, string.Empty));
                }

                var existingContent = existingMetadata.Content.FirstOrDefault(c => c.ContentName.Equals(contentId));
                if (existingContent is null)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = $"Content '{contentId}' does not exist." });
                }

                if (existingContent.IsMain)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.UnprocessableEntity,
                        new BaseResponseObject<object> { Status = "unprocessable", Error = "Main content does not support explicit commit." });
                }

                var ticketHeader = request.Headers.TryGetValues(SafeExchangeSecretStream.AccessTicketHeaderName, out var tickets)
                    ? tickets.FirstOrDefault() ?? string.Empty
                    : string.Empty;
                if (string.IsNullOrEmpty(ticketHeader) || !ticketHeader.Equals(existingContent.AccessTicket))
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.Unauthorized,
                        new BaseResponseObject<object> { Status = "unauthorized", Error = "Access ticket missing or invalid." });
                }

                if (existingContent.RunningHashState is null || existingContent.RunningHashState.Length == 0)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.UnprocessableEntity,
                        new BaseResponseObject<object> { Status = "no_upload_state", Error = "No hashed-mode upload in progress for this content." });
                }

                CommitRequest? payload;
                try
                {
                    payload = await JsonSerializer.DeserializeAsync<CommitRequest>(request.Body,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));
                }
                catch (JsonException)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "bad_request", Error = "Body is not valid JSON." });
                }

                var clientHash = payload?.Hash?.ToLowerInvariant() ?? string.Empty;
                if (!HexRegex.IsMatch(clientHash))
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "bad_request", Error = "hash must be 64 hex characters." });
                }

                var running = SerializableSha256.Restore(existingContent.RunningHashState);
                var serverHash = Convert.ToHexString(running.Finish()).ToLowerInvariant();

                if (!serverHash.Equals(clientHash))
                {
                    log.LogWarning("Commit hash mismatch for '{ContentId}': client {Client}, server {Server}", contentId, clientHash, serverHash);
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.UnprocessableEntity,
                        new BaseResponseObject<ChunkHashMismatch>
                        {
                            Status = "hash_mismatch",
                            Error = "Client-asserted whole-content hash does not match server computation.",
                            Result = new ChunkHashMismatch { Expected = clientHash, Actual = serverHash },
                        });
                }

                existingContent.Hash = serverHash;
                existingContent.Status = ContentStatus.Ready;
                existingContent.AccessTicket = string.Empty;
                existingContent.AccessTicketSetAt = DateTime.MinValue;
                existingContent.RunningHashState = null;
                existingMetadata.LastAccessedAt = DateTimeProvider.UtcNow;
                await this.dbContext.SaveChangesAsync();

                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<ContentCommitOutput>
                    {
                        Status = "ok",
                        Result = new ContentCommitOutput { ContentName = contentId, Hash = serverHash },
                    });
            }, nameof(SafeExchangeContentCommit), log);
        }

        private sealed class CommitRequest
        {
            public string? Hash { get; set; }
        }
    }
}
