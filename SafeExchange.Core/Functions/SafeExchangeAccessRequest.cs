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

        public SafeExchangeAccessRequest(ICosmosDbProvider cosmosDbProvider, IGraphClientProvider graphClientProvider)
        {
            this.cosmosDbProvider = cosmosDbProvider ?? throw new ArgumentNullException(nameof(cosmosDbProvider));
            this.graphClientProvider = graphClientProvider ?? throw new ArgumentNullException(nameof(graphClientProvider));
        }

        public async Task<IActionResult> Run(HttpRequest req, string secretId, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await GlobalFilters.Instance.Value.GetFilterResultAsync(req, principal, log);
            if (shouldReturn)
            {
                return filterResult;
            }

            if (KeyVaultSystemSettings.IsSystemSettingName(secretId))
            {
                log.LogInformation($"Cannot request access to secret '{secretId}', as not allowed for system reserved names");
                return new NotFoundObjectResult(new { status = "not_found", error = $"Secret '{secretId}' not exists" });
            }

            var keyVaultHelper = new KeyVaultHelper(Environment.GetEnvironmentVariable("STORAGE_KEYVAULT_BASEURI"), log);
            var existingSecretVersions = await keyVaultHelper.GetSecretVersionsAsync(secretId);
            if (!existingSecretVersions.Any())
            {
                log.LogInformation($"Cannot request access to secret '{secretId}', as not exists");
                return new NotFoundObjectResult(new { status = "not_found", error = $"Secret '{secretId}' not exists" });
            }

            var accessRequests = await cosmosDbProvider.GetAccessRequestsContainerAsync();
            var subjectPermissions = await cosmosDbProvider.GetSubjectPermissionsContainerAsync();
            var groupDictionary = await cosmosDbProvider.GetGroupDictionaryContainerAsync();
            var notificationSubscriptions = await cosmosDbProvider.GetNotificationSubscriptionsContainerAsync();

            var permissionsHelper = new PermissionsHelper(subjectPermissions, groupDictionary, this.graphClientProvider);
            var notificationsHelper = new NotificationsHelper(notificationSubscriptions, log);
            var accessRequestHelper = new AccessRequestHelper(accessRequests, permissionsHelper, notificationsHelper, log);

            var userName = TokenHelper.GetName(principal);
            return await TryCatch(async () =>
            {
                await accessRequestHelper.RequestAccessAsync(userName, secretId, null);
                return new OkObjectResult(new { status = "ok" });
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
                log.LogError($"{actionName} had exception {ex.GetType()}: {ex.Message}");
                return new ExceptionResult(ex, true);
            }
        }
    }
}
