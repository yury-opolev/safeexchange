/// <summary>
/// SafeExchangeSecretMeta
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
    using Microsoft.EntityFrameworkCore;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using Microsoft.Azure.Functions.Worker.Http;
    using System.Net;

    public class SafeExchangeSecretContentMeta
    {
        private readonly Features features;

        private readonly AccessTicketConfiguration accessTicketConfiguration;

        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        private readonly IPurger purger;

        private readonly IPermissionsManager permissionsManager;

        public SafeExchangeSecretContentMeta(IConfiguration configuration, SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, IPurger purger, IPermissionsManager permissionsManager)
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
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
        }

        public async Task<HttpResponseData> Run(
            HttpRequestData req,
            string secretId, string contentId, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetFilterResultAsync(req, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResult ?? req.CreateResponse(HttpStatusCode.NoContent);
            }

            (SubjectType subjectType, string subjectId) = SubjectHelper.GetSubjectInfo(this.tokenHelper, principal);
            log.LogInformation($"{nameof(SafeExchangeSecretContentMeta)} triggered for '{secretId}' by {subjectType} {subjectId} [{req.Method}].");

            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext);

            switch (req.Method.ToLower())
            {
                case "post":
                    return await this.HandleSecretContentMetaCreation(req, secretId, contentId, subjectType, subjectId, log);

                case "patch":
                    return await this.HandleSecretContentMetaUpdate(req, secretId, contentId, subjectType, subjectId, log);

                case "delete":
                    return await this.HandleSecretContentMetaDeletion(req, secretId, contentId, subjectType, subjectId, log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        req, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        public async Task<HttpResponseData> RunDrop(
            HttpRequestData req,
            string secretId, string contentId, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetFilterResultAsync(req, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResult ?? req.CreateResponse(HttpStatusCode.NoContent);
            }

            (SubjectType subjectType, string subjectId) = SubjectHelper.GetSubjectInfo(this.tokenHelper, principal);
            log.LogInformation($"{nameof(SafeExchangeSecretContentMeta)} triggered for '{secretId}' by {subjectType} {subjectId} [DROP ({req.Method})].");

            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext);

            switch (req.Method.ToLower())
            {
                case "patch":
                    return await this.HandleSecretContentMetaDrop(req, secretId, contentId, subjectType, subjectId, log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        req, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        private async Task<HttpResponseData> HandleSecretContentMetaCreation(HttpRequestData request, string secretId, string contentId, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () => 
        {
            var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
            if (existingMetadata == null)
            {
                log.LogInformation($"Cannot create content for secret '{secretId}', as secret not exists.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.NotFound,
                    new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretId}' does not exist." });
            }

            if (!string.IsNullOrEmpty(contentId))
            {
                log.LogInformation($"Cannot create content for secret '{secretId}' with specified name.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "bad_request", Error = "Cannot specify content name on creation." });
            }

            if (!(await this.permissionsManager.IsAuthorizedAsync(subjectType, subjectId, secretId, PermissionType.Write)))
            {
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Forbidden,
                    ActionResults.InsufficientPermissions(PermissionType.Write, secretId, string.Empty));
            }

            var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            ContentMetadataCreationInput? contentMetadataInput;
            try
            {
                contentMetadataInput = DefaultJsonSerializer.Deserialize<ContentMetadataCreationInput>(requestBody);
            }
            catch
            {
                log.LogInformation($"Could not parse input data for '{secretId}' new content.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "bad_request", Error = "Content settings are not provided." });
            }

            if ((contentMetadataInput == null) || string.IsNullOrEmpty(contentMetadataInput.ContentType))
            {
                log.LogInformation($"Content settings for '{secretId}' not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "bad_request", Error = "Content settings are not provided." });
            }

            var newContent = new ContentMetadata(contentMetadataInput);
            existingMetadata.Content.Add(newContent);
            existingMetadata.LastAccessedAt = DateTimeProvider.UtcNow;
            await this.dbContext.SaveChangesAsync();

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<ContentMetadataOutput> { Status = "ok", Result = newContent.ToDto() });

        }, nameof(HandleSecretContentMetaCreation), log);

        private async Task<HttpResponseData> HandleSecretContentMetaUpdate(HttpRequestData request, string secretId, string contentId, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () =>
        {
            var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
            if (existingMetadata == null)
            {
                log.LogInformation($"Cannot update content for secret '{secretId}', as secret not exists.");
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

            if (string.IsNullOrEmpty(contentId))
            {
                log.LogInformation($"Cannot update content for secret '{secretId}' without specified name.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "bad_request", Error = "Must specify content name on update." });
            }

            if (!(await this.permissionsManager.IsAuthorizedAsync(subjectType, subjectId, secretId, PermissionType.Write)))
            {
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Forbidden,
                    ActionResults.InsufficientPermissions(PermissionType.Write, secretId, string.Empty));
            }

            var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            ContentMetadataUpdateInput? contentMetadataInput;
            try
            {
                contentMetadataInput = DefaultJsonSerializer.Deserialize<ContentMetadataUpdateInput>(requestBody);
            }
            catch
            {
                log.LogInformation($"Could not parse input data for '{secretId}' new content.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "bad_request", Error = "Content settings are not provided." });
            }

            if ((contentMetadataInput == null) || string.IsNullOrEmpty(contentMetadataInput.ContentType))
            {
                log.LogInformation($"Content settings for '{secretId}' content '{contentId}' not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "bad_request", Error = "Content settings are not provided." });
            }

            var existingContent = existingMetadata.Content.First(c => c.ContentName.Equals(contentId));
            existingContent.ContentType = contentMetadataInput.ContentType;
            existingContent.FileName = contentMetadataInput.FileName ?? string.Empty;
            existingMetadata.LastAccessedAt = DateTimeProvider.UtcNow;
            await this.dbContext.SaveChangesAsync();

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<ContentMetadataOutput> { Status = "ok", Result = existingContent.ToDto() });

        }, nameof(HandleSecretContentMetaDeletion), log);

        private async Task<HttpResponseData> HandleSecretContentMetaDrop(HttpRequestData request, string secretId, string contentId, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () =>
        {
            var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
            if (existingMetadata == null)
            {
                log.LogInformation($"Cannot drop content for secret '{secretId}', as secret not exists.");
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

            if (string.IsNullOrEmpty(contentId))
            {
                log.LogInformation($"Cannot drop content for secret '{secretId}' without specified name.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "bad_request", Error = "Must specify content name on drop." });
            }

            if (!(await this.permissionsManager.IsAuthorizedAsync(subjectType, subjectId, secretId, PermissionType.Write)))
            {
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Forbidden,
                    ActionResults.InsufficientPermissions(PermissionType.Write, secretId, string.Empty));
            }

            var existingContent = existingMetadata.Content.First(c => c.ContentName.Equals(contentId));
            var existingAccessTicket = await this.TryGetAccessTicketAsync(existingContent, log);
            if (!string.IsNullOrEmpty(existingAccessTicket))
            {
                log.LogInformation($"Content '{contentId}' for secret '{secretId}' status is being changed by other user, cannot drop.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.UnprocessableEntity,
                    new BaseResponseObject<object> { Status = "unprocessable", Error = "Content is being updated." });
            }

            await this.DeleteAllChunksAsync(existingMetadata, existingContent, false, log);

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<ContentMetadataOutput> { Status = "ok", Result = existingContent.ToDto() });

        }, nameof(HandleSecretContentMetaDeletion), log);

        private async Task<HttpResponseData> HandleSecretContentMetaDeletion(HttpRequestData request, string secretId, string contentId, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () =>
        {
            var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
            if (existingMetadata == null)
            {
                log.LogInformation($"Cannot delete content for secret '{secretId}', as secret not exists.");
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

            if (string.IsNullOrEmpty(contentId))
            {
                log.LogInformation($"Cannot delete content for secret '{secretId}' without specified name.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "bad_request", Error = "Must specify content name on deletion." });
            }

            if (!(await this.permissionsManager.IsAuthorizedAsync(subjectType, subjectId, secretId, PermissionType.Write)))
            {
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Forbidden,
                    ActionResults.InsufficientPermissions(PermissionType.Write, secretId, string.Empty));
            }

            var existingContent = existingMetadata.Content.First(c => c.ContentName.Equals(contentId));

            if (existingContent.IsMain)
            {
                log.LogInformation($"Cannot delete main content for secret '{secretId}'.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "bad_request", Error = "Cannot delete main content." });
            }

            var existingAccessTicket = await this.TryGetAccessTicketAsync(existingContent, log);
            if (!string.IsNullOrEmpty(existingAccessTicket))
            {
                log.LogInformation($"Content '{contentId}' for secret '{secretId}' status is being updated, cannot delete.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.UnprocessableEntity,
                    new BaseResponseObject<object> { Status = "unprocessable", Error = "Content is being updated." });
            }

            await this.DeleteAllChunksAsync(existingMetadata, existingContent, true, log);

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<string> { Status = "ok", Result = "ok" });

        }, nameof(HandleSecretContentMetaDeletion), log);

        private async Task DeleteAllChunksAsync(ObjectMetadata metadata, ContentMetadata content, bool removeContent, ILogger log)
        {
            try
            {
                content.Status = ContentStatus.Updating;

                log.LogInformation($"Setting access ticket to '{content.ContentName}'.");
                content.AccessTicket = $"{Guid.NewGuid()}-{Random.Shared.NextInt64():00000000}";
                content.AccessTicketSetAt = DateTimeProvider.UtcNow;

                await this.dbContext.SaveChangesAsync();

                await this.purger.DeleteContentDataAsync(content);
            }
            catch (Exception exception)
            {
                log.LogError(exception, $"Exception inside exclusive operation on content '{content.ContentName}'.");
            }
            finally
            {
                content.Chunks.Clear();
                content.Status = ContentStatus.Blank;

                log.LogInformation($"Clearing access ticket from '{content.ContentName}'.");
                content.AccessTicket = string.Empty;
                content.AccessTicketSetAt = DateTime.MinValue;

                if (removeContent)
                {
                    metadata.Content.Remove(content);
                }

                metadata.LastAccessedAt = DateTimeProvider.UtcNow;

                await this.dbContext.SaveChangesAsync();
            }
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
