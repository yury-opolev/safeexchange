﻿/// <summary>
/// SafeExchange
/// </summary>

namespace SpaceOyster.SafeExchange.Core
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;
    using SpaceOyster.SafeExchange.Core.CosmosDb;
    using System;
    using System.Security.Claims;
    using System.Threading.Tasks;

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

            var accessRequests = await cosmosDbProvider.GetAccessRequestsContainerAsync();
            var subjectPermissions = await cosmosDbProvider.GetSubjectPermissionsContainerAsync();
            var groupDictionary = await cosmosDbProvider.GetGroupDictionaryContainerAsync();
            var notificationSubscriptions = await cosmosDbProvider.GetNotificationSubscriptionsContainerAsync();

            var permissionsHelper = new PermissionsHelper(subjectPermissions, groupDictionary, this.graphClientProvider);
            var notificationsHelper = new NotificationsHelper(notificationSubscriptions, log);
            var accessRequestHelper = new AccessRequestHelper(accessRequests, permissionsHelper, notificationsHelper, log);

            var userName = TokenHelper.GetName(principal);
            await accessRequestHelper.RequestAccessAsync(userName, secretId, null);

            return new OkObjectResult(new { status = "ok" });
        }
    }
}
