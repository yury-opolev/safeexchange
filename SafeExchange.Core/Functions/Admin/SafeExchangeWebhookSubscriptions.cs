
namespace SafeExchange.Core.Functions.Admin
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using System;
    using System.Net;
    using System.Security.Claims;
    using System.Text.RegularExpressions;

    public class SafeExchangeWebhookSubscriptions
    {
        private static int MaxEmailLength = 320;

        private static string DefaultEmailRegex = @"^[\w-\.]+@([\w-]+\.)+[\w-]{2,4}$";

        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        public SafeExchangeWebhookSubscriptions(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters)
        {
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public async Task<HttpResponseData> Run(
            HttpRequestData request,
            string webhookSubscriptionId,
            ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetAdminFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            (SubjectType subjectType, string subjectId) = await SubjectHelper.GetSubjectInfoAsync(this.tokenHelper, principal, this.dbContext);
            if (SubjectType.Application.Equals(subjectType))
            {
                await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Forbidden,
                    new BaseResponseObject<object> { Status = "forbidden", Error = "Applications cannot use this API." });
            }

            log.LogInformation($"{nameof(SafeExchangeApplications)} triggered for webhook subscription{(string.IsNullOrEmpty(webhookSubscriptionId) ? string.Empty : $" ID '{webhookSubscriptionId}'")} by {subjectType} {subjectId} [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "post":
                    return await this.HandleWebhookSubscriptionCreation(request, subjectType, subjectId, log);

                case "get":
                    return await this.HandleWebhookSubscriptionRead(request, webhookSubscriptionId, subjectType, subjectId, log);

                case "patch":
                    return await this.HandleWebhookSubscriptionUpdate(request, webhookSubscriptionId, subjectType, subjectId, log);

                case "delete":
                    return await this.HandleWebhookSubscriptionDeletion(request, webhookSubscriptionId, subjectType, subjectId, log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized." });
            }
        }

        private async Task<HttpResponseData> HandleWebhookSubscriptionCreation(HttpRequestData request, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () =>
        {
            var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            WebhookSubscriptionCreationInput? creationInput;
            try
            {
                creationInput = DefaultJsonSerializer.Deserialize<WebhookSubscriptionCreationInput>(requestBody);
            }
            catch
            {
                log.LogInformation($"Could not parse input data for webhook subscription creation.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Could not parse webhook subscription details." });
            }

            if (creationInput is null)
            {
                log.LogInformation($"Input data for webhook subscription creation is not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Webhook subscription details are not provided." });
            }

            WebhookEventType eventType;
            try
            {
                eventType = creationInput.EventType.ToModel();
            }
            catch
            {
                log.LogInformation($"Could not convert input event type to model event type, probably not specified.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Could not parse webhook subscription event type." });
            }

            if (string.IsNullOrEmpty(creationInput.ContactEmail))
            {
                log.LogInformation($"{nameof(WebhookSubscriptionCreationInput.ContactEmail)} for webhook subscription is not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Contact email is not provided." });
            }

            if (creationInput.ContactEmail.Length > SafeExchangeWebhookSubscriptions.MaxEmailLength)
            {
                log.LogInformation($"{nameof(creationInput.ContactEmail)} for webhook subscription is too long.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Contact email is too long." });
            }

            if (!Regex.IsMatch(creationInput.ContactEmail, SafeExchangeWebhookSubscriptions.DefaultEmailRegex))
            {
                log.LogInformation($"{nameof(creationInput.ContactEmail)} for webhook subscription is not in email-like format.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Contact email is in incorrect format." });
            }

            if (string.IsNullOrEmpty(creationInput.Url))
            {
                log.LogInformation($"{nameof(WebhookSubscriptionCreationInput.Url)} for webhook subscription is not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Url is not provided." });
            }

            var existingSubscription = await this.dbContext.WebhookSubscriptions.FirstOrDefaultAsync(whs => whs.EventType.Equals(eventType) && whs.Url.Equals(creationInput.Url));
            if (existingSubscription != null)
            {
                log.LogInformation($"Cannot register webhook subscription, as it already exists with ID '{existingSubscription.Id}'.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Conflict,
                    new BaseResponseObject<object> { Status = "conflict", Error = $"Webhook subscription for specified type and Url is already registered with ID '{existingSubscription.Id}'." });
            }

            var createdSubscription = await this.CreateWebhookSubscriptionAsync(creationInput, subjectType, subjectId, log);
            log.LogInformation($"Webhook subscription ID '{createdSubscription.Id}' ({creationInput.EventType}, {creationInput.Url}) created by {subjectType} '{subjectId}'.");

            return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK,
                    new BaseResponseObject<WebhookSubscriptionOutput> { Status = "ok", Result = createdSubscription.ToDto() });

        }, nameof(HandleWebhookSubscriptionCreation), log);

        private async Task<HttpResponseData> HandleWebhookSubscriptionRead(HttpRequestData request, string webhookSubscriptionId, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () =>
        {
            var existingSubscription = await this.dbContext.WebhookSubscriptions.FirstOrDefaultAsync(whs => whs.Id.Equals(webhookSubscriptionId));
            if (existingSubscription == null)
            {
                log.LogInformation($"Cannot get webhook subscription '{webhookSubscriptionId}', as does not exist.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.NotFound,
                    new BaseResponseObject<object> { Status = "not_found", Error = $"Webhook subscription '{webhookSubscriptionId}' does not exist." });
            }

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<WebhookSubscriptionOutput> { Status = "ok", Result = existingSubscription.ToDto() });
        }, nameof(HandleWebhookSubscriptionRead), log);

        private async Task<HttpResponseData> HandleWebhookSubscriptionUpdate(HttpRequestData request, string webhookSubscriptionId, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () =>
        {
            var existingSubscription = await this.dbContext.WebhookSubscriptions.FirstOrDefaultAsync(whs => whs.Id.Equals(webhookSubscriptionId));
            if (existingSubscription == null)
            {
                log.LogInformation($"Cannot get webhook subscription '{webhookSubscriptionId}', as does not exist.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.NotFound,
                    new BaseResponseObject<object> { Status = "not_found", Error = $"Webhook subscription '{webhookSubscriptionId}' does not exist." });
            }

            var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            WebhookSubscriptionUpdateInput? updateInput;
            try
            {
                updateInput = DefaultJsonSerializer.Deserialize<WebhookSubscriptionUpdateInput>(requestBody);
            }
            catch
            {
                log.LogInformation($"Could not parse input data for '{webhookSubscriptionId}' update.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Update data is not provided." });
            }

            if (updateInput is null)
            {
                log.LogInformation($"Update input for '{webhookSubscriptionId}' is not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Update data is not provided." });
            }

            if (updateInput.Enabled is null && updateInput.Authenticate is null && updateInput.WebhookCallDelay is null && updateInput.ContactEmail is null)
            {
                log.LogInformation($"All update input properties are nulls for '{webhookSubscriptionId}' is not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Update data is not provided." });
            }

            if (updateInput.Authenticate == true && string.IsNullOrEmpty(updateInput.AuthenticationResource))
            {
                log.LogInformation($"{nameof(updateInput.AuthenticationResource)} property for '{webhookSubscriptionId}' is not provided for update, when {nameof(updateInput.Authenticate)} is set to {updateInput.Authenticate}.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = $"Update data for '{nameof(updateInput.AuthenticationResource)}' is not provided." });
            }

            if (!string.IsNullOrEmpty(updateInput.ContactEmail) &&
                (updateInput.ContactEmail.Length > SafeExchangeWebhookSubscriptions.MaxEmailLength || !Regex.IsMatch(updateInput.ContactEmail, DefaultEmailRegex)))
            {
                log.LogInformation($"{nameof(updateInput.ContactEmail)} property value for '{webhookSubscriptionId}' is incorrect.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Contact email is in incorrect format." });
            }

            var updatedSubscription = await this.UpdateWebhookSubscriptionAsync(existingSubscription, updateInput, log);
            log.LogInformation($"{subjectType} '{subjectId}' updated webhook subscription '{existingSubscription.Id}'.");

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<WebhookSubscriptionOutput> { Status = "ok", Result = updatedSubscription.ToDto() });
        }, nameof(HandleWebhookSubscriptionUpdate), log);

        private async Task<HttpResponseData> HandleWebhookSubscriptionDeletion(HttpRequestData request, string webhookSubscriptionId, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () =>
        {
            var existingSubscription = await this.dbContext.WebhookSubscriptions.FirstOrDefaultAsync(whs => whs.Id.Equals(webhookSubscriptionId));
            if (existingSubscription == null)
            {
                log.LogInformation($"Cannot delete webhook subscription '{webhookSubscriptionId}', as it does not exist.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.NoContent,
                    new BaseResponseObject<string> { Status = "no_content", Result = $"Webhook subscription '{webhookSubscriptionId}' does not exist." });
            }

            this.dbContext.WebhookSubscriptions.Remove(existingSubscription);
            await dbContext.SaveChangesAsync();

            log.LogInformation($"{subjectType} '{subjectId}' deleted webhook subscription '{webhookSubscriptionId}'.");

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<string> { Status = "ok", Result = "ok" });
        }, nameof(HandleWebhookSubscriptionDeletion), log);

        private async Task<WebhookSubscription> CreateWebhookSubscriptionAsync(WebhookSubscriptionCreationInput creationInput, SubjectType subjectType, string subjectId, ILogger log)
        {
            var webhookSubscription = new WebhookSubscription(creationInput, $"{subjectType} {subjectId}");
            var entity = await this.dbContext.WebhookSubscriptions.AddAsync(webhookSubscription);

            await this.dbContext.SaveChangesAsync();

            return entity.Entity;
        }

        private async Task<WebhookSubscription> UpdateWebhookSubscriptionAsync(WebhookSubscription existingSubscription, WebhookSubscriptionUpdateInput updateInput, ILogger log)
        {
            if (updateInput.Enabled is not null)
            {
                existingSubscription.Enabled = updateInput.Enabled ?? true;
            }

            if (updateInput.Authenticate is not null)
            {
                existingSubscription.Authenticate = updateInput.Authenticate ?? false;
                existingSubscription.AuthenticationResource = updateInput.AuthenticationResource ?? string.Empty;
            }

            if (updateInput.WebhookCallDelay is not null)
            {
                existingSubscription.WebhookCallDelay = updateInput.WebhookCallDelay ?? TimeSpan.Zero;
            }

            if (!string.IsNullOrEmpty(updateInput.ContactEmail))
            {
                existingSubscription.ContactEmail = updateInput.ContactEmail;
            }

            this.dbContext.WebhookSubscriptions.Update(existingSubscription);
            await this.dbContext.SaveChangesAsync();

            return existingSubscription;
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
