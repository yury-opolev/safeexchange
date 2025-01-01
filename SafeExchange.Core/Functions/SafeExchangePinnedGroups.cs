
namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Utilities;
    using System;
    using System.Net;
    using System.Security.Claims;
    using System.Text.RegularExpressions;

    public class SafeExchangePinnedGroups
    {
        public static int MaxPinnedGroupsPerUser = 100;

        private static string DefaultGuidRegex = "^([0-9A-Fa-f]{8}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{12})$";

        private static int MaxEmailLength = 320;

        private static string DefaultEmailRegex = @"^[\w-\.]+@([\w-]+\.)+[\w-]{2,4}$";

        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        public SafeExchangePinnedGroups(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters)
        {
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public async Task<HttpResponseData> Run(
            HttpRequestData request,
            string pinnedGroupId,
            ClaimsPrincipal principal,
            ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
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

            if (!Regex.IsMatch(pinnedGroupId, DefaultGuidRegex))
            {
                log.LogInformation($"{nameof(pinnedGroupId)} is in incorrect format.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Pinned group Id is not in a guid format ('00000000-0000-0000-0000-000000000000')." });
            }

            log.LogInformation($"{nameof(SafeExchangePinnedGroups)} triggered by {subjectType} {subjectId}, [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "get":
                    return await this.HandlePinnedGroupRead(request, pinnedGroupId, subjectType, subjectId, log);

                case "put":
                    return await this.HandlePinnedGroupRegistration(request, pinnedGroupId, subjectType, subjectId, log);

                case "delete":
                    return await this.HandlePinnedGroupDeletion(request, pinnedGroupId, subjectType, subjectId, log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = $"Request method '{request.Method}' not allowed." });
            }
        }

        private async Task<HttpResponseData> HandlePinnedGroupRead(HttpRequestData request, string pinnedGroupId, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () =>
        {
            var existingPinnedGroup = await this.dbContext.PinnedGroups.FirstOrDefaultAsync(pg => pg.UserId.Equals(subjectId) && pg.GroupItemId.Equals(pinnedGroupId));
            if (existingPinnedGroup == default)
            {
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.NoContent,
                    new BaseResponseObject<string> { Status = "no_content", Result = $"Pinned group '{pinnedGroupId}' does not exist." });
            }

            var existingGroup = await this.dbContext.GroupDictionary.FirstOrDefaultAsync(g => g.GroupId.Equals(pinnedGroupId));
            if (existingGroup == default)
            {
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.NoContent,
                    new BaseResponseObject<string> { Status = "no_content", Result = $"Group '{pinnedGroupId}' does not exist." });
            }

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<PinnedGroupOutput>
                {
                    Status = "ok",
                    Result = existingGroup.ToPinnedGroupDto()
                });

        }, nameof(HandlePinnedGroupRead), log);

        private async Task<HttpResponseData> HandlePinnedGroupRegistration(HttpRequestData request, string pinnedGroupId, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () =>
        {
            var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            PinnedGroupInput? registrationInput;
            try
            {
                registrationInput = DefaultJsonSerializer.Deserialize<PinnedGroupInput>(requestBody);
            }
            catch
            {
                log.LogInformation($"Could not parse input data for '{pinnedGroupId}'.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Pinned group details are not provided." });
            }

            if (registrationInput is null)
            {
                log.LogInformation($"Input data for '{pinnedGroupId}' is not provided.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Pinned group details are not provided." });
            }

            if (!string.IsNullOrEmpty(registrationInput.GroupMail))
            {
                if (registrationInput.GroupMail.Length > SafeExchangePinnedGroups.MaxEmailLength)
                {
                    log.LogInformation($"{nameof(registrationInput.GroupMail)} for '{pinnedGroupId}' is too long.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Pinned group mail is too long." });
                }

                if (!Regex.IsMatch(registrationInput.GroupMail, SafeExchangePinnedGroups.DefaultEmailRegex))
                {
                    log.LogInformation($"{nameof(registrationInput.GroupMail)} for '{pinnedGroupId}' is not in email-like format.");
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Pinned group mail is in incorrect format." });
                }
            }

            var userId = request.FunctionContext.GetUserId();
            var existingPinnedGroupsCount = await this.dbContext.PinnedGroups
                .Where(pg => pg.UserId.Equals(userId))
                .CountAsync();

            if (existingPinnedGroupsCount >= MaxPinnedGroupsPerUser)
            {
                log.LogInformation($"Existing number of pinned groups for the user is {existingPinnedGroupsCount}, which is more or equal to max. allowed count of {MaxPinnedGroupsPerUser}.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = $"Pinned group count is {existingPinnedGroupsCount}, which is higher or equal than allowed no. of {MaxPinnedGroupsPerUser} pinned groups. Please remove pinned groups before adding new ones." });
            }

            var groupItemForPinnedGroup = await this.GetOrAddPinnedGroupAsync(pinnedGroupId, registrationInput, userId, subjectType, subjectId, log);
            return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK,
                    new BaseResponseObject<GraphGroupOutput> { Status = "ok", Result = groupItemForPinnedGroup.ToDto() });

        }, nameof(HandlePinnedGroupRegistration), log);

        private async Task<HttpResponseData> HandlePinnedGroupDeletion(HttpRequestData request, string pinnedGroupId, SubjectType subjectType, string subjectId, ILogger log)
            => await TryCatch(request, async () =>
        {
            var userId = request.FunctionContext.GetUserId();
            var existingPinnedGroup = await this.dbContext.PinnedGroups.FirstOrDefaultAsync(pg => pg.UserId.Equals(userId) && pg.GroupItemId.Equals(pinnedGroupId));
            if (existingPinnedGroup == null)
            {
                log.LogInformation($"Cannot delete group registration '{pinnedGroupId}', as it does not exist.");
                return await ActionResults.CreateResponseAsync(
                            request, HttpStatusCode.NoContent,
                            new BaseResponseObject<string> { Status = "no_content", Result = $"Group registration '{pinnedGroupId}' does not exist." });
            }

            this.dbContext.PinnedGroups.Remove(existingPinnedGroup);
            await dbContext.SaveChangesAsync();

            log.LogInformation($"{subjectType} '{subjectId}' deleted pinned group registration '{pinnedGroupId}'.");

            return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.OK,
                        new BaseResponseObject<string> { Status = "ok", Result = "ok" });

        }, nameof(HandlePinnedGroupDeletion), log);

        private async Task<GroupDictionaryItem> GetOrAddPinnedGroupAsync(string pinnedGroupId, PinnedGroupInput? input, string userId, SubjectType subjectType, string subjectId, ILogger log)
        {
            var groupItem = await this.GetOrAddGroupItemAsync(pinnedGroupId, input, subjectType, subjectId, log);
            var pinnedGroup = await this.dbContext.PinnedGroups.FirstOrDefaultAsync(pg => pg.UserId.Equals(userId) && pg.GroupItemId.Equals(pinnedGroupId));
            if (pinnedGroup == default)
            {
                pinnedGroup = await this.RegisterPinnedGroupAsync(input, userId, log);
                log.LogInformation($"Pinned group '{pinnedGroupId}' ({input.GroupDisplayName}, {input.GroupMail}) registered by {subjectType} '{subjectId}'.");
            }

            return groupItem;
        }

        private async Task<GroupDictionaryItem> GetOrAddGroupItemAsync(string pinnedGroupId, PinnedGroupInput? input, SubjectType subjectType, string subjectId, ILogger log)
        {
            var groupItem = await this.dbContext.GroupDictionary.FirstOrDefaultAsync(g => g.GroupId.Equals(pinnedGroupId));
            if (groupItem == default)
            {
                groupItem = await this.RegisterGroupAsync(pinnedGroupId, input, subjectType, subjectId, log);
                log.LogInformation($"Group '{pinnedGroupId}' ({input.GroupDisplayName}, {input.GroupMail}) registered by {subjectType} '{subjectId}'.");
            }

            return groupItem;
        }

        private async Task<GroupDictionaryItem> RegisterGroupAsync(string groupId, PinnedGroupInput registrationInput, SubjectType subjectType, string subjectId, ILogger log)
        {
            var groupItem = new GroupDictionaryItem(groupId, registrationInput, $"{subjectType} {subjectId}");
            return await DbUtils.TryAddOrGetEntityAsync(
                async () =>
                {
                    var entity = await this.dbContext.GroupDictionary.AddAsync(groupItem);
                    await this.dbContext.SaveChangesAsync();
                    return entity.Entity;
                },
                async () =>
                {
                    this.dbContext.GroupDictionary.Remove(groupItem);
                    var existingGroupItem = await this.dbContext.GroupDictionary.FirstAsync(g => g.GroupId.Equals(groupId));
                    return existingGroupItem;
                },
                log);
        }

        private async Task<PinnedGroup> RegisterPinnedGroupAsync(PinnedGroupInput registrationInput, string userId, ILogger log)
        {
            var pinnedGroup = new PinnedGroup(userId, registrationInput);
            return await DbUtils.TryAddOrGetEntityAsync(
                async () =>
                {
                    var entity = await this.dbContext.PinnedGroups.AddAsync(pinnedGroup);
                    await this.dbContext.SaveChangesAsync();
                    return entity.Entity;
                },
                async () =>
                {
                    this.dbContext.PinnedGroups.Remove(pinnedGroup);
                    var existingPinnedGroup = await this.dbContext.PinnedGroups.FirstAsync(pg => pg.UserId.Equals(userId) && pg.GroupItemId.Equals(registrationInput.GroupId));
                    return existingPinnedGroup;
                },
                log);
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
