/// <summary>
/// SafeExchange
/// </summary>

namespace SpaceOyster.SafeExchange.Core
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;
    using SpaceOyster.SafeExchange.Core.CosmosDb;
    using System;
    using System.Linq;
    using System.Security.Claims;
    using System.Threading.Tasks;
    using System.Web.Http;

    public class SafeExchangeAccessRequest
    {
        private readonly IGraphClientProvider graphClientProvider;

        private readonly ICosmosDbProvider cosmosDbProvider;

        private readonly ConfigurationSettings configuration;

        public SafeExchangeAccessRequest(ICosmosDbProvider cosmosDbProvider, IGraphClientProvider graphClientProvider, ConfigurationSettings configuration)
        {
            this.cosmosDbProvider = cosmosDbProvider ?? throw new ArgumentNullException(nameof(cosmosDbProvider));
            this.graphClientProvider = graphClientProvider ?? throw new ArgumentNullException(nameof(graphClientProvider));
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public async Task<IActionResult> Run(HttpRequest req, ClaimsPrincipal principal, GlobalFilters globalFilters, ILogger log)
        {
            var (shouldReturn, filterResult) = await globalFilters.GetFilterResultAsync(req, principal, log);
            if (shouldReturn)
            {
                return filterResult;
            }

            dynamic data = await RequestHelper.GetRequestDataAsync(req);
            string secretId = data?.secretId;
            if (string.IsNullOrEmpty(secretId))
            {
                log.LogInformation($"{nameof(secretId)} is not set.");
                return new BadRequestObjectResult(new { status = "error", error = $"{nameof(secretId)} is required" });
            }

            if (KeyVaultSystemSettings.IsSystemSettingName(secretId))
            {
                log.LogInformation($"Cannot request access to secret '{secretId}', as not allowed for system reserved names");
                return new NotFoundObjectResult(new { status = "not_found", error = $"Secret '{secretId}' not exists" });
            }

            var keyVaultHelper = new KeyVaultHelper(Environment.GetEnvironmentVariable("STORAGE_KEYVAULT_BASEURI"), log);
            var existingSecretVersions = await keyVaultHelper.GetSecretVersionsAsync(secretId);
            if (!existingSecretVersions.Any() && !req.Method.ToLower().Equals("delete"))
            {
                log.LogInformation($"Cannot request access to secret '{secretId}', as not exists");
                return new NotFoundObjectResult(new { status = "not_found", error = $"Secret '{secretId}' not exists" });
            }

            var accessRequests = await cosmosDbProvider.GetAccessRequestsContainerAsync();
            var subjectPermissions = await cosmosDbProvider.GetSubjectPermissionsContainerAsync();
            var groupDictionary = await cosmosDbProvider.GetGroupDictionaryContainerAsync();
            var notificationSubscriptions = await cosmosDbProvider.GetNotificationSubscriptionsContainerAsync();

            var permissionsHelper = new PermissionsHelper(this.configuration, subjectPermissions, groupDictionary, this.graphClientProvider);
            var notificationsHelper = new NotificationsHelper(notificationSubscriptions, log);
            var accessRequestHelper = new AccessRequestHelper(accessRequests, permissionsHelper, notificationsHelper, this.configuration, log);

            var userName = TokenHelper.GetName(principal);
            switch (req.Method.ToLower())
            {
                case "post":
                    return await HandleAccessRequestCreation(data, userName, secretId, accessRequestHelper, log);

                case "patch":
                    return await HandleAccessRequestUpdate(data, userName, secretId, accessRequestHelper, log);

                case "delete":
                    return await HandleAccessRequestDeletion(data, userName, secretId, accessRequestHelper, log);

                default:
                    return new BadRequestObjectResult(new { status = "error", error = "Request method not recognized" });
            }
        }

        private static async Task<IActionResult> HandleAccessRequestCreation(dynamic requestData, string userId, string secretId, AccessRequestHelper accessRequestHelper, ILogger log)
        {
            string permission = requestData?.permission;
            if (!PermissionsHelper.TryParsePermissions(permission, out var permissionList))
            {
                log.LogInformation($"Cannot parse {nameof(permission)}.");
                return new BadRequestObjectResult(new { status = "error", error = $"Valid value of {nameof(permission)} is required" });
            }

            return await TryCatch(async () =>
            {
                await accessRequestHelper.RequestAccessAsync(userId, secretId, permissionList);
                return new OkObjectResult(new { status = "ok" });
            }, "Request-Access", log);
        }

        private static async Task<IActionResult> HandleAccessRequestUpdate(dynamic requestData, string userId, string secretId, AccessRequestHelper accessRequestHelper, ILogger log)
        {
            string requestId = requestData?.requestId;
            if (string.IsNullOrEmpty(requestId))
            {
                log.LogInformation($"'{nameof(requestId)}' is not set.");
                return new BadRequestObjectResult(new { status = "error", error = $"{nameof(requestId)} is required" });
            }

            bool? grant = requestData?.grant;
            if (grant == null)
            {
                log.LogInformation($"'{nameof(grant)}' is not set.");
                return new BadRequestObjectResult(new { status = "error", error = $"{nameof(grant)} is required" });
            }

            return await TryCatch(async () =>
            {
                if (grant == true)
                {
                    await accessRequestHelper.ApproveAccessRequestAsync(userId, requestId, secretId);
                }
                else
                {
                    await accessRequestHelper.DenyAccessRequestAsync(userId, requestId, secretId);
                }
                return new OkObjectResult(new { status = "ok" });
            }, "Request-Access", log);
        }

        private static async Task<IActionResult> HandleAccessRequestDeletion(dynamic requestData, string userId, string secretId, AccessRequestHelper accessRequestHelper, ILogger log)
        {
            string requestId = requestData?.requestId;
            if (string.IsNullOrEmpty(requestId))
            {
                log.LogInformation($"'{nameof(requestId)}' is not set.");
                return new BadRequestObjectResult(new { status = "error", error = $"{nameof(requestId)} is required" });
            }

            return await TryCatch(async () =>
            {
                return await accessRequestHelper.DeleteAccessRequestAsync(userId, requestId, secretId);
            }, "Request-Access", log);
        }

        private static async Task<IActionResult> TryCatch(Func<Task<IActionResult>> action, string actionName, ILogger log)
        {
            try
            {
                return await action();
            }
            catch (Exception ex)
            {
                log.LogError(ex, $"{actionName} had exception {ex.GetType()}: {ex.Message}");
                return new ExceptionResult(ex, true);
            }
        }
    }
}
