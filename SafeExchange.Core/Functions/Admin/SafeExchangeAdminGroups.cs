﻿
namespace SafeExchange.Core.Functions.Admin
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Groups;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using System;
    using System.Net;
    using System.Security.Claims;
    using System.Text.RegularExpressions;

    public class SafeExchangeAdminGroups
    {
        private static string DefaultGuidRegex = "^([0-9A-Fa-f]{8}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{12})$";

        private static int MaxEmailLength = 320;

        private static string DefaultEmailRegex = @"^[\w-\.]+@([\w-]+\.)+[\w-]{2,4}$";

        private readonly SafeExchangeDbContext dbContext;

        private readonly IGroupsManager groupsManager;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        public SafeExchangeAdminGroups(SafeExchangeDbContext dbContext, IGroupsManager groupsManager, ITokenHelper tokenHelper, GlobalFilters globalFilters)
        {
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.groupsManager = groupsManager ?? throw new ArgumentNullException(nameof(groupsManager));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public async Task<HttpResponseData> Run(
            HttpRequestData request,
            string groupId,
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

            log.LogInformation($"{nameof(SafeExchangeAdminGroups)} triggered for '{groupId}' by {subjectType} {subjectId} [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "get":
                    return await this.HandleGroupRead(request, groupId, subjectType, subjectId, log);

                case "put":
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
            var existingGroup = await this.groupsManager.GetGroupAsync(groupId);
            if (existingGroup == default)
            {
                return await ActionResults.CreateResponseAsync(
                            request, HttpStatusCode.OK,
                            new BaseResponseObject<string> { Status = "no_content", Result = default });
            }

            return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.OK,
                        new BaseResponseObject<GraphGroupOutput>
            {
                Status = "ok",
                Result = existingGroup.ToDto()
            });

        }, nameof(HandleGroupRead), log);

        private async Task<HttpResponseData> HandleGroupRegistration(HttpRequestData request, string groupId, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () =>
        {
            var existingRegistration = await this.dbContext.GroupDictionary.FirstOrDefaultAsync(g => g.GroupId.Equals(groupId));
            if (existingRegistration != null)
            {
                log.LogInformation($"Group '{groupId}' is already registered.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK,
                    new BaseResponseObject<GraphGroupOutput>
                    {
                        Status = "ok",
                        Result = existingRegistration.ToDto()
                    });
            }

            var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            GroupInput? registrationInput;
            try
            {
                registrationInput = DefaultJsonSerializer.Deserialize<GroupInput>(requestBody);
            }
            catch
            {
                log.LogInformation($"Could not parse input data for '{groupId}'.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Group details are not provided." });
            }

            if (registrationInput is null)
            {
                log.LogInformation($"Input data for '{groupId}' is not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Group details are not provided." });
            }

            if (!Regex.IsMatch(groupId, DefaultGuidRegex))
            {
                log.LogInformation($"{nameof(groupId)} is in incorrect format.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Group Id is not in a guid format ('00000000-0000-0000-0000-000000000000')." });
            }

            if (!string.IsNullOrEmpty(registrationInput.Mail))
            {
                if (registrationInput.Mail.Length > SafeExchangeAdminGroups.MaxEmailLength)
                {
                    log.LogInformation($"{nameof(registrationInput.Mail)} for '{groupId}' is too long.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Group mail is too long." });
                }

                if (!Regex.IsMatch(registrationInput.Mail, SafeExchangeAdminGroups.DefaultEmailRegex))
                {
                    log.LogInformation($"{nameof(registrationInput.Mail)} for '{groupId}' is not in email-like format.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Group mail is in incorrect format." });
                }
            }

            var registeredGroup = await this.groupsManager.PutGroupAsync(groupId, registrationInput, subjectType, subjectId);
            log.LogInformation($"Group '{groupId}' ({registrationInput.DisplayName}, {registrationInput.Mail}) registered by {subjectType} '{subjectId}'.");

            return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Created,
                    new BaseResponseObject<GraphGroupOutput> { Status = "created", Result = registeredGroup.ToDto() });

        }, nameof(HandleGroupRegistration), log);

        private async Task<HttpResponseData> HandleGroupDeletion(HttpRequestData request, string groupId, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () =>
        {
            var existingRegistration = await this.dbContext.GroupDictionary.FirstOrDefaultAsync(g => g.GroupId.Equals(groupId));
            if (existingRegistration == null)
            {
                log.LogInformation($"Cannot delete group registration '{groupId}', as it does not exist.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK,
                    new BaseResponseObject<string> { Status = "no_content", Result = $"Group registration '{groupId}' does not exist." });
            }

            await this.groupsManager.DeleteGroupAsync(groupId);

            log.LogInformation($"{subjectType} '{subjectId}' deleted group registration '{groupId}'.");

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<string> { Status = "ok", Result = "ok" });

        }, nameof(HandleGroupDeletion), log);

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
