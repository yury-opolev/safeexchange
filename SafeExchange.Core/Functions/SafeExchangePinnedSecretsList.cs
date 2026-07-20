/// <summary>
/// SafeExchangePinnedSecretsList
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
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

    public class SafeExchangePinnedSecretsList
    {
        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        private readonly IPermissionsManager permissionsManager;

        public SafeExchangePinnedSecretsList(
            SafeExchangeDbContext dbContext,
            ITokenHelper tokenHelper,
            GlobalFilters globalFilters,
            IPermissionsManager permissionsManager)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
        }

        public async Task<HttpResponseData> RunList(
            HttpRequestData request, ClaimsPrincipal principal, ILogger log, bool effective = false)
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

            log.LogInformation($"{nameof(SafeExchangePinnedSecretsList)} triggered by {subjectType} (tid {TelemetryContext.Current}), [{request.Method}].");

            var userId = request.FunctionContext.GetUserId();
            switch (request.Method.ToLower())
            {
                case "get":
                    return effective
                        ? await this.HandleListV3(request, userId, subjectType, subjectId, log)
                        : await this.HandleList(request, userId, subjectType, subjectId, log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = $"Request method '{request.Method}' not allowed." });
            }
        }

        private async Task<HttpResponseData> HandleList(
            HttpRequestData request, string userId,
            SubjectType subjectType, string subjectId, ILogger log)
            => await ActionResults.TryCatchAsync(request, async () =>
        {
            var pins = await this.dbContext.PinnedSecrets
                .Where(p => p.UserId.Equals(userId))
                .ToListAsync();

            if (pins.Count == 0)
            {
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK,
                    new BaseResponseObject<List<PinnedSecretOutput>>
                    {
                        Status = "no_content",
                        Result = new List<PinnedSecretOutput>()
                    });
            }

            // Sort DESC in memory (Cosmos OrderBy on non-indexed property can be costly;
            // small N here makes this trivially cheap).
            pins = pins.OrderByDescending(p => p.CreatedAt).ToList();

            var names = pins.Select(p => p.SecretName).Distinct().ToList();

            var metadataByName = (await this.dbContext.Objects
                    .Where(o => names.Contains(o.ObjectName))
                    .ToListAsync())
                .ToDictionary(o => o.ObjectName, o => o);

            var permsByName = (await this.dbContext.Permissions
                    .Where(p => names.Contains(p.SecretName)
                             && p.SubjectType.Equals(subjectType)
                             && p.SubjectId.Equals(subjectId))
                    .ToListAsync())
                .ToDictionary(p => p.SecretName, p => p);

            var result = new List<PinnedSecretOutput>(pins.Count);
            foreach (var pin in pins)
            {
                metadataByName.TryGetValue(pin.SecretName, out var meta);
                permsByName.TryGetValue(pin.SecretName, out var perm);

                var dto = new PinnedSecretOutput
                {
                    SecretName = pin.SecretName,
                    Exists = meta is not null,
                };

                if (perm is not null)
                {
                    dto.CanRead = perm.CanRead;
                    dto.CanWrite = perm.CanWrite;
                    dto.CanGrantAccess = perm.CanGrantAccess;
                    dto.CanRevokeAccess = perm.CanRevokeAccess;
                }

                if (dto.Exists && dto.CanRead)
                {
                    dto.Tags = meta!.Tags?.ToList() ?? new List<string>();
                }

                result.Add(dto);
            }

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<List<PinnedSecretOutput>> { Status = "ok", Result = result });

        }, nameof(HandleList), log);

        private async Task<HttpResponseData> HandleListV3(
            HttpRequestData request, string userId,
            SubjectType subjectType, string subjectId, ILogger log)
            => await ActionResults.TryCatchAsync(request, async () =>
        {
            var pins = await this.dbContext.PinnedSecrets
                .Where(p => p.UserId.Equals(userId))
                .ToListAsync();

            if (pins.Count == 0)
            {
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK,
                    new BaseResponseObject<List<PinnedSecretListItemOutput>>
                    {
                        Status = "no_content",
                        Result = new List<PinnedSecretListItemOutput>()
                    });
            }

            pins = pins.OrderByDescending(p => p.CreatedAt).ToList();

            var names = pins.Select(p => p.SecretName).Distinct().ToList();

            var metadataByName = (await this.dbContext.Objects
                    .Where(o => names.Contains(o.ObjectName))
                    .ToListAsync())
                .ToDictionary(o => o.ObjectName, o => o);

            var directByName = (await this.dbContext.Permissions
                    .Where(p => names.Contains(p.SecretName)
                             && p.SubjectType.Equals(subjectType)
                             && p.SubjectId.Equals(subjectId))
                    .ToListAsync())
                .ToDictionary(p => p.SecretName, p => p);

            // One batched calculation for all pins (bounded by the pin cap) instead of a
            // per-pin user + group + permission query cascade.
            var effectiveByName = await this.permissionsManager.GetEffectivePermissionsAsync(subjectType, subjectId, names);

            var result = new List<PinnedSecretListItemOutput>(pins.Count);
            foreach (var pin in pins)
            {
                metadataByName.TryGetValue(pin.SecretName, out var meta);
                directByName.TryGetValue(pin.SecretName, out var direct);

                var callerEffective = EffectivePermissionsOutput.FromPermissionType(effectiveByName[pin.SecretName]);

                var dto = new PinnedSecretListItemOutput
                {
                    SecretName = pin.SecretName,
                    Exists = meta is not null,
                    CanRead = direct?.CanRead ?? false,
                    CanWrite = direct?.CanWrite ?? false,
                    CanGrantAccess = direct?.CanGrantAccess ?? false,
                    CanRevokeAccess = direct?.CanRevokeAccess ?? false,
                    CallerEffectivePermissions = callerEffective,
                };

                if (dto.Exists && callerEffective.CanRead)
                {
                    dto.Tags = meta!.Tags?.ToList() ?? new List<string>();
                }

                result.Add(dto);
            }

            return await ActionResults.CreateResponseAsync(
                request, HttpStatusCode.OK,
                new BaseResponseObject<List<PinnedSecretListItemOutput>> { Status = "ok", Result = result });

        }, nameof(HandleListV3), log);
    }
}
