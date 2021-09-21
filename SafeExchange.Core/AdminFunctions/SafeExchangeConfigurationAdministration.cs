﻿
namespace SpaceOyster.SafeExchange.Core
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;
    using System;
    using System.Security.Claims;
    using System.Threading.Tasks;
    using System.Web.Http;

    public class SafeExchangeConfigurationAdministration
    {
        private readonly IGraphClientProvider graphClientProvider;

        private readonly ConfigurationSettings configuration;

        private readonly GlobalFilters globalFilters;

        public SafeExchangeConfigurationAdministration(IGraphClientProvider graphClientProvider, ConfigurationSettings configuration, GlobalFilters globalFilters)
        {
            this.graphClientProvider = graphClientProvider ?? throw new ArgumentNullException(nameof(graphClientProvider));
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
        }

        public async Task<IActionResult> Run(HttpRequest req, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetAdminFilterResultAsync(req, principal, log);
            if (shouldReturn)
            {
                return filterResult;
            }

            var userName = TokenHelper.GetName(principal);
            log.LogInformation($"SafeExchange-ConfigurationAdministration triggered by {userName}, ID {TokenHelper.GetId(principal)} [{req.Method}].");

            switch (req.Method.ToLower())
            {
                case "get":
                    return await HandleConfigurationGet(req, log);

                case "put":
                    return await HandleConfigurationSet(req, log);

                default:
                    return new BadRequestObjectResult(new { status = "error", error = "Request method not recognized" });
            }
        }

        private async Task<IActionResult> HandleConfigurationSet(HttpRequest req, ILogger log)
        {
            return await TryCatch(async () =>
            {
                var configurationData = await this.configuration.GetDataAsync();
                dynamic requestData = await RequestHelper.GetRequestDataAsync(req);

                dynamic features = requestData?.features;
                if (features != null)
                {
                    configurationData.Features.UseNotifications = (bool?)features.useNotifications ?? configurationData.Features.UseNotifications;
                    configurationData.Features.UseGroupsAuthorization = (bool?)features.useGroupsAuthorization ?? configurationData.Features.UseGroupsAuthorization;
                }

                configurationData.WhitelistedGroups = (string)requestData?.whitelistedGroups ?? configurationData.WhitelistedGroups;
                
                dynamic cosmosDb = requestData?.cosmosDb;
                if (cosmosDb != null)
                {
                    configurationData.CosmosDb.SubscriptionId = (string)cosmosDb.subscriptionId ?? configurationData.CosmosDb.SubscriptionId;
                    configurationData.CosmosDb.ResourceGroupName = (string)cosmosDb.resourceGroupName ?? configurationData.CosmosDb.ResourceGroupName;
                    configurationData.CosmosDb.AccountName = (string)cosmosDb.accountName ?? configurationData.CosmosDb.AccountName;
                    configurationData.CosmosDb.CosmosDbEndpoint = (string)cosmosDb.cosmosDbEndpoint ?? configurationData.CosmosDb.CosmosDbEndpoint;
                    configurationData.CosmosDb.DatabaseName = (string)cosmosDb.databaseName ?? configurationData.CosmosDb.DatabaseName;
                }

                configurationData.AdminGroups = (string?)requestData?.adminGroups ?? configurationData.AdminGroups;

                await this.configuration.PersistSettingsAsync();
                return new OkObjectResult(new { status = "ok" });
            }, "Set-Configuration", log);
        }

        private async Task<IActionResult> HandleConfigurationGet(HttpRequest req, ILogger log)
        {
            return await TryCatch(async () =>
            {
                var data = await this.configuration.GetDataAsync();
                return new OkObjectResult(new { status = "ok", result = data });
            }, "Get-Configuration", log);
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
