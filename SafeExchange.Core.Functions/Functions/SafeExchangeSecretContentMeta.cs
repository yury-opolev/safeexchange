/// <summary>
/// SafeExchangeSecretMeta
/// </summary>

namespace SafeExchange.Core.Functions
{
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using System.Security.Claims;
    using System;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging.Abstractions;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Services;
    using SafeExchange.Core.Telemetry;
    using Microsoft.Azure.Functions.Worker.Http;
    using System.Net;

    /// <summary>
    /// HTTP adapter for content items (<c>/secret/{secretId}/content[/{contentId}[/drop]]</c>) over <see cref="ContentService"/>.
    /// </summary>
    public class SafeExchangeSecretContentMeta
    {
        private readonly ICallerResolver callerResolver;

        private readonly ContentService contentService;

        public SafeExchangeSecretContentMeta(ICallerResolver callerResolver, ContentService contentService)
        {
            this.callerResolver = callerResolver ?? throw new ArgumentNullException(nameof(callerResolver));
            this.contentService = contentService ?? throw new ArgumentNullException(nameof(contentService));
        }

        /// <summary>
        /// Pre-service constructor, kept for code that builds handlers by hand: resolves callers through
        /// <paramref name="globalFilters"/> exactly as before and builds the service itself (without logging).
        /// </summary>
        public SafeExchangeSecretContentMeta(IConfiguration configuration, SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, IPurger purger, IPermissionsManager permissionsManager, IAuditWriter auditWriter)
            : this(
                  new GlobalFiltersCallerResolver(globalFilters, tokenHelper, dbContext),
                  new ContentService(configuration, dbContext, purger, permissionsManager, auditWriter, NullLogger<ContentService>.Instance))
        {
        }

        public async Task<HttpResponseData> Run(
            HttpRequestData request,
            string secretId, string contentId, ClaimsPrincipal principal, ILogger log)
        {
            var resolution = await this.callerResolver.ResolveAsync(principal, CallerChannels.Rest, null);
            if (!resolution.IsAllowed)
            {
                return await resolution.Denial.ToResponseAsync(request);
            }

            var caller = resolution.Caller;
            log.LogInformation($"{nameof(SafeExchangeSecretContentMeta)} triggered for '{secretId}' by {caller.SubjectType} (tid {TelemetryContext.Current}) [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "post":
                    return await ActionResults.ServiceCallAsync(request, async () =>
                    {
                        var input = await ReadInputAsync<ContentMetadataCreationInput>(request, secretId, log);
                        return await (await this.contentService.CreateAsync(caller, secretId, contentId, input)).ToResponseAsync(request);
                    }, "HandleSecretContentMetaCreation", log);

                case "patch":
                    return await ActionResults.ServiceCallAsync(request, async () =>
                    {
                        var input = await ReadInputAsync<ContentMetadataUpdateInput>(request, secretId, log);
                        return await (await this.contentService.UpdateAsync(caller, secretId, contentId, input)).ToResponseAsync(request);
                    }, "HandleSecretContentMetaDeletion", log);

                case "delete":
                    return await ActionResults.ServiceCallAsync(
                        request,
                        async () => await (await this.contentService.DeleteAsync(caller, secretId, contentId)).ToResponseAsync(request),
                        "HandleSecretContentMetaDeletion", log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        public async Task<HttpResponseData> RunDrop(
            HttpRequestData request,
            string secretId, string contentId, ClaimsPrincipal principal, ILogger log)
        {
            var resolution = await this.callerResolver.ResolveAsync(principal, CallerChannels.Rest, null);
            if (!resolution.IsAllowed)
            {
                return await resolution.Denial.ToResponseAsync(request);
            }

            var caller = resolution.Caller;
            log.LogInformation($"{nameof(SafeExchangeSecretContentMeta)} triggered for '{secretId}' by {caller.SubjectType} (tid {TelemetryContext.Current}) [DROP ({request.Method})].");

            switch (request.Method.ToLower())
            {
                case "patch":
                    return await ActionResults.ServiceCallAsync(
                        request,
                        async () => await (await this.contentService.DropAsync(caller, secretId, contentId)).ToResponseAsync(request),
                        "HandleSecretContentMetaDeletion", log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        private static async Task<T?> ReadInputAsync<T>(HttpRequestData request, string secretId, ILogger log) where T : class
        {
            var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            try
            {
                return DefaultJsonSerializer.Deserialize<T>(requestBody);
            }
            catch
            {
                log.LogInformation($"Could not parse input data for '{secretId}' new content.");
                return null;
            }
        }
    }
}
