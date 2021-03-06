/// SafeExchange

namespace SpaceOyster.SafeExchange.Core
{
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json;
    using System.Security.Claims;
    using System.Linq;
    using System;
    using System.Web.Http;
    using System.Collections.Generic;
    using SpaceOyster.SafeExchange.Core.CosmosDb;

    public class SafeExchangeSecret
    {
        private readonly IGraphClientProvider graphClientProvider;

        private readonly ICosmosDbProvider cosmosDbProvider;

        public SafeExchangeSecret(ICosmosDbProvider cosmosDbProvider, IGraphClientProvider graphClientProvider)
        {
            this.cosmosDbProvider = cosmosDbProvider ?? throw new ArgumentNullException(nameof(cosmosDbProvider));
            this.graphClientProvider = graphClientProvider ?? throw new ArgumentNullException(nameof(graphClientProvider));
        }

        public async Task<IActionResult> Run(
            HttpRequest req,
            string secretId, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await GlobalFilters.Instance.Value.GetFilterResultAsync(req, principal, log);
            if (shouldReturn)
            {
                return filterResult;
            } 

            var subjectPermissions = await cosmosDbProvider.GetSubjectPermissionsContainerAsync();
            var objectMetadata = await cosmosDbProvider.GetObjectMetadataContainerAsync();
            var groupDictionary = await cosmosDbProvider.GetGroupDictionaryContainerAsync();
            var accessRequests = await cosmosDbProvider.GetAccessRequestsContainerAsync();

            var userName = TokenHelper.GetName(principal);
            log.LogInformation($"SafeExchange-Secret triggered for '{secretId}' by {userName}, ID {TokenHelper.GetId(principal)} [{req.Method}].");

            var metadataHelper = new MetadataHelper(objectMetadata);
            var permissionsHelper = new PermissionsHelper(subjectPermissions, groupDictionary, this.graphClientProvider);
            var keyVaultHelper = new KeyVaultHelper(Environment.GetEnvironmentVariable("STORAGE_KEYVAULT_BASEURI"), log);
            var accessRequestHelper = new AccessRequestHelper(accessRequests, permissionsHelper, null, log);

            var purgeHelper = new PurgeHelper(keyVaultHelper, permissionsHelper, metadataHelper, accessRequestHelper, log);
            await purgeHelper.TryPurgeAsync(secretId);

            TryGetReplyType(req, out ReplyDataType replyType);

            switch (req.Method.ToLower())
            {
                case "post":
                    return await HandleSecretCreation(req, secretId, principal, permissionsHelper, metadataHelper, keyVaultHelper, log);

                case "get":
                    return await HandleSecretRead(req, secretId, principal, permissionsHelper, metadataHelper, keyVaultHelper, purgeHelper, log, replyType);

                case "patch":
                    return await HandleSecretUpdate(req, secretId, principal, permissionsHelper, metadataHelper, keyVaultHelper, log);

                case "delete":
                    return await HandleSecretDeletion(req, secretId, principal, permissionsHelper, metadataHelper, keyVaultHelper, purgeHelper, log);

                default:
                    return new BadRequestObjectResult(new { status = "error", error = "Request method not recognized" });
            }
        }

