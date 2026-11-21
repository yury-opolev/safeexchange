/// <summary>
/// SafeExchangeAccess
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Groups;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Services;
    using SafeExchange.Core.Telemetry;
    using System;
    using System.Net;
    using System.Security.Claims;

    /// <summary>
    /// HTTP adapter for <c>/access/{secretId}</c> over <see cref="AccessService"/>.
    /// </summary>
    public class SafeExchangeAccess
    {
        private readonly ICallerResolver callerResolver;

        private readonly AccessService accessService;

        public SafeExchangeAccess(ICallerResolver callerResolver, AccessService accessService)
        {
            this.callerResolver = callerResolver ?? throw new ArgumentNullException(nameof(callerResolver));
            this.accessService = accessService ?? throw new ArgumentNullException(nameof(accessService));
        }

        /// <summary>
        /// Pre-service constructor, kept for code that builds handlers by hand: resolves callers through
        /// <paramref name="globalFilters"/> exactly as before and builds the service itself (without logging).
        /// <paramref name="purger"/> is not used (it never was).
        /// </summary>
        public SafeExchangeAccess(
            SafeExchangeDbContext dbContext,
            IGroupsManager groupsManager,
            ITokenHelper tokenHelper,
            GlobalFilters globalFilters,
            IPurger purger,
            IPermissionsManager permissionsManager,
            IAuditWriter auditWriter,
            IOrphanedSecretManager orphanedSecretManager)
            : this(
                  new GlobalFiltersCallerResolver(globalFilters, tokenHelper, dbContext),
                  new AccessService(dbContext, groupsManager, permissionsManager, auditWriter, orphanedSecretManager, NullLogger<AccessService>.Instance))
        {
            ArgumentNullException.ThrowIfNull(purger);
        }

        public async Task<HttpResponseData> Run(
            HttpRequestData request,
            string secretId, ClaimsPrincipal principal, ILogger log, bool enrichedAccessList = false)
        {
            var resolution = await this.callerResolver.ResolveAsync(principal, CallerChannels.Rest, null);
            if (!resolution.IsAllowed)
            {
                return await resolution.Denial.ToResponseAsync(request);
            }

            var caller = resolution.Caller;
            log.LogInformation($"{nameof(SafeExchangeAccess)} triggered for '{secretId}' by {caller.SubjectType} (tid {TelemetryContext.Current}), [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "post":
                    return await ActionResults.ServiceCallAsync(request, async () =>
                    {
                        var input = await ReadInputAsync<List<SubjectPermissionsInput>>(request, "Could not parse input data for permissions input.", log);
                        return await (await this.accessService.GrantAsync(caller, secretId, input)).ToResponseAsync(request);
                    }, "GrantAccessAsync", log);

                case "get":
                    return enrichedAccessList
                        ? await ActionResults.ServiceCallAsync(
                            request,
                            async () => await (await this.accessService.ListAsync(caller, secretId)).ToResponseAsync(request),
                            "GetEnrichedAccessListAsync", log)
                        : await ActionResults.ServiceCallAsync(
                            request,
                            async () => await (await this.accessService.ListV2Async(caller, secretId)).ToResponseAsync(request),
                            "GetAccessListAsync", log);

                case "delete":
                    return await ActionResults.ServiceCallAsync(request, async () =>
                    {
                        var input = await ReadInputAsync<List<SubjectPermissionsInput>>(request, "Could not parse input data for permissions input.", log);
                        return await (await this.accessService.RevokeAsync(caller, secretId, input)).ToResponseAsync(request);
                    }, "RevokeAccessAsync", log);

                case "patch":
                    return await ActionResults.ServiceCallAsync(request, async () =>
                    {
                        var input = await ReadInputAsync<AccessUpdateInput>(request, "Could not parse input data for access update input.", log);
                        return await (await this.accessService.PatchAsync(caller, secretId, input)).ToResponseAsync(request);
                    }, "PatchAccessAsync", log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        private static async Task<T?> ReadInputAsync<T>(HttpRequestData request, string parseFailureMessage, ILogger log) where T : class
        {
            var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            try
            {
                return DefaultJsonSerializer.Deserialize<T>(requestBody);
            }
            catch (Exception exception)
            {
                log.LogWarning(exception, parseFailureMessage);
                return null;
            }
        }
    }
}
