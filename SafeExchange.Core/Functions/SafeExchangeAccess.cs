/// SafeExchange

namespace SpaceOyster.SafeExchange.Core
{
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;
    using System.Security.Claims;
    using System.IO;
    using Newtonsoft.Json;
    using System;
    using System.Collections.Generic;
    using SpaceOyster.SafeExchange.Core.CosmosDb;

    public class SafeExchangeAccess
    {
        private readonly IGraphClientProvider graphClientProvider;

        private readonly ICosmosDbProvider cosmosDbProvider;

        public SafeExchangeAccess(ICosmosDbProvider cosmosDbProvider, IGraphClientProvider graphClientProvider)
        {
            this.cosmosDbProvider = cosmosDbProvider ?? throw new ArgumentNullException(nameof(cosmosDbProvider));
            this.graphClientProvider = graphClientProvider ?? throw new ArgumentNullException(nameof(graphClientProvider));
        }

        public async Task<IActionResult> Run(
            HttpRequest req,
            string secretId, ClaimsPrincipal principal, ILogger log)
        {
            var subjectPermissions = await cosmosDbProvider.GetSubjectPermissionsContainerAsync();
            var groupDictionary = await cosmosDbProvider.GetSubjectPermissionsContainerAsync();

            var userName = TokenHelper.GetName(principal);
            log.LogInformation($"SafeExchange-Access triggered for '{secretId}' by {userName}, ID {TokenHelper.GetId(principal)} [{req.Method}].");

            if (!TokenHelper.IsUserToken(principal, log))
            {
                log.LogInformation($"{userName} is not authenticated with user access/id token.");
                return new ObjectResult(new { status = "unauthorized", error = $"Not authorized" }) { StatusCode = StatusCodes.Status401Unauthorized };
            }

            string subject = null;
            IList<PermissionType> permissionList = null;

            if (req.Method.ToLower().Equals("post") || req.Method.ToLower().Equals("delete"))
            {
                dynamic data = await GetRequestDataAsync(req);

                subject = data?.subject;
                if (string.IsNullOrEmpty(subject))
                {
                    log.LogInformation($"{nameof(subject)} is not set.");
                    return new BadRequestObjectResult(new { status = "error", error = $"{nameof(subject)} is required" });
                }

                string permission = data?.permission;
                if (!TryParsePermissions(permission, out permissionList))
                {
                    log.LogInformation($"Cannot parse {nameof(permission)}.");
                    return new BadRequestObjectResult(new { status = "error", error = $"Valid value of {nameof(permission)} is required" });
                }
            }

            var permissionsHelper = new PermissionsHelper(subjectPermissions, groupDictionary, this.graphClientProvider);
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

        private static async Task<dynamic> GetRequestDataAsync(HttpRequest req)
        {
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);
            return data;
        }

        private static bool TryParsePermissions(string permissions, out IList<PermissionType> permissionList)
        {
            var chunks = permissions.Split(',', StringSplitOptions.RemoveEmptyEntries);
            permissionList = new List<PermissionType>(chunks.Length);
            var permissionType = PermissionType.Read;
            foreach (var chunk in chunks)
            {
                if (!Enum.TryParse(chunk, ignoreCase: true, out permissionType))
                {
                    return false;
                }
                permissionList.Add(permissionType);
            }
            return true;
        }
    }
}
