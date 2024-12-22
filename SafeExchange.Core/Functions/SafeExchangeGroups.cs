
namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using System;
    using System.Net;
    using System.Security.Claims;

    public class SafeExchangeGroups
    {
        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        public SafeExchangeGroups(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters)
        {
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public async Task<HttpResponseData> Run(
            HttpRequestData request,
            string groupId,
            ClaimsPrincipal principal,
            ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            (SubjectType subjectType, string subjectId) = await SubjectHelper.GetSubjectInfoAsync(this.tokenHelper, principal, this.dbContext);
            if (SubjectType.Application.Equals(subjectType) && string.IsNullOrEmpty(subjectId))
            {
                await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Forbidden,
                    new BaseResponseObject<object> { Status = "forbidden", Error = "Application is not registered or disabled." });
            }

            log.LogInformation($"{nameof(SafeExchangeGroups)} triggered by {subjectType} {subjectId}, [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "get":
                    return await this.HandleGroupRead(request, groupId, subjectType, subjectId, log);

                case "post":
                    return await this.HandleGroupRegistration(request, groupId, subjectType, subjectId, log);

                case "delete":
                    return await this.HandleGroupDeletion(request, groupId, subjectType, subjectId, log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = $"Request method '{request.Method}' not allowed." });
            }
        }

        private async Task<HttpResponseData> HandleGroupRead(HttpRequestData request, string groupId, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () =>
            {
                var existingGroup = await this.dbContext.GroupDictionary.FirstOrDefaultAsync(g => g.GroupId.Equals(groupId));

                if (existingGroup == default)
                {
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.NoContent,
                        new BaseResponseObject<string> { Status = "no_content", Result = $"Group registration '{groupId}' does not exist." });
                }

                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK,
                    new BaseResponseObject<GroupOverviewOutput>
                    {
                        Status = "ok",
                        Result = existingGroup.ToOverviewDto()
                    });

            }, nameof(HandleGroupRead), log);

        private async Task<HttpResponseData> HandleGroupRegistration(HttpRequestData request, string groupId, SubjectType subjectType, string subjectId, ILogger log)
        {
            throw new NotImplementedException();
        }

        private async Task<HttpResponseData> HandleGroupDeletion(HttpRequestData request, string groupId, SubjectType subjectType, string subjectId, ILogger log)
        {
            throw new NotImplementedException();
        }

        private static async Task<HttpResponseData> TryCatch(HttpRequestData request, Func<Task<HttpResponseData>> action, string actionName, ILogger log)
        {
            try
            {
                return await action();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, $"Exception in {actionName}: {ex.GetType()}: {ex.Message}");

                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.InternalServerError,
                    new BaseResponseObject<object> { Status = "error", SubStatus = "internal_exception", Error = $"{ex.GetType()}: {ex.Message ?? "Unknown exception."}" });
            }
        }
    }
}
