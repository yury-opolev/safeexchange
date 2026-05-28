/// <summary>
/// SafeS2SApps — HTTP trigger entry points for the self-service S2S apps surface.
/// Two routes for now (POST /s2sapps and GET /s2sapps/mine); follow-up commits
/// add the remaining verbs.
/// </summary>

namespace SafeExchange.Functions
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core;
    using SafeExchange.Core.Applications;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using System;
    using System.Threading.Tasks;

    public class SafeS2SApps
    {
        private const string Version = "v2";

        private readonly SafeExchangeS2SApps handler;
        private readonly ILogger log;

        public SafeS2SApps(
            SafeExchangeDbContext dbContext,
            ITokenHelper tokenHelper,
            GlobalFilters globalFilters,
            IApplicationOwnerService ownerService,
            IOptionsMonitor<Features> features,
            IOptionsMonitor<Limits> limits,
            ILogger<SafeS2SApps> log)
        {
            this.handler = new SafeExchangeS2SApps(dbContext, tokenHelper, globalFilters, ownerService, features, limits);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        [Function("SafeExchange-S2SApps-Register")]
        public async Task<HttpResponseData> RunRegister(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = $"{Version}/s2sapps")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunRegister(request, principal, this.log);
        }

        [Function("SafeExchange-S2SApps-Mine")]
        public async Task<HttpResponseData> RunListMine(
            // Distinct route so it can't collide with /s2sapps/{displayName} —
            // Azure Functions doesn't always prefer literal segments over
            // parameterised ones when both routes are equally specific.
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/me/s2sapps")]
            HttpRequestData request)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunListMine(request, principal, this.log);
        }

        [Function("SafeExchange-S2SApps-Detail")]
        public async Task<HttpResponseData> RunDetail(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/s2sapps/{{displayName}}")]
            HttpRequestData request,
            string displayName)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunDetail(request, displayName, principal, this.log);
        }

        [Function("SafeExchange-S2SApps-Delete")]
        public async Task<HttpResponseData> RunDelete(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = $"{Version}/s2sapps/{{displayName}}")]
            HttpRequestData request,
            string displayName)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunDelete(request, displayName, principal, this.log);
        }

        [Function("SafeExchange-S2SApps-ReplaceOwners")]
        public async Task<HttpResponseData> RunReplaceOwners(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = $"{Version}/s2sapps/{{displayName}}/owners")]
            HttpRequestData request,
            string displayName)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunReplaceOwners(request, displayName, principal, this.log);
        }

        [Function("SafeExchange-S2SApps-ToggleEnabled")]
        public async Task<HttpResponseData> RunToggleEnabled(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = $"{Version}/s2sapps/{{displayName}}/enabled")]
            HttpRequestData request,
            string displayName)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunToggleEnabled(request, displayName, principal, this.log);
        }

        [Function("SafeExchange-S2SApps-ListOwners")]
        public async Task<HttpResponseData> RunListOwners(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{Version}/s2sapps/{{displayName}}/owners")]
            HttpRequestData request,
            string displayName)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunListOwners(request, displayName, principal, this.log);
        }

        [Function("SafeExchange-S2SApps-AddOwner")]
        public async Task<HttpResponseData> RunAddOwner(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = $"{Version}/s2sapps/{{displayName}}/owners")]
            HttpRequestData request,
            string displayName)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunAddOwner(request, displayName, principal, this.log);
        }

        [Function("SafeExchange-S2SApps-RemoveOwner")]
        public async Task<HttpResponseData> RunRemoveOwner(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = $"{Version}/s2sapps/{{displayName}}/owners/{{subjectType}}/{{subjectId}}")]
            HttpRequestData request,
            string displayName,
            string subjectType,
            string subjectId)
        {
            var principal = request.FunctionContext.GetPrincipal();
            return await this.handler.RunRemoveOwner(request, displayName, subjectType, subjectId, principal, this.log);
        }
    }
}
