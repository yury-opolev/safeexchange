/// <summary>
/// SafeExchangeNotificationSubscription
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using System;
    using System.Security.Claims;
    using System.Text.Json;
    using System.Web.Http;

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

        public async Task<IActionResult> Run(
            HttpRequest request, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResult ?? new EmptyResult();
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
                    return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        private async Task<IActionResult> AddSubscriptionAsync(HttpRequest request, string userUpn, ILogger log)
            => await TryCatch(async () =>
        {
            var subscriptionInput = await TryGetInputAsync<NotificationSubscriptionCreationInput>(request, log);
            if (string.IsNullOrWhiteSpace(subscriptionInput?.Url))
            {
                log.LogInformation($"Input data for subscription ({nameof(subscriptionInput.Url)}) is not provided.");
                return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = $"Input data for '{nameof(subscriptionInput.Url)}' is not provided." });
            }

            if (string.IsNullOrWhiteSpace(subscriptionInput?.P256dh))
            {
                log.LogInformation($"Input data for subscription ({nameof(subscriptionInput.P256dh)}) is not provided.");
                return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = $"Input data for '{nameof(subscriptionInput.P256dh)}' is not provided." });
            }

            if (string.IsNullOrWhiteSpace(subscriptionInput?.Auth))
            {
                log.LogInformation($"Input data for subscription ({nameof(subscriptionInput.Auth)}) is not provided.");
                return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = $"Input data for '{nameof(subscriptionInput.Auth)}' is not provided." });
            }

            this.dbContext.NotificationSubscriptions.Add(new NotificationSubscription(userUpn, subscriptionInput));

            await this.dbContext.SaveChangesAsync();

            return new OkObjectResult(new BaseResponseObject<string> { Status = "ok", Result = "ok" });

        }, nameof(AddSubscriptionAsync), log);

        private async Task<IActionResult> RemoveSubscriptionAsync(HttpRequest request, string userUpn, ILogger log)
            => await TryCatch(async () =>
        {
            var subscriptionInput = await TryGetInputAsync<NotificationSubscriptionDeletionInput>(request, log);
            if (string.IsNullOrWhiteSpace(subscriptionInput?.Url))
            {
                log.LogInformation($"Input data for web notification subscription is not provided.");
                return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Input data is not provided." });
            }

            var existingSubscription = await this.dbContext.NotificationSubscriptions.FirstOrDefaultAsync(ns => ns.Url.Equals(subscriptionInput.Url));
            if (existingSubscription == null)
            {
                log.LogInformation($"Cannot delete web notification subscription, as it not exists.");
                return new NotFoundObjectResult(new BaseResponseObject<object> { Status = "not_found", Error = $"Subscription with given URL does not exist." });
            }

            this.dbContext.NotificationSubscriptions.Remove(existingSubscription);
            await this.dbContext.SaveChangesAsync();

            return new OkObjectResult(new BaseResponseObject<string> { Status = "ok", Result = "ok" });

        }, nameof(RemoveSubscriptionAsync), log);

        private static async Task<T?> TryGetInputAsync<T>(HttpRequest request, ILogger log)
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