        private static async Task<IActionResult> HandleSecretCreation(HttpRequest req, string secretId, ClaimsPrincipal principal, PermissionsHelper permissionsHelper, MetadataHelper metadataHelper, KeyVaultHelper keyVaultHelper, ILogger log)
        {
            dynamic data = await RequestHelper.GetRequestDataAsync(req);

            string value = data?.value;
            if (string.IsNullOrEmpty(value))
            {
                log.LogInformation($"{nameof(value)} is not set.");
                return new BadRequestObjectResult(new { status = "error", error = $"{nameof(value)} is required" });
            }

            if (KeyVaultSystemSettings.IsSystemSettingName(secretId))
            {
                log.LogInformation($"Cannot create secret '{secretId}', as not allowed to create secrets with system reserved names");
                return new ConflictObjectResult(new { status = "conflict", error = $"Secret '{secretId}' not allowed" });
            }

            string contentType = data?.contentType;

            var now = DateTime.UtcNow;

            bool setDestroyValues = false;
            bool destroyAfterRead = false;
            bool scheduleDestroy = false;
            DateTime destroyAt = now;

            dynamic destroySettings = data?.destroySettings;
            if (destroySettings != null)
            {
                setDestroyValues = true;
                destroyAfterRead = ((bool?)destroySettings.destroyAfterRead) ?? false;
                scheduleDestroy = ((bool?)destroySettings.scheduleDestroy) ?? false;
                destroyAt = scheduleDestroy ? ((DateTime?)destroySettings.destroyAt) ?? now : now;
            }

            return await TryCatch(async () =>
            {
                var existingSecretVersions = await keyVaultHelper.GetSecretVersionsAsync(secretId);
                if (existingSecretVersions.Any())
                {
                    log.LogInformation($"Cannot create secret '{secretId}', as already exists");
                    return new ConflictObjectResult(new { status = "conflict", error = $"Secret '{secretId}' already exists" });
                }

                var existingDeletedSecret = await keyVaultHelper.TryGetDeletedSecretAsync(secretId);
                if (existingDeletedSecret != null)
                {
                    log.LogInformation($"Purging deleted secret '{secretId}'");
                    await keyVaultHelper.TryPurgeSecretAsync(secretId);
                }

                var userName = TokenHelper.GetName(principal);
                var tags = new Dictionary<string, string> { { "createdBy", userName } };
                await keyVaultHelper.SetSecretAsync(secretId, value, contentType, tags);
                log.LogInformation($"User '{userName}' created secret '{secretId}'");

                await metadataHelper.SetSecretMetadataAsync(secretId, userName, setDestroyValues, destroyAfterRead, scheduleDestroy, destroyAt);
                log.LogInformation($"Metadata for secret '{secretId}' created");

                await permissionsHelper.SetPermissionAsync(userName, secretId, PermissionType.Read);
                await permissionsHelper.SetPermissionAsync(userName, secretId, PermissionType.Write);
                await permissionsHelper.SetPermissionAsync(userName, secretId, PermissionType.GrantAccess);
                await permissionsHelper.SetPermissionAsync(userName, secretId, PermissionType.RevokeAccess);
                log.LogInformation($"Permissions for user '{userName}' to access secret '{secretId}' created");

                return new OkObjectResult(new { status = "ok" });
            }, "Create-Secret", log);
        }

        private static async Task<IActionResult> HandleSecretRead(HttpRequest req, string secretId, ClaimsPrincipal principal, PermissionsHelper permissionsHelper, MetadataHelper metadataHelper, KeyVaultHelper keyVaultHelper, PurgeHelper purgeHelper, ILogger log, ReplyDataType replyType)
        {
            if (KeyVaultSystemSettings.IsSystemSettingName(secretId))
            {
                log.LogInformation($"Cannot get secret '{secretId}', as not allowed to get secrets for system reserved names");
                return new NotFoundObjectResult(new { status = "not_found", error = $"Secret '{secretId}' not exists" });
            }

            var userName = TokenHelper.GetName(principal);
            return await TryCatch(async () =>
            {
                var existingSecretVersions = await keyVaultHelper.GetSecretVersionsAsync(secretId);
                if (!existingSecretVersions.Any())
                {
                    log.LogInformation($"Cannot get secret '{secretId}', as not exists");
                    return new NotFoundObjectResult(new { status = "not_found", error = $"Secret '{secretId}' not exists" });
                }

                var tokenResult = TokenHelper.GetTokenResult(req, principal, log);
                if (!(await permissionsHelper.IsAuthorizedAsync(userName, secretId, PermissionType.Read, tokenResult, log)))
                {
                    return PermissionsHelper.InsufficientPermissionsResult(PermissionType.Read, secretId);
                }

                var secretBundle = await keyVaultHelper.GetSecretAsync(secretId);
                log.LogInformation($"User '{userName}' read secret '{secretId}'");

                var metadata = await metadataHelper.GetSecretMetadataAsync(secretId);
                if (metadata?.DestroyAfterRead == true)
                {
                    await purgeHelper.DestroyAsync(secretId);
                }

                var resultData = new
                {
                    secret = secretBundle.Value,
                    contentType = secretBundle.ContentType,
                    destroySettings = new
                    {
                        destroyAfterRead = metadata.DestroyAfterRead,
                        scheduleDestroy = metadata.ScheduleDestroy,
                        destroyAt = metadata.DestroyAt
                    }
                };

                switch (replyType)
                {
                    case ReplyDataType.Html:
                    {
                        var data = ResourcesHelper.ReadEmbeddedResource(ResourcesHelper.ObjectValueHtmlTemplateName);
                        data = data.Replace("{SECRETNAME}", secretId);
                        data = data.Replace("{DATAOBJECT}", JsonConvert.SerializeObject(resultData));
                        return new ContentResult() { Content = data, ContentType = "text/html" };
                    }

                    default:
                        return new OkObjectResult(new { status = "ok", result = resultData });
                }
            }, "Get-Secret", log);
        }

