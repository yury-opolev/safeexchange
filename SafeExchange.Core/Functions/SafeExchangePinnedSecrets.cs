/// <summary>
/// SafeExchangePinnedSecrets
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Middleware;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Utilities;
    using SafeExchange.Core.Telemetry;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class SafeExchangePinnedSecrets
    {
        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        private readonly IPermissionsManager permissionsManager;

        private readonly PinnedSecretsConfiguration config;

        public SafeExchangePinnedSecrets(
            SafeExchangeDbContext dbContext,
            ITokenHelper tokenHelper,
            GlobalFilters globalFilters,
            IPermissionsManager permissionsManager,
            IOptions<PinnedSecretsConfiguration> config)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
            this.config = config?.Value ?? throw new ArgumentNullException(nameof(config));
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
            if (SubjectType.Application.Equals(subjectType))
            {
                return await ActionResults.ForbiddenAsync(request, "Applications cannot use this API.");
            }

            if (string.IsNullOrEmpty(secretId))
            {
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object> { Status = "error", Error = "Secret id value is not provided." });
            }

            log.LogInformation($"{nameof(SafeExchangePinnedSecrets)} triggered for '{secretId}' by {subjectType} (tid {TelemetryContext.Current}), [{request.Method}].");

            var userId = request.FunctionContext.GetUserId();

            switch (request.Method.ToLower())
            {
                case "put":
                    return await this.HandlePin(request, secretId, userId, subjectType, subjectId, log);

                case "get":
                    return await this.HandleGetPin(request, secretId, userId, subjectType, subjectId, log);

                case "delete":
                    return await this.HandleUnpin(request, secretId, userId, log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = $"Request method '{request.Method}' not allowed." });
            }
        }

        private async Task<HttpResponseData> HandlePin(
            HttpRequestData request, string secretId, string userId,
            SubjectType subjectType, string subjectId, ILogger log)
            => await ActionResults.TryCatchAsync(request, async () =>
        {
            var metadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
            if (metadata is null)
            {
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.NotFound,
                    new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretId}' does not exist." });
            }

            if (!await this.permissionsManager.IsAuthorizedAsync(subjectType, subjectId, secretId, PermissionType.Read))
            {
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Forbidden,
                    ActionResults.InsufficientPermissions(PermissionType.Read, secretId, string.Empty));
            }

            var existing = await this.dbContext.PinnedSecrets
                .FirstOrDefaultAsync(p => p.UserId.Equals(userId) && p.SecretName.Equals(secretId));
            if (existing is not null)
            {
                log.LogInformation($"User '{userId}' attempted to pin secret '{secretId}' but pin already exists.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK,
                    new BaseResponseObject<PinnedSecretOutput>
                    {
                        Status = "ok",
                        Result = await this.BuildDtoAsync(secretId, userId, subjectType, subjectId)
                    });
            }

            var count = await this.dbContext.PinnedSecrets.Where(p => p.UserId.Equals(userId)).CountAsync();
            if (count >= this.config.MaxPinnedSecretsPerUser)
            {
                log.LogInformation($"User '{userId}' has {count} pinned secrets, which is >= max. allowed {this.config.MaxPinnedSecretsPerUser}.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.BadRequest,
                    new BaseResponseObject<object>
                    {
                        Status = "error",
                        Error = $"Pinned secret count is {count}, which is higher or equal than allowed no. of {this.config.MaxPinnedSecretsPerUser} pinned secrets. Please unpin secrets before adding new ones."
                    });
            }

            var pin = await DbUtils.TryAddOrGetEntityAsync(
                async () =>
                {
                    var entity = await this.dbContext.PinnedSecrets.AddAsync(new PinnedSecret(userId, secretId));
                    await this.dbContext.SaveChangesAsync();
                    return entity.Entity;
                },
                async () =>
                {
                    return await this.dbContext.PinnedSecrets.FirstAsync(p => p.UserId.Equals(userId) && p.SecretName.Equals(secretId));
                },
                log);

            log.LogInformation($"User '{userId}' pinned secret '{secretId}'.");

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<PinnedSecretOutput>
                {
                    Status = "ok",
                    Result = await this.BuildDtoAsync(secretId, userId, subjectType, subjectId)
                });

        }, nameof(HandlePin), log);

        private async Task<HttpResponseData> HandleGetPin(
            HttpRequestData request, string secretId, string userId,
            SubjectType subjectType, string subjectId, ILogger log)
            => await ActionResults.TryCatchAsync(request, async () =>
        {
            var existing = await this.dbContext.PinnedSecrets
                .FirstOrDefaultAsync(p => p.UserId.Equals(userId) && p.SecretName.Equals(secretId));
            if (existing is null)
            {
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK,
                    new BaseResponseObject<PinnedSecretOutput> { Status = "no_content", Result = null });
            }

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<PinnedSecretOutput>
                {
                    Status = "ok",
                    Result = await this.BuildDtoAsync(secretId, userId, subjectType, subjectId)
                });

        }, nameof(HandleGetPin), log);

        private async Task<HttpResponseData> HandleUnpin(
            HttpRequestData request, string secretId, string userId, ILogger log)
            => await ActionResults.TryCatchAsync(request, async () =>
        {
            var existing = await this.dbContext.PinnedSecrets
                .FirstOrDefaultAsync(p => p.UserId.Equals(userId) && p.SecretName.Equals(secretId));
            if (existing is null)
            {
                log.LogInformation($"User '{userId}' attempted to unpin secret '{secretId}' but pin does not exist.");
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK,
                    new BaseResponseObject<string>
                    {
                        Status = "no_content",
                        Result = $"Pin for secret '{secretId}' does not exist."
                    });
            }

            this.dbContext.PinnedSecrets.Remove(existing);
            await this.dbContext.SaveChangesAsync();

            log.LogInformation($"User '{userId}' unpinned secret '{secretId}'.");

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<string> { Status = "ok", Result = "ok" });

        }, nameof(HandleUnpin), log);

        private async Task<PinnedSecretOutput> BuildDtoAsync(
            string secretName, string userId, SubjectType subjectType, string subjectId)
        {
            var dto = new PinnedSecretOutput { SecretName = secretName };

            var metadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretName));
            dto.Exists = metadata is not null;

            var permission = await this.dbContext.Permissions
                .FirstOrDefaultAsync(p => p.SecretName.Equals(secretName)
                                       && p.SubjectType.Equals(subjectType)
                                       && p.SubjectId.Equals(subjectId));
            if (permission is not null)
            {
                dto.CanRead = permission.CanRead;
                dto.CanWrite = permission.CanWrite;
                dto.CanGrantAccess = permission.CanGrantAccess;
                dto.CanRevokeAccess = permission.CanRevokeAccess;
            }

            if (dto.Exists && dto.CanRead)
            {
                dto.Tags = metadata.Tags?.ToList() ?? new List<string>();
            }
            else
            {
                dto.Tags = new List<string>();
            }

            return dto;
        }
    }
}
