/// <summary>
/// SafeExchangeSecretMeta
/// </summary>

namespace SafeExchange.Core.Functions
{
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;
    using System.Security.Claims;
    using System;
    using System.Web.Http;
    using Microsoft.Extensions.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Permissions;
    using Microsoft.EntityFrameworkCore;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using System.Text.Json;
    using SafeExchange.Core.Model.Dto.Output;

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

        public async Task<IActionResult> Run(
            HttpRequest req,
            string secretId, string contentId, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetFilterResultAsync(req, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResult ?? new EmptyResult();
            }

            var userUpn = this.tokenHelper.GetUpn(principal);
            log.LogInformation($"{nameof(SafeExchangeSecretContentMeta)} triggered for '{secretId}' by {userUpn}, ID {this.tokenHelper.GetObjectId(principal)} [{req.Method}].");

            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext);

            switch (req.Method.ToLower())
            {
                case "post":
                    return await this.HandleSecretContentMetaCreation(req, secretId, contentId, principal, log);

                case "patch":
                    return await this.HandleSecretContentMetaUpdate(req, secretId, contentId, principal, log);

                case "delete":
                    return await this.HandleSecretContentMetaDeletion(req, secretId, contentId, principal, log);

                default:
                    return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized." });
            }
        }

        public async Task<IActionResult> RunDrop(
            HttpRequest req,
            string secretId, string contentId, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetFilterResultAsync(req, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResult ?? new EmptyResult();
            }

            var userUpn = this.tokenHelper.GetUpn(principal);
            log.LogInformation($"{nameof(SafeExchangeSecretContentMeta)} triggered for '{secretId}' by {userUpn}, ID {this.tokenHelper.GetObjectId(principal)} [DROP ({req.Method})].");

            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext);

            switch (req.Method.ToLower())
            {
                case "patch":
                    return await this.HandleSecretContentMetaDrop(req, secretId, contentId, principal, log);

                default:
                    return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized." });
            }
        }

        private async Task<IActionResult> HandleSecretContentMetaCreation(HttpRequest request, string secretId, string contentId, ClaimsPrincipal principal, ILogger log)
            => await TryCatch(async () => 
        {
            var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
            if (existingMetadata == null)
            {
                log.LogInformation($"Cannot create content for secret '{secretId}', as secret not exists.");
                return new NotFoundObjectResult(new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretId}' not exists." });
            }

            if (!string.IsNullOrEmpty(contentId))
            {
                log.LogInformation($"Cannot create content for secret '{secretId}' with specified name.");
                return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "bad_request", Error = $"Cannot specify content name on creation." });
            }

            var userUpn = this.tokenHelper.GetUpn(principal);
            if (!(await this.permissionsManager.IsAuthorizedAsync(userUpn, secretId, PermissionType.Write)))
            {
                return ActionResults.InsufficientPermissionsResult(PermissionType.Write, secretId);
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
                return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Content settings are not provided." });
            }

            if ((contentMetadataInput == null) || string.IsNullOrEmpty(contentMetadataInput.ContentType))
            {
                log.LogInformation($"Content settings for '{secretId}' not provided.");
                return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Content settings are not provided." });
            }

            var newContent = new ContentMetadata(contentMetadataInput);
            existingMetadata.Content.Add(newContent);
            await this.dbContext.SaveChangesAsync();

            return new OkObjectResult(new BaseResponseObject<ContentMetadataOutput> { Status = "ok", Result = newContent.ToDto() });

        }, nameof(HandleSecretContentMetaCreation), log);

        private async Task<IActionResult> HandleSecretContentMetaUpdate(HttpRequest request, string secretId, string contentId, ClaimsPrincipal principal, ILogger log)
            => await TryCatch(async () =>
        {
            var userName = this.tokenHelper.GetUpn(principal);
            var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
            if (existingMetadata == null)
            {
                log.LogInformation($"Cannot update content for secret '{secretId}', as secret not exists.");
                return new NotFoundObjectResult(new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretId}' not exists." });
            }

            if (!existingMetadata.KeepInStorage)
            {
                log.LogInformation($"The data is not kept in storage, cannot use this api.");
                return new UnprocessableEntityObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Cannot use this endpoint for previous versions data." });
            }

            if (string.IsNullOrEmpty(contentId))
            {
                log.LogInformation($"Cannot update content for secret '{secretId}' without specified name.");
                return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "bad_request", Error = $"Must specify content name on update." });
            }

            var userUpn = this.tokenHelper.GetUpn(principal);
            if (!(await this.permissionsManager.IsAuthorizedAsync(userUpn, secretId, PermissionType.Write)))
            {
                return ActionResults.InsufficientPermissionsResult(PermissionType.Write, secretId);
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
                return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Content settings are not provided." });
            }

            if ((contentMetadataInput == null) || string.IsNullOrEmpty(contentMetadataInput.ContentType))
            {
                log.LogInformation($"Content settings for '{secretId}' content '{contentId}' not provided.");
                return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Content settings are not provided." });
            }

            var existingContent = existingMetadata.Content.First(c => c.ContentName.Equals(contentId));
            existingContent.ContentType = contentMetadataInput.ContentType;
            existingContent.FileName = contentMetadataInput.FileName ?? string.Empty;
            await this.dbContext.SaveChangesAsync();

            return new OkObjectResult(new BaseResponseObject<ContentMetadataOutput> { Status = "ok", Result = existingContent.ToDto() });

        }, nameof(HandleSecretContentMetaDeletion), log);

        private async Task<IActionResult> HandleSecretContentMetaDrop(HttpRequest request, string secretId, string contentId, ClaimsPrincipal principal, ILogger log)
            => await TryCatch(async () =>
        {
            var userName = this.tokenHelper.GetUpn(principal);
            var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
            if (existingMetadata == null)
            {
                log.LogInformation($"Cannot drop content for secret '{secretId}', as secret not exists.");
                return new NotFoundObjectResult(new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretId}' not exists." });
            }

            if (!existingMetadata.KeepInStorage)
            {
                log.LogInformation($"The data is not kept in storage, cannot use this api.");
                return new UnprocessableEntityObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Cannot use this endpoint for previous versions data." });
            }

            if (string.IsNullOrEmpty(contentId))
            {
                log.LogInformation($"Cannot drop content for secret '{secretId}' without specified name.");
                return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "bad_request", Error = $"Must specify content name on drop." });
            }

            var userUpn = this.tokenHelper.GetUpn(principal);
            if (!(await this.permissionsManager.IsAuthorizedAsync(userUpn, secretId, PermissionType.Write)))
            {
                return ActionResults.InsufficientPermissionsResult(PermissionType.Write, secretId);
            }

            var existingContent = existingMetadata.Content.First(c => c.ContentName.Equals(contentId));
            var existingAccessTicket = await this.TryGetAccessTicketAsync(existingContent, log);
            if (!string.IsNullOrEmpty(existingAccessTicket))
            {
                log.LogInformation($"Content '{contentId}' for secret '{secretId}' status is being changed by other user, cannot drop.");
                return new UnprocessableEntityObjectResult(new BaseResponseObject<object> { Status = "unprocessable", Error = "Content is being updated." });
            }

            await this.DeleteAllChunksAsync(existingContent, log);

            return new OkObjectResult(new BaseResponseObject<ContentMetadataOutput> { Status = "ok", Result = existingContent.ToDto() });

        }, nameof(HandleSecretContentMetaDeletion), log);

        private async Task<IActionResult> HandleSecretContentMetaDeletion(HttpRequest request, string secretId, string contentId, ClaimsPrincipal principal, ILogger log)
            => await TryCatch(async () =>
        {
            var userName = this.tokenHelper.GetUpn(principal);
            var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
            if (existingMetadata == null)
            {
                log.LogInformation($"Cannot delete content for secret '{secretId}', as secret not exists.");
                return new NotFoundObjectResult(new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretId}' not exists." });
            }

            if (!existingMetadata.KeepInStorage)
            {
                log.LogInformation($"The data is not kept in storage, cannot use this api.");
                return new UnprocessableEntityObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Cannot use this endpoint for previous versions data." });
            }

            if (string.IsNullOrEmpty(contentId))
            {
                log.LogInformation($"Cannot delete content for secret '{secretId}' without specified name.");
                return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "bad_request", Error = $"Must specify content name on deletion." });
            }

            var userUpn = this.tokenHelper.GetUpn(principal);
            if (!(await this.permissionsManager.IsAuthorizedAsync(userUpn, secretId, PermissionType.Write)))
            {
                return ActionResults.InsufficientPermissionsResult(PermissionType.Write, secretId);
            }

            var existingContent = existingMetadata.Content.First(c => c.ContentName.Equals(contentId));

            if (existingContent.IsMain)
            {
                log.LogInformation($"Cannot delete main content for secret '{secretId}'.");
                return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "bad_request", Error = $"Cannot delete main content." });
            }

            var existingAccessTicket = await this.TryGetAccessTicketAsync(existingContent, log);
            if (!string.IsNullOrEmpty(existingAccessTicket))
            {
                log.LogInformation($"Content '{contentId}' for secret '{secretId}' status is being updated, cannot delete.");
                return new UnprocessableEntityObjectResult(new BaseResponseObject<object> { Status = "unprocessable", Error = "Content is being updated." });
            }

            await this.DeleteAllChunksAsync(existingContent, log);
            existingMetadata.Content.Remove(existingContent);
            await this.dbContext.SaveChangesAsync();

            return new OkObjectResult(new BaseResponseObject<string> { Status = "ok", Result = "ok" });

        }, nameof(HandleSecretContentMetaDeletion), log);

        private async Task DeleteAllChunksAsync(ContentMetadata content, ILogger log)
        {
            try
            {
                content.Status = ContentStatus.Updating;
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
                content.AccessTicket = string.Empty;
                content.AccessTicketSetAt = DateTime.MinValue;

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

        private static async Task<IActionResult> TryCatch(Func<Task<IActionResult>> action, string actionName, ILogger log)
        {
            try
            {
                return await action();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, $"{actionName} had exception {ex.GetType()}: {ex.Message}");
                return new ExceptionResult(ex, true);
            }
        }
    }
}
