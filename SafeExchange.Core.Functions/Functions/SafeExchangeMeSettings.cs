/// <summary>
/// SafeExchangeMeSettings — the caller's own settings (GET and PATCH /me/settings). Users only.
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Utilities;
    using System;
    using System.IO;
    using System.Net;
    using System.Security.Claims;
    using System.Text.Json;
    using System.Threading.Tasks;

    public class SafeExchangeMeSettings
    {
        public const string UsersOnlyError = "Settings are available to users only.";

        public const string EnableWhileBlockedError = "MCP is blocked for this account by an administrator.";

        public const string InvalidBodyError = "Body must be { \"mcpAllowed\": bool }.";

        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        public SafeExchangeMeSettings(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
        }

        /// <summary>GET /me/settings.</summary>
        public async Task<HttpResponseData> RunGet(HttpRequestData request, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            if (!this.tokenHelper.IsUserToken(principal))
            {
                return await ActionResults.ForbiddenAsync(request, UsersOnlyError);
            }

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var user = await UserLookup.FindByTokenAsync(this.dbContext, this.tokenHelper, principal);
                if (user is null)
                {
                    return await UserNotFoundAsync(request);
                }

                return await SettingsAsync(request, user);
            }, nameof(RunGet), log);
        }

        /// <summary>PATCH /me/settings, body <c>{ "mcpAllowed": bool }</c>.</summary>
        public async Task<HttpResponseData> RunUpdate(HttpRequestData request, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            if (!this.tokenHelper.IsUserToken(principal))
            {
                return await ActionResults.ForbiddenAsync(request, UsersOnlyError);
            }

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var body = await new StreamReader(request.Body).ReadToEndAsync();
                UserSettingsInput? input;
                try
                {
                    input = DefaultJsonSerializer.Deserialize<UserSettingsInput>(body);
                }
                catch (JsonException)
                {
                    input = null;
                }

                if (input?.McpAllowed is not bool mcpAllowed)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = InvalidBodyError });
                }

                var user = await UserLookup.FindByTokenAsync(this.dbContext, this.tokenHelper, principal);
                if (user is null)
                {
                    return await UserNotFoundAsync(request);
                }

                // Re-checked on every attempt, so a block saved concurrently wins over enabling.
                var saved = await UserUpdate.SaveWithRetryAsync(this.dbContext, user, u =>
                {
                    if (mcpAllowed && u.McpBlockedByAdmin)
                    {
                        return false;
                    }

                    u.McpAllowed = mcpAllowed;
                    u.ModifiedAt = DateTimeProvider.UtcNow;
                    return true;
                });

                if (!saved)
                {
                    log.LogInformation("User (tid {TelemetryId}) cannot allow MCP: blocked by an administrator.", user.TelemetryId);
                    return await ActionResults.ForbiddenAsync(request, EnableWhileBlockedError, McpUserAccessPolicy.BlockedByAdminSubStatus);
                }

                log.LogInformation("User (tid {TelemetryId}) set McpAllowed to {McpAllowed}.", user.TelemetryId, mcpAllowed);
                return await SettingsAsync(request, user);
            }, nameof(RunUpdate), log);
        }

        private static Task<HttpResponseData> SettingsAsync(HttpRequestData request, User user)
            => ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                new BaseResponseObject<UserSettingsOutput> { Status = "ok", Result = UserSettingsOutput.From(user) });

        private static Task<HttpResponseData> UserNotFoundAsync(HttpRequestData request)
            => ActionResults.CreateResponseAsync(request, HttpStatusCode.NotFound,
                new BaseResponseObject<object> { Status = "not_found", Error = "User not found." });
    }
}
