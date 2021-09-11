/// SafeExchange

namespace SpaceOyster.SafeExchange.Core
{
    using System;
    using System.Collections.Generic;
    using System.Security.Claims;
    using System.Threading.Tasks;
    using System.Web.Http;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;
    using SpaceOyster.SafeExchange.Core.CosmosDb;

    public class SafeExchangeListSecrets
    {
        private readonly ICosmosDbProvider cosmosDbProvider;

        private readonly ConfigurationSettings configuration;

        public SafeExchangeListSecrets(ICosmosDbProvider cosmosDbProvider, IGraphClientProvider graphClientProvider, ConfigurationSettings configuration)
        {
            this.cosmosDbProvider = cosmosDbProvider ?? throw new ArgumentNullException(nameof(cosmosDbProvider));
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public async Task<IActionResult> Run(
            HttpRequest req,
            ClaimsPrincipal principal, GlobalFilters globalFilters, ILogger log)
        {
            var (shouldReturn, filterResult) = await globalFilters.GetFilterResultAsync(req, principal, log);
            if (shouldReturn)
            {
                return filterResult;
            }

            var subjectPermissions = await cosmosDbProvider.GetSubjectPermissionsContainerAsync();

            var userName = TokenHelper.GetName(principal);
            log.LogInformation($"SafeExchange-ListSecrets triggered by {userName}, ID {TokenHelper.GetId(principal)} [{req.Method}].");

            var permissionsHelper = new PermissionsHelper(this.configuration, subjectPermissions, null, null);
            return await HandleReadSecretsList(userName, permissionsHelper, log);
        }

        private static async Task<IActionResult> HandleReadSecretsList(string subjectName, PermissionsHelper permissionsHelper, ILogger log)
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
                log.LogWarning(ex, $"{actionName} had exception {ex.GetType()}: {ex.Message}");
                return new ExceptionResult(ex, true);
            }
        }
    }
}