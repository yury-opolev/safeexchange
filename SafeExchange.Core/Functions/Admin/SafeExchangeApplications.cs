
namespace SafeExchange.Core.Functions.Admin
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Crypto;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Model;
    using System;
    using System.Security.Claims;
    using System.Web.Http;

    public class SafeExchangeApplications
    {
        private static string DefaultGuidRegex = "^([0-9A-Fa-f]{8}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{12})$";

        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly ICryptoHelper cryptoHelper;

        private readonly GlobalFilters globalFilters;

        public SafeExchangeApplications(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, ICryptoHelper cryptoHelper, GlobalFilters globalFilters)
        {
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.cryptoHelper = cryptoHelper ?? throw new ArgumentNullException(nameof(cryptoHelper));
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public async Task<IActionResult> Run(
            HttpRequest req,
            string applicationId,
            ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetFilterResultAsync(req, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResult ?? new EmptyResult();
            }

            (SubjectType subjectType, string subjectId) = SubjectHelper.GetSubjectInfo(this.tokenHelper, principal);
            log.LogInformation($"{nameof(SafeExchangeApplications)} triggered for '{applicationId}' by {subjectType} {subjectId} [{req.Method}].");

            switch (req.Method.ToLower())
            {
                case "post":
                    return await this.HandleApplicationRegistration(req, subjectType, subjectId, log);

                case "get":
                    return await this.HandleApplicationRead(applicationId, subjectType, subjectId, log);

                case "patch":
                    return await this.HandleApplicationModification(req, applicationId, subjectType, subjectId, log);

                case "delete":
                    return await this.HandleApplicationDeletion(applicationId, subjectType, subjectId, log);

                default:
                    return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized." });
            }
        }

        public async Task<IActionResult> RunList(
            HttpRequest req, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetFilterResultAsync(req, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResult ?? new EmptyResult();
            }

            (SubjectType subjectType, string subjectId) = SubjectHelper.GetSubjectInfo(this.tokenHelper, principal);
            log.LogInformation($"{nameof(SafeExchangeSecretMeta)}-{nameof(RunList)} triggered by {subjectType} {subjectId}, [{req.Method}].");

            switch (req.Method.ToLower())
            {
                case "get":
                    return await this.HandleListApplications(subjectType, subjectId, log);

                default:
                    return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized." });
            }
        }

        private async Task<IActionResult> HandleApplicationRegistration(HttpRequest req, SubjectType subjectType, string subjectId, ILogger log)
        {
            throw new NotImplementedException();
        }

        private async Task<IActionResult> HandleApplicationRead(string applicationId, SubjectType subjectType, string subjectId, ILogger log)
        {
            throw new NotImplementedException();
        }

        private async Task<IActionResult> HandleApplicationModification(HttpRequest req, string applicationId, SubjectType subjectType, string subjectId, ILogger log)
        {
            throw new NotImplementedException();
        }

        private async Task<IActionResult> HandleApplicationDeletion(string applicationId, SubjectType subjectType, string subjectId, ILogger log)
        {
            throw new NotImplementedException();
        }

        private async Task<IActionResult> HandleListApplications(SubjectType subjectType, string subjectId, ILogger log)
        {
            throw new NotImplementedException();
        }

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
