/// <summary>
/// SafeExchangeAdminOperations
/// </summary>

namespace SafeExchange.Core.Functions.Admin
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Crypto;
    using SafeExchange.Core.Filters;
    using System;
    using System.Security.Claims;
    using System.Web.Http;

    public class SafeExchangeAdminOperations
    {
        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly ICryptoHelper cryptoHelper;

        private readonly GlobalFilters globalFilters;

        public SafeExchangeAdminOperations(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, ICryptoHelper cryptoHelper, GlobalFilters globalFilters)
        {
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.cryptoHelper = cryptoHelper ?? throw new ArgumentNullException(nameof(cryptoHelper));
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public async Task<IActionResult> Run(
            HttpRequest request,
            string operationName, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetAdminFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResult ?? new EmptyResult();
            }

            var userUpn = this.tokenHelper.GetUpn(principal);
            log.LogInformation($"{nameof(SafeExchangeAdminOperations)} triggered for operation '{operationName}' by '{userUpn}', ID {this.tokenHelper.GetObjectId(principal)} [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "post":
                    return await this.PerformOperationAsync(operationName, request, log);

                default:
                    return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        private async Task<IActionResult> PerformOperationAsync(string operationName, HttpRequest request, ILogger log)
            => await TryCatch(async () =>
        {
            switch (operationName)
            {
                case "ensure_dbcreated":
                    await this.dbContext.Database.EnsureCreatedAsync();
                    break;

                case "add_kek_version":
                    var cryptoConfiguration = this.cryptoHelper.CryptoConfiguration;
                    await this.cryptoHelper.CreateNewCryptoKeyVersionAsync(cryptoConfiguration.KeyName);
                    break;
            }

            return new OkObjectResult(new BaseResponseObject<string> { Status = "ok", Result = "ok" });

        }, nameof(PerformOperationAsync), log);

        private static async Task<IActionResult> TryCatch(Func<Task<IActionResult>> action, string actionName, ILogger log)
        {
            try
            {
                return await action();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, $"Exception in {actionName}: {ex.GetType()}: {ex.Message}");
                return new ExceptionResult(ex, true);
            }
        }
    }
}
