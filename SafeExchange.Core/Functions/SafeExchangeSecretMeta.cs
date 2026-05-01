/// <summary>
/// SafeExchangeSecretMeta
/// </summary>

namespace SafeExchange.Core.Functions
{
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using System.Security.Claims;
    using System;
    using System.IO;

    using Microsoft.Extensions.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Utilities;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Azure.Functions.Worker.Http;
    using System.Net;

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

        public async Task<HttpResponseData> Run(
            HttpRequestData request,
            string secretId, ClaimsPrincipal principal, ILogger log)
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

            log.LogInformation($"{nameof(SafeExchangeSecretMeta)} triggered for '{secretId}' by {subjectType} {subjectId} [{request.Method}].");

            await this.purger.PurgeIfNeededAsync(secretId, this.dbContext);
            
            switch (request.Method.ToLower())
            {
                case "post":
                    return await this.HandleSecretMetaCreation(request, secretId, subjectType, subjectId, log);

                case "get":
                    return await this.HandleSecretMetaRead(request, secretId, subjectType, subjectId, log);

                case "patch":
                    return await this.HandleSecretMetaUpdate(request, secretId, subjectType, subjectId, log);

                case "delete":
                    return await this.HandleSecretMetaDeletion(request, secretId, subjectType, subjectId, log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        public async Task<HttpResponseData> RunList(
            HttpRequestData request, ClaimsPrincipal principal, ILogger log)
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

            log.LogInformation($"{nameof(SafeExchangeSecretMeta)}-{nameof(RunList)} triggered by {subjectType} {subjectId}, ID {this.tokenHelper.GetObjectId(principal)} [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "get":
                    return await this.HandleListSecretMeta(request, subjectType, subjectId, log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        private async Task<HttpResponseData> HandleListSecretMeta(HttpRequestData request, SubjectType subjectType, string subjectId, ILogger log)
            => await ActionResults.TryCatchAsync(request, async () =>
        {
            var rawTags = Array.Empty<string>();
            var query = request.Url?.Query;
            if (!string.IsNullOrEmpty(query))
            {
                rawTags = System.Web.HttpUtility.ParseQueryString(query).GetValues("tag") ?? Array.Empty<string>();
            }

            var (tagsOk, requiredTags, tagsError) = TagValidator.TryNormalizeList(rawTags);
            if (!tagsOk)
            {
                log.LogInformation($"Invalid tag filter: {tagsError}.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "bad_request", Error = tagsError });
            }

            var permissions = await this.dbContext.Permissions
                .Where(p => p.SubjectType.Equals(subjectType) && p.SubjectId.Equals(subjectId) && p.CanRead)
                .ToListAsync();

            var names = permissions.Select(p => p.SecretName).Distinct().ToList();
            var tagMap = new Dictionary<string, List<string>>();
            if (names.Count > 0)
            {
                var tagPairs = await this.dbContext.Objects
                    .Where(o => names.Contains(o.ObjectName))
                    .Select(o => new { o.ObjectName, o.Tags })
                    .ToListAsync();

                foreach (var pair in tagPairs)
                {
                    tagMap[pair.ObjectName] = pair.Tags?.ToList() ?? new List<string>();
                }
            }

            if (requiredTags.Count > 0)
            {
                permissions = permissions
                    .Where(p => tagMap.TryGetValue(p.SecretName, out var t)
                                && requiredTags.All(rt => t.Contains(rt)))
                    .ToList();
            }

            var dtos = permissions.Select(p =>
            {
                var dto = p.ToDto();
                dto.Tags = tagMap.TryGetValue(p.SecretName, out var t) ? t.ToList() : new List<string>();
                return dto;
            }).ToList();

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<List<SubjectPermissionsOutput>>
                {
                    Status = "ok",
                    Result = dtos
                });

        }, nameof(HandleListSecretMeta), log);

        private async Task<HttpResponseData> HandleSecretMetaCreation(HttpRequestData request, string secretId, SubjectType subjectType, string subjectId, ILogger log)
            => await ActionResults.TryCatchAsync(request, async () =>
        {
            var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
            if (existingMetadata != null)
            {
                log.LogInformation($"Cannot create secret '{secretId}', as already exists");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Conflict,
                    new BaseResponseObject<object> { Status = "conflict", Error = $"Secret '{secretId}' already exists." });
            }

            var requestBody = await new StreamReader(request.Body ?? Stream.Null).ReadToEndAsync();
            MetadataCreationInput? metadataInput;
            try
            {
                metadataInput = DefaultJsonSerializer.Deserialize<MetadataCreationInput>(requestBody);
            }
            catch
            {
                log.LogInformation($"Could not parse input data for '{secretId}'.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "bad_request", Error = "Expiration settings are not provided." });
            }

            if ((metadataInput is null) || metadataInput.ExpirationSettings == null)
            {
                log.LogInformation($"Expiration settings for '{secretId}' not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "bad_request", Error = "Expiration settings are not provided." });
            }

            if (string.IsNullOrEmpty(secretId))
            {
                log.LogInformation("Secret id value is not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "bad_request", Error = "Secret id value is not provided." });
            }

            var (tagsOk, normalisedTags, tagsError) = TagValidator.TryNormalizeList(metadataInput.Tags);
            if (!tagsOk)
            {
                log.LogInformation($"Invalid tags for secret '{secretId}': {tagsError}.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "bad_request", Error = tagsError });
            }
            metadataInput.Tags = normalisedTags.ToList();

            var createdMetadata = await this.CreateMetadataAndPermissionsAsync(secretId, metadataInput, subjectType, subjectId, log);
            log.LogInformation($"Metadata for secret '{secretId}' added, full permissions for {subjectType} '{subjectId}' are set.");

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<ObjectMetadataOutput> { Status = "ok", Result = createdMetadata.ToDto() });
        }, nameof(HandleSecretMetaCreation), log);

        private async Task<HttpResponseData> HandleSecretMetaRead(HttpRequestData request, string secretId, SubjectType subjectType, string subjectId, ILogger log)
            => await ActionResults.TryCatchAsync(request, async () =>
        {
            var metadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
            if (metadata == null)
            {
                log.LogInformation($"Cannot get secret '{secretId}', as no metadata exists.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.NotFound,
                    new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretId}' does not exist." });
            }

            if (!metadata.KeepInStorage)
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

            metadata.LastAccessedAt = DateTimeProvider.UtcNow;
            await this.dbContext.SaveChangesAsync();

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<ObjectMetadataOutput> { Status = "ok", Result = metadata.ToDto() });
        }, nameof(HandleSecretMetaRead), log);

        private async Task<HttpResponseData> HandleSecretMetaUpdate(HttpRequestData request, string secretId, SubjectType subjectType, string subjectId, ILogger log)
            => await ActionResults.TryCatchAsync(request, async () =>
        {
            var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
            if (existingMetadata == null)
            {
                log.LogInformation($"Cannot update secret '{secretId}', as no metadata exists.");
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

            var requestBody = await new StreamReader(request.Body ?? Stream.Null).ReadToEndAsync();
            MetadataUpdateInput? metadataInput;
            try
            {
                metadataInput = DefaultJsonSerializer.Deserialize<MetadataUpdateInput>(requestBody);
            }
            catch
            {
                log.LogInformation($"Could not parse input data for '{secretId}'.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "bad_request", Error = "Expiration settings are not provided." });
            }

            if ((metadataInput is null) || metadataInput.ExpirationSettings == null)
            {
                log.LogInformation($"Expiration settings for '{secretId}' not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "bad_request", Error = "Expiration settings are not provided." });
            }

            if (string.IsNullOrEmpty(secretId))
            {
                log.LogInformation("Secret id value is not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "bad_request", Error = "Secret id value is not provided." });
            }

            if (metadataInput.Tags is not null)
            {
                var (tagsOk, normalisedTags, tagsError) = TagValidator.TryNormalizeList(metadataInput.Tags);
                if (!tagsOk)
                {
                    log.LogInformation($"Invalid tags for secret '{secretId}': {tagsError}.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "bad_request", Error = tagsError });
                }
                metadataInput.Tags = normalisedTags.ToList();
            }

            var updatedMetadata = await this.UpdateMetadataAsync(existingMetadata, metadataInput, log);
            log.LogInformation($"{subjectType} '{subjectId}' updated metadata for secret '{existingMetadata.ObjectName}'.");

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<ObjectMetadataOutput> { Status = "ok", Result = updatedMetadata.ToDto() });
        }, nameof(HandleSecretMetaUpdate), log);

        private async Task<HttpResponseData> HandleSecretMetaDeletion(HttpRequestData request, string secretId, SubjectType subjectType, string subjectId, ILogger log)
            => await ActionResults.TryCatchAsync(request, async () =>
        {
            var existingMetadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
            if (existingMetadata == null)
            {
                log.LogInformation($"Cannot delete secret '{secretId}', as no metadata exists.");
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

            await this.purger.PurgeAsync(secretId, this.dbContext);
            log.LogInformation($"{subjectType} '{subjectId}' deleted secret '{secretId}'");

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<string> { Status = "ok", Result = "ok" });
        }, nameof(HandleSecretMetaDeletion), log);

        private async Task<ObjectMetadata> CreateMetadataAndPermissionsAsync(string secretId, MetadataCreationInput metadataInput, SubjectType subjectType, string subjectId, ILogger log)
        {
            var objectMetadata = new ObjectMetadata(secretId, metadataInput, $"{subjectType} {subjectId}");
            var entity = await this.dbContext.Objects.AddAsync(objectMetadata);

            var subjectName = subjectId;
            await this.permissionsManager.SetPermissionAsync(subjectType, subjectId, subjectName, secretId, PermissionType.Full);
            
            await this.dbContext.SaveChangesAsync();

            return entity.Entity;
        }

        private async Task<ObjectMetadata> UpdateMetadataAsync(ObjectMetadata existingMetadata, MetadataUpdateInput metadataInput, ILogger log)
        {
            var updatedExpirationMetadata = new ExpirationMetadata(metadataInput.ExpirationSettings);
            existingMetadata.ExpirationMetadata = updatedExpirationMetadata;
            existingMetadata.LastAccessedAt = DateTimeProvider.UtcNow;

            if (metadataInput.Tags is not null)
            {
                existingMetadata.Tags = metadataInput.Tags.ToList();
            }

            await this.dbContext.SaveChangesAsync();

            return existingMetadata;
        }

    }
}
