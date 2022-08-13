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
    using System.IO;
    using System.Text.Json;
    using Microsoft.Extensions.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Model.Dto.Output;
    using Microsoft.EntityFrameworkCore;

    public class SafeExchangeSecretMeta
    {
        private readonly Features features;

        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        private readonly IPurger purger;

        private readonly IPermissionsManager permissionsManager;

        public SafeExchangeSecretMeta(IConfiguration configuration, SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, IPurger purger, IPermissionsManager permissionsManager)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            this.features = new Features();
            configuration.GetSection("Features").Bind(this.features);

            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.purger = purger ?? throw new ArgumentNullException(nameof(purger));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
        }

        public async Task<IActionResult> Run(
            HttpRequest req,
            string secretId, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetFilterResultAsync(req, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResult ?? new EmptyResult();
            }

            var userUpn = this.tokenHelper.GetUpn(principal);
            log.LogInformation($"{nameof(SafeExchangeSecretMeta)} triggered for '{secretId}' by {userUpn}, ID {this.tokenHelper.GetObjectId(principal)} [{req.Method}].");

            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext);
            
            switch (req.Method.ToLower())
            {
                case "post":
                    return await this.HandleSecretMetaCreation(req, secretId, principal, log);

                case "get":
                    return await this.HandleSecretMetaRead(secretId, principal, log);

                case "patch":
                    return await this.HandleSecretMetaUpdate(req, secretId, principal, log);

                case "delete":
                    return await this.HandleSecretMetaDeletion(req, secretId, principal, log);

                default:
                    return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized." });
            }
        }

        public async Task<IActionResult> RunList(
            HttpRequest req, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetFilterResultAsync(req, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResult ?? new EmptyResult();
            }

            var userUpn = this.tokenHelper.GetUpn(principal);
            log.LogInformation($"{nameof(SafeExchangeSecretMeta)}-{nameof(RunList)} triggered by {userUpn}, ID {this.tokenHelper.GetObjectId(principal)} [{req.Method}].");

            switch (req.Method.ToLower())
            {
                case "get":
                    return await this.HandleListSecretMeta(principal, log);

                default:
                    return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized." });
            }
        }

        private async Task<IActionResult> HandleListSecretMeta(ClaimsPrincipal principal, ILogger log)
            => await TryCatch(async () =>
        {
            var userUpn = this.tokenHelper.GetUpn(principal);
            var existingPermissions = await this.dbContext.Permissions.Where(p => p.SubjectName.Equals(userUpn) && p.CanRead).ToListAsync();

            return new OkObjectResult(new BaseResponseObject<List<SubjectPermissionsOutput>>
            {
                Status = "ok",
                Result = existingPermissions.Select(p => p.ToDto()).ToList()
            });

        }, nameof(HandleListSecretMeta), log);

        private async Task<IActionResult> HandleSecretMetaCreation(HttpRequest request, string secretId, ClaimsPrincipal principal, ILogger log)
            => await TryCatch(async () =>
        {
            var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
            if (existingMetadata != null)
            {
                log.LogInformation($"Cannot create secret '{secretId}', as already exists");
                return new ConflictObjectResult(new BaseResponseObject<object> { Status = "conflict", Error = $"Secret '{secretId}' already exists" });
            }

            var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            MetadataCreationInput? metadataInput;
            try
            {
                metadataInput = DefaultJsonSerializer.Deserialize<MetadataCreationInput>(requestBody);
            }
            catch
            {
                log.LogInformation($"Could not parse input data for '{secretId}'.");
                return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Expiration settings are not provided." });
            }

            if ((metadataInput is null) || metadataInput.ExpirationSettings == null)
            {
                log.LogInformation($"Expiration settings for '{secretId}' not provided.");
                return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Expiration settings are not provided." });
            }

            if (string.IsNullOrEmpty(secretId))
            {
                log.LogInformation("Secret id value is not provided.");
                return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Secret id value is not provided." });
            }

            var userUpn = this.tokenHelper.GetUpn(principal);
            var createdMetadata = await this.CreateMetadataAndPermissionsAsync(secretId, metadataInput, userUpn, log);
            log.LogInformation($"Metadata for secret '{secretId}' added, full permissions for user '{userUpn}' are set.");

            return new OkObjectResult(new BaseResponseObject<ObjectMetadataOutput> { Status = "ok", Result = createdMetadata.ToDto() });

        }, nameof(HandleSecretMetaCreation), log);

        private async Task<IActionResult> HandleSecretMetaRead(string secretId, ClaimsPrincipal principal, ILogger log)
            => await TryCatch(async () =>
        {
            var userName = this.tokenHelper.GetUpn(principal);

            var metadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
            if (metadata == null)
            {
                log.LogInformation($"Cannot get secret '{secretId}', as no metadata exists.");
                return new NotFoundObjectResult(new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretId}' not exists." });
            }

            if (!metadata.KeepInStorage)
            {
                log.LogInformation($"The data is not kept in storage, cannot use this api.");
                return new UnprocessableEntityObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Cannot use this endpoint for previous versions data." });
            }

            if (!(await this.permissionsManager.IsAuthorizedAsync(userName, secretId, PermissionType.Read)))
            {
                return ActionResults.InsufficientPermissionsResult(PermissionType.Read, secretId, string.Empty);
            }

            metadata.LastAccessedAt = DateTimeProvider.UtcNow;
            await this.dbContext.SaveChangesAsync();

            return new OkObjectResult(new BaseResponseObject<ObjectMetadataOutput> { Status = "ok", Result = metadata.ToDto() });

        }, nameof(HandleSecretMetaRead), log);

        private async Task<IActionResult> HandleSecretMetaUpdate(HttpRequest request, string secretId, ClaimsPrincipal principal, ILogger log)
            => await TryCatch(async () =>
        {
            var userName = this.tokenHelper.GetUpn(principal);

            var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
            if (existingMetadata == null)
            {
                log.LogInformation($"Cannot update secret '{secretId}', as no metadata exists.");
                return new NotFoundObjectResult(new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretId}' not exists." });
            }

            if (!existingMetadata.KeepInStorage)
            {
                log.LogInformation($"The data is not kept in storage, cannot use this api.");
                return new UnprocessableEntityObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Cannot use this endpoint for previous versions data." });
            }

            if (!(await this.permissionsManager.IsAuthorizedAsync(userName, secretId, PermissionType.Write)))
            {
                return ActionResults.InsufficientPermissionsResult(PermissionType.Write, secretId, string.Empty);
            }

            var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            MetadataUpdateInput? metadataInput;
            try
            {
                metadataInput = DefaultJsonSerializer.Deserialize<MetadataUpdateInput>(requestBody);
            }
            catch
            {
                log.LogInformation($"Could not parse input data for '{secretId}'.");
                return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Expiration settings are not provided." });
            }

            if ((metadataInput is null) || metadataInput.ExpirationSettings == null)
            {
                log.LogInformation($"Expiration settings for '{secretId}' not provided.");
                return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Expiration settings are not provided." });
            }

            if (string.IsNullOrEmpty(secretId))
            {
                log.LogInformation("Secret id value is not provided.");
                return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Secret id value is not provided." });
            }

            var updatedMetadata = await this.UpdateMetadataAsync(existingMetadata, metadataInput, log);
            log.LogInformation($"User '{userName}' updated metadata for secret '{existingMetadata.ObjectName}'.");

            return new OkObjectResult(new BaseResponseObject<ObjectMetadataOutput> { Status = "ok", Result = updatedMetadata.ToDto() });
        }, nameof(HandleSecretMetaUpdate), log);

        private async Task<IActionResult> HandleSecretMetaDeletion(HttpRequest request, string secretId, ClaimsPrincipal principal, ILogger log)
            => await TryCatch(async () =>
        {
            var userName = this.tokenHelper.GetUpn(principal);

            var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
            if (existingMetadata == null)
            {
                log.LogInformation($"Cannot delete secret '{secretId}', as no metadata exists.");
                return new NotFoundObjectResult(new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretId}' not exists." });
            }

            if (!existingMetadata.KeepInStorage)
            {
                log.LogInformation($"The data is not kept in storage, cannot use this api.");
                return new UnprocessableEntityObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Cannot use this endpoint for previous versions data." });
            }

            if (!(await this.permissionsManager.IsAuthorizedAsync(userName, secretId, PermissionType.Write)))
            {
                return ActionResults.InsufficientPermissionsResult(PermissionType.Write, secretId, string.Empty);
            }

            await this.purger.PurgeAsync(secretId, this.dbContext);
            log.LogInformation($"User '{userName}' deleted secret '{secretId}'");

            return new OkObjectResult(new BaseResponseObject<string> { Status = "ok", Result = "ok" });
        }, nameof(HandleSecretMetaDeletion), log);

        private async Task<ObjectMetadata> CreateMetadataAndPermissionsAsync(string secretId, MetadataCreationInput metadataInput, string userName, ILogger log)
        {
            var objectMetadata = new ObjectMetadata(secretId, metadataInput, userName);
            var entity = await this.dbContext.Objects.AddAsync(objectMetadata);
            
            await this.permissionsManager.SetPermissionAsync(userName, secretId, PermissionType.Full);
            
            await this.dbContext.SaveChangesAsync();

            return entity.Entity;
        }

        private async Task<ObjectMetadata> UpdateMetadataAsync(ObjectMetadata existingMetadata, MetadataUpdateInput metadataInput, ILogger log)
        {
            var updatedExpirationMetadata = new ExpirationMetadata(metadataInput.ExpirationSettings);
            existingMetadata.ExpirationMetadata = updatedExpirationMetadata;
            existingMetadata.LastAccessedAt = DateTimeProvider.UtcNow;

            await this.dbContext.SaveChangesAsync();

            return existingMetadata;
        }

        private static async Task<IActionResult> TryCatch(Func<Task<IActionResult>> action, string actionName, ILogger log)
        {
            try
            {
                return await action();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, $"Exception in {actionName}: {ex.GetType()}: {ex.Message}");
                return new ExceptionResult(ex, true);
            }
        }
    }
}
