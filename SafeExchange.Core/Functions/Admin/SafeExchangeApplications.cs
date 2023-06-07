
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

    public class SafeExchangeApplications
    {
        private static string DefaultGuidRegex = "^([0-9A-Fa-f]{8}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{12})$";

        private static int MaxEmailLength = 320;

        private static string DefaultEmailRegex = @"^[\w-\.]+@([\w-]+\.)+[\w-]{2,4}$";

        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        public SafeExchangeApplications(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters)
        {
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public async Task<HttpResponseData> Run(
            HttpRequestData req,
            string applicationId, // display name
            ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetAdminFilterResultAsync(req, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? req.CreateResponse(HttpStatusCode.NoContent);
            }

            (SubjectType subjectType, string subjectId) = SubjectHelper.GetSubjectInfo(this.tokenHelper, principal);
            log.LogInformation($"{nameof(SafeExchangeApplications)} triggered for '{applicationId}' by {subjectType} {subjectId} [{req.Method}].");

            switch (req.Method.ToLower())
            {
                case "post":
                    return await this.HandleApplicationRegistration(req, applicationId, subjectType, subjectId, log);

                case "get":
                    return await this.HandleApplicationRead(req, applicationId, subjectType, subjectId, log);

                case "patch":
                    return await this.HandleApplicationUpdate(req, applicationId, subjectType, subjectId, log);

                case "delete":
                    return await this.HandleApplicationDeletion(req, applicationId, subjectType, subjectId, log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        req, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized." });
            }
        }

        private async Task<HttpResponseData> HandleApplicationRegistration(HttpRequestData request, string applicationId, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () =>
        {
            var existingRegistration = await this.dbContext.Applications.FirstOrDefaultAsync(o => o.DisplayName.Equals(applicationId));
            if (existingRegistration != null)
            {
                log.LogInformation($"Cannot register application '{applicationId}', as already exists");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Conflict,
                    new BaseResponseObject<object> { Status = "conflict", Error = $"Application '{applicationId}' is already registered with display name." });
            }

            var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            ApplicationRegistrationInput? registrationInput;
            try
            {
                registrationInput = DefaultJsonSerializer.Deserialize<ApplicationRegistrationInput>(requestBody);
            }
            catch
            {
                log.LogInformation($"Could not parse input data for '{applicationId}'.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Application details are not provided." });
            }

            if (registrationInput is null)
            {
                log.LogInformation($"Input data for '{applicationId}' is not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Application details are not provided." });
            }

            if (string.IsNullOrEmpty(registrationInput.ContactEmail))
            {
                log.LogInformation($"{nameof(ApplicationRegistrationInput.ContactEmail)} for '{applicationId}' is not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Contact email is not provided." });
            }

            if (registrationInput.ContactEmail.Length > SafeExchangeApplications.MaxEmailLength)
            {
                log.LogInformation($"{nameof(registrationInput.ContactEmail)} for '{applicationId}' is too long.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Contact email is too long." });
            }

            if (!Regex.IsMatch(registrationInput.ContactEmail, SafeExchangeApplications.DefaultEmailRegex))
            {
                log.LogInformation($"{nameof(registrationInput.ContactEmail)} for '{applicationId}' is not in email-like format.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Contact email is in incorrect format." });
            }

            if (string.IsNullOrEmpty(registrationInput.AadTenantId))
            {
                log.LogInformation($"{nameof(ApplicationRegistrationInput.AadTenantId)} for '{applicationId}' is not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Tenant Id is not provided." });
            }

            if (!Regex.IsMatch(registrationInput.AadTenantId, DefaultGuidRegex))
            {
                log.LogInformation($"{nameof(ApplicationRegistrationInput.AadTenantId)} for '{applicationId}' is in incorrect format.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Tenant Id is not in a guid format ('00000000-0000-0000-0000-000000000000')." });
            }

            if (string.IsNullOrEmpty(registrationInput.AadClientId))
            {
                log.LogInformation($"{nameof(ApplicationRegistrationInput.AadClientId)} for '{applicationId}' is not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Client Id is not provided." });
            }

            if (!Regex.IsMatch(registrationInput.AadClientId, DefaultGuidRegex))
            {
                log.LogInformation($"{nameof(ApplicationRegistrationInput.AadClientId)} for '{applicationId}' is in incorrect format.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Client Id is not in a guid format ('00000000-0000-0000-0000-000000000000')." });
            }

            existingRegistration = await this.dbContext.Applications
                .FirstOrDefaultAsync(r => r.AadTenantId.Equals(registrationInput.AadTenantId) && r.AadClientId.Equals(registrationInput.AadClientId));
            if (existingRegistration != null)
            {
                log.LogInformation($"Cannot register application '{applicationId}', as already exists with provided tenant and client Ids.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Conflict,
                    new BaseResponseObject<object> { Status = "conflict", Error = $"Application '{applicationId}' is already registered with tenant and client ids." });
            }

            var registeredApplication = await this.RegisterApplicationAsync(applicationId, registrationInput, subjectType, subjectId, log);
            log.LogInformation($"Application '{applicationId}' ({registrationInput.AadTenantId}.{registrationInput.AadClientId}) registered by {subjectType} '{subjectId}'.");

            return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK,
                    new BaseResponseObject<ApplicationRegistrationOutput> { Status = "ok", Result = registeredApplication.ToDto() });

        }, nameof(HandleApplicationRegistration), log);

        private async Task<HttpResponseData> HandleApplicationRead(HttpRequestData request, string applicationId, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () =>
        {
            var existingRegistration = await this.dbContext.Applications.FirstOrDefaultAsync(o => o.DisplayName.Equals(applicationId));
            if (existingRegistration == null)
            {
                log.LogInformation($"Cannot get application registration '{applicationId}', as does not exist.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.NotFound,
                    new BaseResponseObject<object> { Status = "not_found", Error = $"Application registration '{applicationId}' does not exist." });
            }

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<ApplicationRegistrationOutput> { Status = "ok", Result = existingRegistration.ToDto() });
        }, nameof(HandleApplicationRead), log);

        private async Task<HttpResponseData> HandleApplicationUpdate(HttpRequestData request, string applicationId, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () =>
        {
            var existingRegistration = await this.dbContext.Applications.FirstOrDefaultAsync(o => o.DisplayName.Equals(applicationId));
            if (existingRegistration == null)
            {
                log.LogInformation($"Cannot update application registration '{applicationId}', as it does not exist.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.NotFound,
                    new BaseResponseObject<object> { Status = "not_found", Error = $"Application registration '{applicationId}' does not exist." });
            }

            var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            ApplicationRegistrationUpdateInput? updateInput;
            try
            {
                updateInput = DefaultJsonSerializer.Deserialize<ApplicationRegistrationUpdateInput>(requestBody);
            }
            catch
            {
                log.LogInformation($"Could not parse input data for '{applicationId}' update.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Update data is not provided." });
            }

            if (updateInput is null)
            {
                log.LogInformation($"Update input for '{applicationId}' is not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Update data is not provided." });
            }

            if (updateInput.Enabled is null || string.IsNullOrEmpty(updateInput.ContactEmail))
            {
                log.LogInformation($"Either {nameof(updateInput.Enabled)} or {nameof(updateInput.ContactEmail)} property for '{applicationId}' is not provided for update.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Update data is not provided." });
            }

            if (!string.IsNullOrEmpty(updateInput.ContactEmail) &&
                (updateInput.ContactEmail.Length > SafeExchangeApplications.MaxEmailLength || !Regex.IsMatch(updateInput.ContactEmail, DefaultEmailRegex)))
            {
                log.LogInformation($"{nameof(updateInput.ContactEmail)} property value for '{applicationId}' is incorrect.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Contact email is in incorrect format." });
            }

            var updatedRegistration = await this.UpdateApplicationRegistrationAsync(existingRegistration, updateInput, log);
            log.LogInformation($"{subjectType} '{subjectId}' updated application registration '{existingRegistration.DisplayName}'.");

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<ApplicationRegistrationOutput> { Status = "ok", Result = updatedRegistration.ToDto() });
        }, nameof(HandleApplicationUpdate), log);

        private async Task<HttpResponseData> HandleApplicationDeletion(HttpRequestData request, string applicationId, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () =>
        {
            var existingRegistration = await this.dbContext.Applications.FirstOrDefaultAsync(o => o.DisplayName.Equals(applicationId));
            if (existingRegistration == null)
            {
                log.LogInformation($"Cannot delete application registration '{applicationId}', as it does not exist.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.NoContent,
                    new BaseResponseObject<string> { Status = "no_content", Result = $"Application registration '{applicationId}' does not exist." });
            }

            this.dbContext.Applications.Remove(existingRegistration);
            await dbContext.SaveChangesAsync();

            log.LogInformation($"{subjectType} '{subjectId}' deleted application registration '{applicationId}'.");

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<string> { Status = "ok", Result = "ok" });
        }, nameof(HandleApplicationDeletion), log);

        private async Task<Application> RegisterApplicationAsync(string applicationId, ApplicationRegistrationInput registrationInput, SubjectType subjectType, string subjectId, ILogger log)
        {
            var appRegistration = new Application(applicationId, registrationInput, $"{subjectType} {subjectId}");
            var entity = await this.dbContext.Applications.AddAsync(appRegistration);

            await this.dbContext.SaveChangesAsync();

            return entity.Entity;
        }

        private async Task<Application> UpdateApplicationRegistrationAsync(Application existingApplication, ApplicationRegistrationUpdateInput updateInput, ILogger log)
        {
            if (updateInput.Enabled is not null)
            {
                existingApplication.Enabled = updateInput.Enabled ?? true;
            }

            if (!string.IsNullOrEmpty(updateInput.ContactEmail))
            {
                existingApplication.ContactEmail = updateInput.ContactEmail;
            }

            this.dbContext.Applications.Update(existingApplication);
            await this.dbContext.SaveChangesAsync();

            return existingApplication;
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