        private static async Task<IActionResult> HandleSecretUpdate(HttpRequest req, string secretId, ClaimsPrincipal principal, PermissionsHelper permissionsHelper, MetadataHelper metadataHelper, KeyVaultHelper keyVaultHelper, ILogger log)
        {
            if (KeyVaultSystemSettings.IsSystemSettingName(secretId))
            {
                log.LogInformation($"Cannot update secret '{secretId}', as not allowed to update secrets for system reserved names");
                return new NotFoundObjectResult(new { status = "not_found", error = $"Secret '{secretId}' not exists" });
            }

            dynamic data = await RequestHelper.GetRequestDataAsync(req);
            
            string value = data?.value;
            if (string.IsNullOrEmpty(value))
            {
                log.LogInformation($"{nameof(value)} is not set.");
                return new BadRequestObjectResult(new { status = "error", error = $"{nameof(value)} is required" });
            }

            string contentType = data?.contentType;

            var now = DateTime.UtcNow;

            bool setDestroyValues = false;
            bool destroyAfterRead = false;
            bool scheduleDestroy = false;
            DateTime destroyAt = now;

            dynamic destroySettings = data?.destroySettings;
            if (destroySettings != null)
            {
                setDestroyValues = true;
                destroyAfterRead = ((bool?)destroySettings.destroyAfterRead) ?? false;
                scheduleDestroy = ((bool?)destroySettings.scheduleDestroy) ?? false;
                destroyAt = scheduleDestroy ? ((DateTime?)destroySettings.destroyAt) ?? now : now;
            }

            var userName = TokenHelper.GetName(principal);
            return await TryCatch(async () =>
            {
                var existingSecretVersions = await keyVaultHelper.GetSecretVersionsAsync(secretId);
                if (!existingSecretVersions.Any())
                {
                    log.LogInformation($"Cannot update secret '{secretId}', as not exists");
                    return new NotFoundObjectResult(new { status = "not_found", error = $"Secret '{secretId}' not exists" });
                }

                var tokenResult = TokenHelper.GetTokenResult(req, principal, log);
                if (!(await permissionsHelper.IsAuthorizedAsync(userName, secretId, PermissionType.Write, tokenResult, log)))
                {
                    return PermissionsHelper.InsufficientPermissionsResult(PermissionType.Write, secretId);
                }

                var tags = new Dictionary<string, string> { { "modifiedBy", userName } };
                var secretBundle = await keyVaultHelper.SetSecretAsync(secretId, value, contentType, tags);
                log.LogInformation($"User '{userName}' updated secret '{secretId}'");

                await metadataHelper.SetSecretMetadataAsync(secretId, userName, setDestroyValues, destroyAfterRead, scheduleDestroy, destroyAt);

                return new OkObjectResult(new { status = "ok" });
            }, "Update-Secret", log);
        }

        private static async Task<IActionResult> HandleSecretDeletion(HttpRequest req, string secretId, ClaimsPrincipal principal, PermissionsHelper permissionsHelper, MetadataHelper metadataHelper, KeyVaultHelper keyVaultHelper, PurgeHelper purgeHelper, ILogger log)
        {
            if (KeyVaultSystemSettings.IsSystemSettingName(secretId))
            {
                log.LogInformation($"Cannot delete secret '{secretId}', as not allowed to delete secrets for system reserved names");
                return new NotFoundObjectResult(new { status = "not_found", error = $"Secret '{secretId}' not exists" });
            }

            var userName = TokenHelper.GetName(principal);
            return await TryCatch(async () =>
            {
                var existingSecretVersions = await keyVaultHelper.GetSecretVersionsAsync(secretId);
                if (!existingSecretVersions.Any())
                {
                    log.LogInformation($"Cannot delete secret '{secretId}', as not exists");
                    return new NotFoundObjectResult(new { status = "not_found", error = $"Secret '{secretId}' not exists" });
                }

                var tokenResult = TokenHelper.GetTokenResult(req, principal, log);
                if (!(await permissionsHelper.IsAuthorizedAsync(userName, secretId, PermissionType.Write, tokenResult, log)))
                {
                    return PermissionsHelper.InsufficientPermissionsResult(PermissionType.Write, secretId);
                }

                await purgeHelper.DestroyAsync(secretId);
                log.LogInformation($"User '{userName}' deleted secret '{secretId}'");

                return new OkObjectResult(new { status = "ok" });
            }, "Delete-Secret", log);
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

        private static bool TryGetReplyType(HttpRequest req, out ReplyDataType replyType)
        {
            string replyTypeString = req.Query["replytype"].FirstOrDefault() ?? "Json";
            if (!Enum.TryParse(replyTypeString, true, out replyType))
            {
                replyType = ReplyDataType.Json;
                return false;
            }
            return true;
        }
    }
}
