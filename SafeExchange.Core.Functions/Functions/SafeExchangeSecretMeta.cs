/// <summary>
/// SafeExchangeSecretMeta
/// </summary>

namespace SafeExchange.Core.Functions
{
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using System.Security.Claims;
    using System;
    using System.IO;

    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging.Abstractions;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Services;
    using SafeExchange.Core.Telemetry;
    using Microsoft.Azure.Functions.Worker.Http;
    using System.Collections.Generic;
    using System.Net;

    /// <summary>
    /// HTTP adapter for secret metadata (<c>/secret/{secretId}</c>, <c>/secret-list</c>) over <see cref="SecretService"/>.
    /// </summary>
    public class SafeExchangeSecretMeta
    {
        private readonly ICallerResolver callerResolver;

        private readonly SecretService secretService;

        public SafeExchangeSecretMeta(ICallerResolver callerResolver, SecretService secretService)
        {
            this.callerResolver = callerResolver ?? throw new ArgumentNullException(nameof(callerResolver));
            this.secretService = secretService ?? throw new ArgumentNullException(nameof(secretService));
        }

        /// <summary>
        /// Pre-service constructor, kept for code that builds handlers by hand: resolves callers through
        /// <paramref name="globalFilters"/> exactly as before and builds the service itself (without logging).
        /// </summary>
        public SafeExchangeSecretMeta(IConfiguration configuration, SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, IPurger purger, IPermissionsManager permissionsManager, IAuditWriter auditWriter)
            : this(
                  new GlobalFiltersCallerResolver(globalFilters, tokenHelper, dbContext),
                  new SecretService(configuration, dbContext, purger, permissionsManager, auditWriter, NullLogger<SecretService>.Instance))
        {
        }

        public async Task<HttpResponseData> Run(
            HttpRequestData request,
            string secretId, ClaimsPrincipal principal, ILogger log)
        {
            var resolution = await this.callerResolver.ResolveAsync(principal, CallerChannels.Rest, null);
            if (!resolution.IsAllowed)
            {
                return await resolution.Denial.ToResponseAsync(request);
            }

            var caller = resolution.Caller;
            log.LogInformation($"{nameof(SafeExchangeSecretMeta)} triggered for '{secretId}' by {caller.SubjectType} (tid {TelemetryContext.Current}) [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "post":
                    return await ActionResults.ServiceCallAsync(request, async () =>
                    {
                        var input = await ReadInputAsync<MetadataCreationInput>(request, secretId, log);
                        return await (await this.secretService.CreateAsync(caller, secretId, input)).ToResponseAsync(request);
                    }, "HandleSecretMetaCreation", log);

                case "get":
                    return await ActionResults.ServiceCallAsync(
                        request,
                        async () => await (await this.secretService.GetAsync(caller, secretId)).ToResponseAsync(request),
                        "HandleSecretMetaRead", log);

                case "patch":
                    return await ActionResults.ServiceCallAsync(request, async () =>
                    {
                        var input = await ReadInputAsync<MetadataUpdateInput>(request, secretId, log);
                        return await (await this.secretService.UpdateAsync(caller, secretId, input)).ToResponseAsync(request);
                    }, "HandleSecretMetaUpdate", log);

                case "delete":
                    return await ActionResults.ServiceCallAsync(
                        request,
                        async () => await (await this.secretService.DeleteAsync(caller, secretId)).ToResponseAsync(request),
                        "HandleSecretMetaDeletion", log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        public async Task<HttpResponseData> RunList(
            HttpRequestData request, ClaimsPrincipal principal, ILogger log)
        {
            var resolution = await this.callerResolver.ResolveAsync(principal, CallerChannels.Rest, null);
            if (!resolution.IsAllowed)
            {
                return await resolution.Denial.ToResponseAsync(request);
            }

            var caller = resolution.Caller;
            log.LogInformation($"{nameof(SafeExchangeSecretMeta)}-{nameof(RunList)} triggered by {caller.SubjectType} (tid {TelemetryContext.Current}) [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "get":
                    return await ActionResults.ServiceCallAsync(
                        request,
                        async () => await (await this.secretService.ListV2Async(caller, GetTagFilter(request))).ToResponseAsync(request),
                        "HandleListSecretMeta", log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        public async Task<HttpResponseData> RunListV3(
            HttpRequestData request, ClaimsPrincipal principal, ILogger log)
        {
            var resolution = await this.callerResolver.ResolveAsync(principal, CallerChannels.Rest, null);
            if (!resolution.IsAllowed)
            {
                return await resolution.Denial.ToResponseAsync(request);
            }

            var caller = resolution.Caller;
            log.LogInformation($"{nameof(SafeExchangeSecretMeta)}-{nameof(RunListV3)} triggered by {caller.SubjectType} (tid {TelemetryContext.Current}) [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "get":
                    return await ActionResults.ServiceCallAsync(
                        request,
                        async () => await (await this.secretService.ListAsync(caller, GetTagFilter(request))).ToResponseAsync(request),
                        "HandleListSecretMetaV3", log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        private static IReadOnlyList<string> GetTagFilter(HttpRequestData request)
        {
            var query = request.Url?.Query;
            if (string.IsNullOrEmpty(query))
            {
                return Array.Empty<string>();
            }

            return System.Web.HttpUtility.ParseQueryString(query).GetValues("tag") ?? Array.Empty<string>();
        }

        private static async Task<T?> ReadInputAsync<T>(HttpRequestData request, string secretId, ILogger log) where T : class
        {
            var requestBody = await new StreamReader(request.Body ?? Stream.Null).ReadToEndAsync();
            try
            {
                return DefaultJsonSerializer.Deserialize<T>(requestBody);
            }
            catch
            {
                log.LogInformation($"Could not parse input data for '{secretId}'.");
                return null;
            }
        }
    }
}
