/// <summary>
/// SafeExchangeAccessGiveUp
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Graph;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using System;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class SafeExchangeAccessGiveUp
    {
        private readonly SafeExchangeDbContext dbContext;
        private readonly ITokenHelper tokenHelper;
        private readonly GlobalFilters globalFilters;
        private readonly IPermissionsManager permissionsManager;
        private readonly IOrphanedSecretManager orphanedSecretManager;
        private readonly IOptionsMonitor<Features> features;

        public SafeExchangeAccessGiveUp(
            SafeExchangeDbContext dbContext,
            ITokenHelper tokenHelper,
            GlobalFilters globalFilters,
            IPermissionsManager permissionsManager,
            IOrphanedSecretManager orphanedSecretManager,
            IOptionsMonitor<Features> features)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
            this.orphanedSecretManager = orphanedSecretManager ?? throw new ArgumentNullException(nameof(orphanedSecretManager));
            this.features = features ?? throw new ArgumentNullException(nameof(features));
        }

        public async Task<HttpResponseData> Run(
            HttpRequestData request,
            string secretId,
            ClaimsPrincipal principal,
            ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            (SubjectType subjectType, string subjectId) = await SubjectHelper.GetSubjectInfoAsync(this.tokenHelper, principal, this.dbContext);
            if (SubjectType.Application.Equals(subjectType) && string.IsNullOrEmpty(subjectId))
            {
                return await ActionResults.ForbiddenAsync(request, "Application is not registered or disabled.");
            }

            log.LogInformation($"{nameof(SafeExchangeAccessGiveUp)} triggered for '{secretId}' by {subjectType} {subjectId}, [{request.Method}].");

            if (!this.features.CurrentValue.UseAccessGiveUp)
            {
                return request.CreateResponse(HttpStatusCode.NoContent);
            }

            var existingMetadata = await this.dbContext.Objects.FindAsync(secretId);
            if (existingMetadata == null)
            {
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.NotFound,
                    new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretId}' not exists" });
            }

            var hasAnyAccess = await this.permissionsManager.HasAnyAccessAsync(subjectType, subjectId, secretId);
            if (!hasAnyAccess)
            {
                var consentRequired = await this.permissionsManager.IsConsentRequiredAsync(subjectId);
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Forbidden,
                    ActionResults.InsufficientPermissions(PermissionType.Read, secretId,
                        consentRequired ? GraphDataProvider.ConsentRequiredSubStatus : string.Empty));
            }

            return request.Method.ToLower() switch
            {
                "get" => await this.PreviewAsync(request, existingMetadata.ObjectName, subjectType, subjectId, log),
                "delete" => await this.GiveUpAsync(request, existingMetadata.ObjectName, subjectType, subjectId, log),
                _ => await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" })
            };
        }

        private async Task<HttpResponseData> PreviewAsync(
            HttpRequestData request, string secretId, SubjectType subjectType, string subjectId, ILogger log)
            => await ActionResults.TryCatchAsync(request, async () =>
        {
            var directRow = await this.permissionsManager.GetSubjectPermissionsAsync(secretId, subjectType, subjectId);
            var hasDirectRow = directRow != null;

            var preview = await this.orphanedSecretManager.PreviewAsync(secretId, this.dbContext);

            var output = new GiveUpPreviewOutput
            {
                HasDirectRow = hasDirectRow,
                WouldOrphan = hasDirectRow && preview.WouldOrphan,
                CurrentExpireAt = preview.CurrentExpireAt,
                ProspectiveExpireAt = (hasDirectRow && preview.WouldOrphan) ? preview.ProspectiveExpireAt : null
            };

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<GiveUpPreviewOutput> { Status = "ok", Result = output });
        }, nameof(PreviewAsync), log);

        private async Task<HttpResponseData> GiveUpAsync(
            HttpRequestData request, string secretId, SubjectType subjectType, string subjectId, ILogger log)
            => await ActionResults.TryCatchAsync(request, async () =>
        {
            var directRow = await this.permissionsManager.GetSubjectPermissionsAsync(secretId, subjectType, subjectId);
            if (directRow == null)
            {
                log.LogInformation($"Subject {subjectType} '{subjectId}' attempted give-up on '{secretId}' but had no direct row.");
                return request.CreateResponse(HttpStatusCode.NoContent);
            }

            log.LogInformation($"Subject {subjectType} '{subjectId}' relinquished access to '{secretId}'.");
            this.dbContext.Permissions.Remove(directRow);

            var orphanResult = await this.orphanedSecretManager.ApplyOrphanRuleAsync(secretId, this.dbContext);
            await this.dbContext.SaveChangesAsync();

            var output = new GiveUpResultOutput
            {
                HadDirectRow = true,
                WasOrphaned = orphanResult.WasOrphaned,
                ExpireAt = orphanResult.ExpireAt
            };

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<GiveUpResultOutput> { Status = "ok", Result = output });
        }, nameof(GiveUpAsync), log);
    }
}
