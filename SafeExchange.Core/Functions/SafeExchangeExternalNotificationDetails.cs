
namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Purger;
    using System;
    using System.Net;
    using System.Security.Claims;

    public class SafeExchangeExternalNotificationDetails
    {
        private readonly GeneralConfiguration generalConfiguration;

        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly IPurger purger;

        private readonly GlobalFilters globalFilters;

        public SafeExchangeExternalNotificationDetails(IConfiguration configuration, SafeExchangeDbContext dbContext, IPurger purger, ITokenHelper tokenHelper, GlobalFilters globalFilters)
        {
            this.generalConfiguration = new GeneralConfiguration();
            configuration.GetSection("GeneralConfiguration").Bind(generalConfiguration);

            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.purger = purger ?? throw new ArgumentNullException(nameof(purger));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public async Task<HttpResponseData> Run(
            string webhookNotificationDataId, HttpRequestData request, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            (SubjectType subjectType, string subjectId) = await SubjectHelper.GetSubjectInfoAsync(this.tokenHelper, principal, this.dbContext);
            if (!SubjectType.Application.Equals(subjectType))
            {
                await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Forbidden,
                    new BaseResponseObject<object> { Status = "forbidden", Error = "Only applications can use this API." });
            }

            if (string.IsNullOrEmpty(subjectId))
            {
                await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Forbidden,
                    new BaseResponseObject<object> { Status = "forbidden", Error = "Application is not registered or disabled." });
            }

            log.LogInformation($"{nameof(SafeExchangeExternalNotificationDetails)} triggered by {subjectType} {subjectId}, [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "get":
                    return await this.HandleExternalNotificationDetailsRead(webhookNotificationDataId, request, subjectType, subjectId, log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized." });
            }
        }

        private async Task<HttpResponseData> HandleExternalNotificationDetailsRead(string webhookNotificationDataId, HttpRequestData request, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () =>
            {
                await this.purger.PurgeNotificationDataIfNeededAsync(webhookNotificationDataId, this.dbContext);

                var existingNotificationData = await this.dbContext.WebhookNotificationData.FirstOrDefaultAsync(wnd => wnd.Id.Equals(webhookNotificationDataId));
                if (existingNotificationData == default)
                {
                    log.LogInformation($"Cannot get notification data '{webhookNotificationDataId}', as does not exist.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = $"Notification data '{webhookNotificationDataId}' does not exist." });
                }

                if (existingNotificationData.EventType != WebhookEventType.AccessRequestCreated)
                {
                    log.LogInformation($"Notification data '{webhookNotificationDataId}' is of type '{existingNotificationData.EventType}'.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = $"Notification data '{webhookNotificationDataId}' is not of type '{WebhookEventType.AccessRequestCreated}'." });
                }

                var existingAccessRequest = await this.dbContext.AccessRequests.FindAsync(existingNotificationData.EventId);
                if (existingAccessRequest == default)
                {
                    log.LogInformation($"Cannot get access request '{existingNotificationData.EventId}', as does not exist.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.Gone,
                        new BaseResponseObject<object> { Status = "gone", Error = $"Access request for notification data '{webhookNotificationDataId}' does not exist." });
                }

                if (existingAccessRequest.Status != RequestStatus.InProgress)
                {
                    log.LogInformation($"Access request '{existingNotificationData.EventId}' {nameof(existingAccessRequest.Status)} is '{existingAccessRequest.Status}'.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.Gone,
                        new BaseResponseObject<object> { Status = "gone", Error = $"Access request for notification data '{webhookNotificationDataId}' is not '{RequestStatus.InProgress}'." });
                }

                var responseData = new NotificationDataOutput()
                {
                    Url = $"{this.generalConfiguration.WebClientBaseUri.TrimEnd('/')}/accessrequests",
                    RecipientUpns = new List<string>(existingAccessRequest.Recipients
                        .Where(r => r.SubjectType.Equals(SubjectType.User))
                        .Select(r => r.SubjectName))
                };

                try
                {
                    await this.purger.PurgeNotificationDataAsync(webhookNotificationDataId, this.dbContext);
                }
                catch
                {
                    // no-op
                }

                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK,
                    new BaseResponseObject<NotificationDataOutput>
                    {
                        Status = "ok",
                        Result = responseData
                    });
            }, nameof(HandleExternalNotificationDetailsRead), log);

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
