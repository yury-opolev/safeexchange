/// <summary>
/// ...
/// </summary>

namespace SpaceOyster.SafeExchange.Core
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;
    using SpaceOyster.SafeExchange.Core.CosmosDb;
    using System;
    using System.Collections.Generic;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class SafeExchangeListAccessRequests
    {
        private readonly IGraphClientProvider graphClientProvider;

        private readonly ICosmosDbProvider cosmosDbProvider;

        public SafeExchangeListAccessRequests(ICosmosDbProvider cosmosDbProvider, IGraphClientProvider graphClientProvider)
        {
            this.cosmosDbProvider = cosmosDbProvider ?? throw new ArgumentNullException(nameof(cosmosDbProvider));
            this.graphClientProvider = graphClientProvider ?? throw new ArgumentNullException(nameof(graphClientProvider));
        }

        public async Task<IActionResult> Run(HttpRequest req, ClaimsPrincipal principal, ILogger log)
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
            var existingAccessRequests = await accessRequestHelper.GetAccessRequestsToHandleAsync(userName);

            return new OkObjectResult(new { status = "ok", accessRequests = ConvertToOutputAccessRequests(existingAccessRequests) });
        }

        private static IList<OutputAccessRequest> ConvertToOutputAccessRequests(IList<AccessRequest> accessRequests)
        {
            var result = new List<OutputAccessRequest>(accessRequests.Count);
            foreach (var accessRequest in accessRequests)
            {
                result.Add(new OutputAccessRequest(accessRequest));
            }
            return result;
        }
    }
}
