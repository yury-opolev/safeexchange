/// <summary>
/// SafeExchangeAdminS2SApp — admin-gated mirror of the self-service S2S app
/// detail + owners endpoints. Lets the admin panel reuse the same manage UX
/// (load detail, edit owner set, save) on apps where the admin is NOT an owner.
///
/// The registrar guard (RunReplaceOwners) is identical to the self-service
/// path — admin cannot strip the registrar either (per design decision). The
/// existing whole-app Delete and Toggle-Enabled admin endpoints remain unchanged.
/// </summary>

namespace SafeExchange.Core.Functions.Admin
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Applications;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class SafeExchangeAdminS2SApp
    {
        private readonly SafeExchangeDbContext dbContext;
        private readonly GlobalFilters globalFilters;
        private readonly IApplicationOwnerService ownerService;

        public SafeExchangeAdminS2SApp(
            SafeExchangeDbContext dbContext,
            GlobalFilters globalFilters,
            IApplicationOwnerService ownerService)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.ownerService = ownerService ?? throw new ArgumentNullException(nameof(ownerService));
        }

        /// <summary>GET /admin/applications/{displayName} — full detail incl. owners.</summary>
        public async Task<HttpResponseData> RunDetail(HttpRequestData request, string displayName, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetAdminFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var app = await this.dbContext.Applications.FirstOrDefaultAsync(a => a.DisplayName == displayName);
                if (app is null)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = $"Application '{displayName}' not found." });
                }

                var owners = await this.ownerService.ListOwnersAsync(app.Id);
                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<S2SAppOutput> { Status = "ok", Result = SafeExchangeS2SApps.ToDto(app, owners) });
            }, nameof(RunDetail), log);
        }

        /// <summary>PUT /admin/applications/{displayName}/owners — atomic owner-set reconcile.</summary>
        public async Task<HttpResponseData> RunReplaceOwners(HttpRequestData request, string displayName, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetAdminFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var app = await this.dbContext.Applications.FirstOrDefaultAsync(a => a.DisplayName == displayName);
                if (app is null)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = $"Application '{displayName}' not found." });
                }

                var body = await new StreamReader(request.Body).ReadToEndAsync();
                S2SAppReplaceOwnersInput? input;
                try
                {
                    input = DefaultJsonSerializer.Deserialize<S2SAppReplaceOwnersInput>(body);
                }
                catch
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Body is not valid JSON." });
                }

                if (input is null || input.Owners is null)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Owner list is required." });
                }

                var desired = input.Owners
                    .Where(o => o is not null && !string.IsNullOrWhiteSpace(o.SubjectId))
                    .Select(o => new ApplicationOwner(app.Id, o.SubjectType, o.SubjectId, addedBy: "admin", subjectName: o.SubjectName ?? string.Empty))
                    .ToList();

                if (!SafeExchangeS2SApps.HasRegistrarOwner(app, desired))
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.Conflict,
                        new BaseResponseObject<object> { Status = "conflict", Error = SafeExchangeS2SApps.RegistrarProtectionMessage });
                }

                try
                {
                    await this.ownerService.ReplaceOwnersAsync(app.Id, desired);
                }
                catch (ApplicationOwnerInvariantException ex)
                {
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.Conflict,
                        new BaseResponseObject<object> { Status = "conflict", Error = ex.Message });
                }

                log.LogInformation("Admin replaced owner set on app '{App}' (final count {Count}).",
                    app.DisplayName, desired.Count);

                var owners = await this.ownerService.ListOwnersAsync(app.Id);
                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<S2SAppOutput> { Status = "ok", Result = SafeExchangeS2SApps.ToDto(app, owners) });
            }, nameof(RunReplaceOwners), log);
        }
    }
}
