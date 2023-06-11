/// <summary>
/// SafeExchangeNotificationSubscription
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using System;
    using System.Net;
    using System.Security.Claims;

    public class SafeExchangeNotificationSubscription
    {
        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        public SafeExchangeNotificationSubscription(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
        }

        public async Task<HttpResponseData> Run(
            HttpRequestData request, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResult ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            var userUpn = this.tokenHelper.GetUpn(principal);
            log.LogInformation($"{nameof(SafeExchangeNotificationSubscription)} triggered by {userUpn}, ID {this.tokenHelper.GetObjectId(principal)} [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "post":
                    return await this.AddSubscriptionAsync(request, userUpn, log);

                case "delete":
                    return await this.RemoveSubscriptionAsync(request, userUpn, log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        private async Task<HttpResponseData> AddSubscriptionAsync(HttpRequestData request, string userUpn, ILogger log)
            => await TryCatch(request, async () =>
        {
            var subscriptionInput = await TryGetInputAsync<NotificationSubscriptionCreationInput>(request, log);
            if (string.IsNullOrWhiteSpace(subscriptionInput?.Url))
            {
                log.LogInformation($"Input data for subscription ({nameof(subscriptionInput.Url)}) is not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = $"Input data for '{nameof(subscriptionInput.Url)}' is not provided." });
            }

            if (string.IsNullOrWhiteSpace(subscriptionInput?.P256dh))
            {
                log.LogInformation($"Input data for subscription ({nameof(subscriptionInput.P256dh)}) is not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = $"Input data for '{nameof(subscriptionInput.P256dh)}' is not provided." });
            }

            if (string.IsNullOrWhiteSpace(subscriptionInput?.Auth))
            {
                log.LogInformation($"Input data for subscription ({nameof(subscriptionInput.Auth)}) is not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = $"Input data for '{nameof(subscriptionInput.Auth)}' is not provided." });
            }

            this.dbContext.NotificationSubscriptions.Add(new NotificationSubscription(userUpn, subscriptionInput));

            await this.dbContext.SaveChangesAsync();

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<string> { Status = "ok", Result = "ok" });

        }, nameof(AddSubscriptionAsync), log);

        private async Task<HttpResponseData> RemoveSubscriptionAsync(HttpRequestData request, string userUpn, ILogger log)
            => await TryCatch(request, async () =>
        {
            var subscriptionInput = await TryGetInputAsync<NotificationSubscriptionDeletionInput>(request, log);
            if (string.IsNullOrWhiteSpace(subscriptionInput?.Url))
            {
                log.LogInformation($"Input data for web notification subscription is not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Input data is not provided." });
            }

            var existingSubscription = await this.dbContext.NotificationSubscriptions.FirstOrDefaultAsync(ns => ns.Url.Equals(subscriptionInput.Url));
            if (existingSubscription == null)
            {
                log.LogInformation($"Cannot delete web notification subscription, as it not exists.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.NotFound,
                    new BaseResponseObject<object> { Status = "not_found", Error = "Subscription with given URL does not exist." });
            }

            this.dbContext.NotificationSubscriptions.Remove(existingSubscription);
            await this.dbContext.SaveChangesAsync();

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<string> { Status = "ok", Result = "ok" });

        }, nameof(RemoveSubscriptionAsync), log);

        private static async Task<T?> TryGetInputAsync<T>(HttpRequestData request, ILogger log)
        {
            var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            try
            {
                return DefaultJsonSerializer.Deserialize<T>(requestBody);
            }
            catch (Exception exception)
            {
                log.LogWarning(exception, "Could not parse input data for permissions input.");
                return default;
            }
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
                    new BaseResponseObject<object> { Status = "error", Error = $"{ex.GetType()}: {ex.Message}" });
            }
        }
    }
}
