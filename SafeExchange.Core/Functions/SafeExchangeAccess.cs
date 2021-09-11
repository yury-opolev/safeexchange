/// SafeExchange

namespace SpaceOyster.SafeExchange.Core
{
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;
    using System.Security.Claims;
    using System;
    using System.Collections.Generic;
    using SpaceOyster.SafeExchange.Core.CosmosDb;

    public class SafeExchangeAccess
    {
        private readonly IGraphClientProvider graphClientProvider;

        private readonly ICosmosDbProvider cosmosDbProvider;

        private readonly ConfigurationSettings configuration;

        public SafeExchangeAccess(ICosmosDbProvider cosmosDbProvider, IGraphClientProvider graphClientProvider, ConfigurationSettings configuration)
        {
            this.cosmosDbProvider = cosmosDbProvider ?? throw new ArgumentNullException(nameof(cosmosDbProvider));
            this.graphClientProvider = graphClientProvider ?? throw new ArgumentNullException(nameof(graphClientProvider));
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public async Task<IActionResult> Run(
            HttpRequest req,
            string secretId, ClaimsPrincipal principal, GlobalFilters globalFilters, ILogger log)
        {
            var (shouldReturn, filterResult) = await globalFilters.GetFilterResultAsync(req, principal, log);
            if (shouldReturn)
            {
                return filterResult;
            }

            var subjectPermissions = await cosmosDbProvider.GetSubjectPermissionsContainerAsync();
            var groupDictionary = await cosmosDbProvider.GetGroupDictionaryContainerAsync();

            var userName = TokenHelper.GetName(principal);
            log.LogInformation($"SafeExchange-Access triggered for '{secretId}' by {userName}, ID {TokenHelper.GetId(principal)} [{req.Method}].");

            string subject = null;
            IList<PermissionType> permissionList = null;

            if (req.Method.ToLower().Equals("post") || req.Method.ToLower().Equals("delete"))
            {
                dynamic data = await RequestHelper.GetRequestDataAsync(req);

                subject = data?.subject;
                if (string.IsNullOrEmpty(subject))
                {
                    log.LogInformation($"{nameof(subject)} is not set.");
                    return new BadRequestObjectResult(new { status = "error", error = $"{nameof(subject)} is required" });
                }

                string permission = data?.permission;
                if (!PermissionsHelper.TryParsePermissions(permission, out permissionList))
                {
                    log.LogInformation($"Cannot parse {nameof(permission)}.");
                    return new BadRequestObjectResult(new { status = "error", error = $"Valid value of {nameof(permission)} is required" });
                }
            }

            var permissionsHelper = new PermissionsHelper(this.configuration, subjectPermissions, groupDictionary, this.graphClientProvider);
            var existingPermissions = await permissionsHelper.GetAllPermissionsAsync(secretId);
            if (existingPermissions.Count == 0)
            {
                log.LogInformation($"Cannot handle permissions for secret '{secretId}', as not exists");
                return new NotFoundObjectResult(new { status = "not_found", error = $"Secret '{secretId}' not exists" });
            }

            var tokenResult = TokenHelper.GetTokenResult(req, principal, log);
            switch (req.Method.ToLower())
            {
                case "post":
                {
                    if (!(await permissionsHelper.IsAuthorizedAsync(userName, secretId, PermissionType.GrantAccess, tokenResult, log)))
                    {
                        return PermissionsHelper.InsufficientPermissionsResult(PermissionType.GrantAccess, secretId);
                    }
                    return await GrantAccess(subject, permissionList, secretId, permissionsHelper, log);
                }

                case "get":
                {
                    if (!(await permissionsHelper.IsAuthorizedAsync(userName, secretId, PermissionType.Read, tokenResult, log)))
                    {
                        return PermissionsHelper.InsufficientPermissionsResult(PermissionType.Read, secretId);
                    }
                    return await GetAccessList(secretId, permissionsHelper, log);
                }

                case "delete":
                {
                    if (!(await permissionsHelper.IsAuthorizedAsync(userName, secretId, PermissionType.RevokeAccess, tokenResult, log)))
                    {
                        return PermissionsHelper.InsufficientPermissionsResult(PermissionType.RevokeAccess, secretId);
                    }
                    return await RevokeAccess(subject, permissionList, secretId, permissionsHelper, log);
                }

                default:
                    return new BadRequestObjectResult(new { status = "error", error = "Request method not recognized" });
            }
        }

        private static async Task<IActionResult> GrantAccess(string userName, IList<PermissionType> permissionList, string secretId, PermissionsHelper permissionsHelper, ILogger log)
        {
            foreach (var permission in permissionList)
            {
                await permissionsHelper.SetPermissionAsync(userName, secretId, permission);
            }
            return new OkObjectResult(new { status = "ok" });
        }

        private static async Task<IActionResult> GetAccessList(string secretId, PermissionsHelper permissionsHelper, ILogger log)
        {
            var existingPermissions = await permissionsHelper.GetAllPermissionsAsync(secretId);
            return new OkObjectResult(new { status = "ok", accessList = ConvertToOutputPermissions(existingPermissions) });
        }

        private static async Task<IActionResult> RevokeAccess(string userName, IList<PermissionType> permissionList, string secretId, PermissionsHelper permissionsHelper, ILogger log)
        {
            foreach (var permission in permissionList)
            {
                await permissionsHelper.DeletePermissionAsync(userName, secretId, permission);
            }
            return new OkObjectResult(new { status = "ok" });
        }

        private static IList<OutputSubjectPermissions> ConvertToOutputPermissions(IList<SubjectPermissions> permissions)
        {
            var result = new List<OutputSubjectPermissions>(permissions.Count);
            foreach (var permission in permissions)
            {
                result.Add(new OutputSubjectPermissions(permission));
            }
            return result;
        }
    }
}
