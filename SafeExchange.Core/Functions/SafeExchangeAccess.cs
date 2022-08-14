/// <summary>
/// SafeExchangeAccess
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using System;
    using System.Security.Claims;
    using System.Text.Json;
    using System.Web.Http;

    public class SafeExchangeAccess
    {
        private static readonly string ConsentRequiredSubStatus = "consent_required";

        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        private readonly IPurger purger;

        private readonly IPermissionsManager permissionsManager;

        public SafeExchangeAccess(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, IPurger purger, IPermissionsManager permissionsManager)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.purger = purger ?? throw new ArgumentNullException(nameof(purger));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
        }

        public async Task<IActionResult> Run(
            HttpRequest request,
            string secretId, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResult ?? new EmptyResult();
            }

            var userUpn = this.tokenHelper.GetUpn(principal);
            log.LogInformation($"{nameof(SafeExchangeAccess)} triggered for '{secretId}' by {userUpn}, ID {this.tokenHelper.GetObjectId(principal)} [{request.Method}].");

            var existingMetadata = await this.dbContext.Objects.FindAsync(secretId);
            if (existingMetadata == null)
            {
                log.LogInformation($"Cannot handle permissions for secret '{secretId}', as it not exists.");
                return new NotFoundObjectResult(new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretId}' not exists" });
            }

            switch (request.Method.ToLower())
            {
                case "post":
                    {
                        if (!await this.permissionsManager.IsAuthorizedAsync(userUpn, secretId, PermissionType.GrantAccess))
                        {
                            var consentRequired = await this.permissionsManager.IsConsentRequiredAsync(userUpn);
                            return ActionResults.InsufficientPermissionsResult(PermissionType.GrantAccess, secretId, consentRequired ? ConsentRequiredSubStatus : string.Empty);
                        }

                        var userCanRevokeAccess = await this.permissionsManager.IsAuthorizedAsync(userUpn, secretId, PermissionType.RevokeAccess);
                        return await this.GrantAccessAsync(existingMetadata.ObjectName, request, userCanRevokeAccess, log);
                    }

                case "get":
                    {
                        if (!await this.permissionsManager.IsAuthorizedAsync(userUpn, secretId, PermissionType.Read))
                        {
                            var consentRequired = await this.permissionsManager.IsConsentRequiredAsync(userUpn);
                            return ActionResults.InsufficientPermissionsResult(PermissionType.Read, secretId, consentRequired ? ConsentRequiredSubStatus : string.Empty);
                        }

                        return await this.GetAccessListAsync(existingMetadata.ObjectName, log);
                    }

                case "delete":
                    {
                        if (!await this.permissionsManager.IsAuthorizedAsync(userUpn, secretId, PermissionType.RevokeAccess))
                        {
                            var consentRequired = await this.permissionsManager.IsConsentRequiredAsync(userUpn);
                            return ActionResults.InsufficientPermissionsResult(PermissionType.RevokeAccess, secretId, consentRequired ? ConsentRequiredSubStatus : string.Empty);
                        }

                        return await this.RevokeAccessAsync(existingMetadata.ObjectName, request, log);
                    }

                default:
                    return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Request method not recognized" });
            }
        }

        private async Task<IActionResult> GrantAccessAsync(string secretId, HttpRequest request, bool userCanRevokeAccess, ILogger log)
            => await TryCatch(async () =>
        {
            var permissionsInput = await this.TryGetPermissionsInputAsync(request, log);
            if ((permissionsInput?.Count ?? 0) == 0)
            {
                log.LogInformation($"Permissions data for '{secretId}' not provided.");
                return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Access settings are not provided." });
            }

            foreach (var permissionInput in permissionsInput ?? Array.Empty<SubjectPermissionsInput>().ToList())
            {
                var permission = permissionInput.GetPermissionType();
                if (!userCanRevokeAccess)
                {
                    permission &= ~PermissionType.RevokeAccess;
                }

                await this.permissionsManager.SetPermissionAsync(permissionInput.SubjectName, secretId, permission);
            }

            await this.dbContext.SaveChangesAsync();

            return new OkObjectResult(new BaseResponseObject<string> { Status = "ok", Result = "ok" });

        }, nameof(GrantAccessAsync), log);

        private async Task<IActionResult> GetAccessListAsync(string secretId, ILogger log)
            => await TryCatch(async () =>
        {
            var existingPermissions = await this.dbContext.Permissions.Where(p => p.SecretName.Equals(secretId)).ToListAsync();

            return new OkObjectResult(new BaseResponseObject<List<SubjectPermissionsOutput>>
            {
                Status = "ok",
                Result = existingPermissions.Select(p => p.ToDto()).ToList()
            });

        }, nameof(GetAccessListAsync), log);

        private async Task<IActionResult> RevokeAccessAsync(string secretId, HttpRequest request, ILogger log)
            => await TryCatch(async () =>
        {
            var permissionsInput = await this.TryGetPermissionsInputAsync(request, log);
            if ((permissionsInput?.Count ?? 0) == 0)
            {
                log.LogInformation($"Permissions data for '{secretId}' not provided.");
                return new BadRequestObjectResult(new BaseResponseObject<object> { Status = "error", Error = "Access settings are not provided." });
            }

            foreach (var permissionInput in permissionsInput ?? Array.Empty<SubjectPermissionsInput>().ToList())
            {
                await this.permissionsManager.UnsetPermissionAsync(permissionInput.SubjectName, secretId, permissionInput.GetPermissionType());
            }

            await this.dbContext.SaveChangesAsync();

            return new OkObjectResult(new BaseResponseObject<string> { Status = "ok", Result = "ok" });

        }, nameof(RevokeAccessAsync), log);

        private async Task<List<SubjectPermissionsInput>?> TryGetPermissionsInputAsync(HttpRequest request, ILogger log)
        {
            var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            try
            {
                return DefaultJsonSerializer.Deserialize<List<SubjectPermissionsInput>>(requestBody);
            }
            catch (Exception exception)
            {
                log.LogWarning(exception, "Could not parse input data for permissions input.");
                return null;
            }
        }

        private static async Task<IActionResult> TryCatch(Func<Task<IActionResult>> action, string actionName, ILogger log)
        {
            try
            {
                return await action();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, $"Exception in {actionName}: {ex.GetType()}: {ex.Message}");
                return new ExceptionResult(ex, true);
            }
        }
    }
}
