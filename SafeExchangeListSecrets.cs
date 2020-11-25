/// SafeExchange

namespace SpaceOyster.SafeExchange
{
    using System;
    using System.Collections.Generic;
    using System.Security.Claims;
    using System.Threading.Tasks;
    using System.Web.Http;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Azure.Cosmos.Table;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Azure.WebJobs.Extensions.Http;
    using Microsoft.Extensions.Logging;

    public static class SafeExchangeListSecrets
    {
        [FunctionName("SafeExchange-ListSecrets")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "secrets")]
            HttpRequest req,
            [Table("SubjectPermissions")]
            CloudTable subjectPermissionsTable,
            ClaimsPrincipal principal, ILogger log, ExecutionContext context)
        {
            var userName = TokenHelper.GetName(principal);
            log.LogInformation($"SafeExchange-ListSecrets triggered by {userName}, ID {TokenHelper.GetId(principal)} [{req.Method}].");

            if (TokenHelper.GetTokenType(principal, log) == TokenType.AccessToken && !TokenHelper.IsUserAccessToken(principal, log))
            {
                log.LogInformation($"{userName} is not authenticated with user access/id token.");
                return new ObjectResult(new { status = "unauthorized", error = $"Not authorized" }) { StatusCode = StatusCodes.Status401Unauthorized };
            }

            var permissionsHelper = new PermissionsHelper(subjectPermissionsTable);
            return await HandleReadSecretsList(userName, permissionsHelper, log, context);
        }

        private static async Task<IActionResult> HandleReadSecretsList(string subjectName, PermissionsHelper permissionsHelper, ILogger log, ExecutionContext context)
        {
            return await TryCatch(async () =>
            {
                var secretsList = await permissionsHelper.ListSecretsWithPermissionAsync(subjectName, PermissionType.Read);
                return new OkObjectResult(new { status = "ok", secrets = ConvertToListOfObjectDescriptions(secretsList) });
            }, "Read-SecretsList", log);
        }

        private static IList<OutputObjectDescription> ConvertToListOfObjectDescriptions(IList<SubjectPermissions> permissions)
        {
            var result = new List<OutputObjectDescription>(permissions.Count);
            foreach (var permission in permissions)
            {
                result.Add(new OutputObjectDescription(permission));
            }
            return result;
        }

        private static async Task<IActionResult> TryCatch(Func<Task<IActionResult>> action, string actionName, ILogger log)
        {
            try
            {
                return await action();
            }
            catch (Exception ex)
            {
                log.LogWarning($"{actionName} had exception {ex.GetType()}: {ex.Message}");
                return new ExceptionResult(ex, true);
            }
        }
    }
}