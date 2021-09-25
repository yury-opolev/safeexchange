
namespace SpaceOyster.SafeExchange.Core
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;
    using System;
    using System.Security.Claims;
    using System.Threading.Tasks;
    using System.Web.Http;

    public class SafeExchangeAdministratorStatus
    {
        private readonly GlobalFilters globalFilters;

        public SafeExchangeAdministratorStatus(GlobalFilters globalFilters)
        {
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
        }

        public async Task<IActionResult> Run(HttpRequest req, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetFilterResultAsync(req, principal, log);
            if (shouldReturn)
            {
                return filterResult;
            }

            var userName = TokenHelper.GetName(principal);
            log.LogInformation($"SafeExchange-AdministratorStatus triggered by {userName}, ID {TokenHelper.GetId(principal)} [{req.Method}].");

            switch (req.Method.ToLower())
            {
                case "get":
                    return await HandleGetAdministratorStatus(req, principal, log);

                default:
                    return new BadRequestObjectResult(new { status = "error", error = "Request method not recognized" });
            }
        }
         
        private async Task<IActionResult> HandleGetAdministratorStatus(HttpRequest req, ClaimsPrincipal principal, ILogger log)
        {
            return await TryCatch(async () =>
            {
                var (shouldReturn, filterResult) = await this.globalFilters.GetAdminFilterResultAsync(req, principal, log);
                return new OkObjectResult(new
                {
                    status = "ok",
                    result = new { status = !shouldReturn }
                });
            }, "Get-AdministratorStatus", log);
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
