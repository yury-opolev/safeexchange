/// <summary>
/// SafeExchangeS2SApps — self-service endpoints for end users to register and
/// manage their own S2S applications. Regular auth gate (NOT admin) — auth and
/// ownership together decide what the caller can see and do.
///
/// Scope of this commit (Phase A Task A4): register (POST /s2sapps) and
/// list-mine (GET /s2sapps/mine). Detail / update / delete / owner mgmt live
/// in subsequent commits — see docs/SPIKE-s2s-apps-PLAN.md.
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core.Applications;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Utilities;
    using System;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Text.RegularExpressions;

    public class SafeExchangeS2SApps
    {
        private static readonly string GuidRegex = "^([0-9A-Fa-f]{8}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{12})$";

        private readonly SafeExchangeDbContext dbContext;
        private readonly ITokenHelper tokenHelper;
        private readonly GlobalFilters globalFilters;
        private readonly IApplicationOwnerService ownerService;
        private readonly IOptionsMonitor<Features> features;
        private readonly IOptionsMonitor<Limits> limits;

        public SafeExchangeS2SApps(
            SafeExchangeDbContext dbContext,
            ITokenHelper tokenHelper,
            GlobalFilters globalFilters,
            IApplicationOwnerService ownerService,
            IOptionsMonitor<Features> features,
            IOptionsMonitor<Limits> limits)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.ownerService = ownerService ?? throw new ArgumentNullException(nameof(ownerService));
            this.features = features ?? throw new ArgumentNullException(nameof(features));
            this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        }

        private string RegistrarCreatedBy(string upn) => $"User {upn}";

        /// <summary>POST /s2sapps — self-service register.</summary>
        public async Task<HttpResponseData> RunRegister(HttpRequestData request, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            if (!this.features.CurrentValue.S2SAppsSelfService)
            {
                // Same shape as the give-up flag's gated response — clients
                // already know how to hide UI based on a 204.
                return request.CreateResponse(HttpStatusCode.NoContent);
            }

            (SubjectType subjectType, string subjectId) = await SubjectHelper.GetSubjectInfoAsync(this.tokenHelper, principal, this.dbContext);
            if (subjectType != SubjectType.User || string.IsNullOrEmpty(subjectId))
            {
                return await ActionResults.ForbiddenAsync(request, "Only authenticated users may self-register applications.");
            }

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var body = await new StreamReader(request.Body).ReadToEndAsync();
                S2SAppRegistrationInput? input;
                try
                {
                    input = DefaultJsonSerializer.Deserialize<S2SAppRegistrationInput>(body);
                }
                catch
                {
                    return await BadRequestAsync(request, "Registration body is not valid JSON.");
                }

                if (input is null
                    || string.IsNullOrWhiteSpace(input.DisplayName)
                    || string.IsNullOrWhiteSpace(input.AadClientId))
                {
                    return await BadRequestAsync(request, "DisplayName and AadClientId are required.");
                }

                if (!S2SAppDisplayNameValidator.TryValidate(input.DisplayName, out var nameError))
                {
                    return await BadRequestAsync(request, nameError!);
                }

                if (!Regex.IsMatch(input.AadClientId, GuidRegex))
                {
                    return await BadRequestAsync(request, "AadClientId must be a GUID.");
                }

                var tenantId = string.IsNullOrWhiteSpace(input.AadTenantId)
                    ? this.tokenHelper.GetTenantId(principal)
                    : input.AadTenantId;
                if (string.IsNullOrWhiteSpace(tenantId) || !Regex.IsMatch(tenantId, GuidRegex))
                {
                    return await BadRequestAsync(request, "AadTenantId is missing or not a GUID; pass it explicitly if the caller's token does not carry one.");
                }

                var contactEmail = string.IsNullOrWhiteSpace(input.ContactEmail) ? subjectId : input.ContactEmail;

                // Per-registrar cap. Counts apps where the caller is the *registrar*
                // (Application.CreatedBy starts with their UPN); does NOT count apps
                // where they were merely added as a co-owner. Cap=0 disables the
                // check. See docs/SPIKE-s2s-apps.md and Limits.MaxAppsPerRegistrar.
                var maxRegistered = this.limits.CurrentValue.MaxAppsPerRegistrar;
                if (maxRegistered > 0)
                {
                    var createdBy = this.RegistrarCreatedBy(subjectId);
                    var existingRegistered = await this.dbContext.Applications.CountAsync(a => a.CreatedBy == createdBy);
                    if (existingRegistered >= maxRegistered)
                    {
                        return await ConflictAsync(request,
                            $"You have already registered {existingRegistered} apps (max {maxRegistered}). " +
                            "Co-ownership is unlimited — ask another owner to register the new app and add you as a co-owner, or have an admin raise the cap.");
                    }
                }

                // Uniqueness: display name (global) and client id (global).
                // CountAsync(...) > 0 instead of AnyAsync(predicate) — the latter generates
                // SELECT VALUE EXISTS (SELECT 1 FROM root c WHERE ...) which the Cosmos
                // emulator can't parse; CountAsync emits SELECT VALUE COUNT(1) which it
                // does support. Real Cosmos handles both.
                var nameTaken = await this.dbContext.Applications.CountAsync(a => a.DisplayName == input.DisplayName) > 0;
                if (nameTaken)
                {
                    return await ConflictAsync(request, $"Application '{input.DisplayName}' is already registered.");
                }
                // Client-id uniqueness is enforced on the (tenantId, clientId) pair —
                // the natural domain key matching SubjectHelper.GetApplicationDisplayNameAsync.
                // Multi-tenant Entra apps legitimately use the same clientId across
                // multiple tenants (different `tid` claims), and we want both to be
                // registrable as distinct subjects.
                var clientPairTaken = await this.dbContext.Applications.CountAsync(
                    a => a.AadTenantId == tenantId && a.AadClientId == input.AadClientId) > 0;
                if (clientPairTaken)
                {
                    return await ConflictAsync(request, $"An application with tenant/client id pair ({tenantId}, {input.AadClientId}) is already registered.");
                }

                // Validate the invariant up-front against the proposed owner set
                // (caller + additional). Cheaper than creating the app then
                // rolling back when the invariant fails.
                var proposed = new List<ApplicationOwner>
                {
                    new ApplicationOwner("pending", OwnerSubjectType.User, subjectId, addedBy: subjectId),
                };
                foreach (var ao in input.AdditionalOwners ?? new List<S2SAppOwnerInput>())
                {
                    if (string.IsNullOrWhiteSpace(ao.SubjectId))
                    {
                        return await BadRequestAsync(request, "Each additional owner must have a non-empty SubjectId.");
                    }
                    proposed.Add(new ApplicationOwner("pending", ao.SubjectType, ao.SubjectId, addedBy: subjectId));
                }
                try
                {
                    this.ownerService.ValidateInvariant(proposed);
                }
                catch (ApplicationOwnerInvariantException ex)
                {
                    return await ConflictAsync(request, ex.Message);
                }

                // Materialise the Application.
                var registration = new ApplicationRegistrationInput
                {
                    AadClientId = input.AadClientId,
                    AadTenantId = tenantId,
                    ContactEmail = contactEmail,
                    Enabled = true,
                    ExternalNotificationsReader = false,
                };
                var app = new Application(input.DisplayName, registration, createdBy: this.RegistrarCreatedBy(subjectId));
                await this.dbContext.Applications.AddAsync(app);
                await this.dbContext.SaveChangesAsync();

                // Owner #1 (caller) + additional owners.
                await this.ownerService.AddOwnerAsync(app.Id, OwnerSubjectType.User, subjectId, addedBy: subjectId);
                foreach (var ao in input.AdditionalOwners ?? new List<S2SAppOwnerInput>())
                {
                    await this.ownerService.AddOwnerAsync(app.Id, ao.SubjectType, ao.SubjectId, addedBy: subjectId);
                }

                log.LogInformation("S2S app '{App}' registered by User {Subject} with {OwnerCount} owners.",
                    app.DisplayName, subjectId, 1 + (input.AdditionalOwners?.Count ?? 0));

                var owners = await this.ownerService.ListOwnersAsync(app.Id);
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Created,
                    new BaseResponseObject<S2SAppOutput> { Status = "ok", Result = ToDto(app, owners) });
            }, nameof(RunRegister), log);
        }

        /// <summary>GET /s2sapps/mine — apps where the caller is a direct user-owner.</summary>
        public async Task<HttpResponseData> RunListMine(HttpRequestData request, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            if (!this.features.CurrentValue.S2SAppsSelfService)
            {
                return request.CreateResponse(HttpStatusCode.NoContent);
            }

            (SubjectType subjectType, string subjectId) = await SubjectHelper.GetSubjectInfoAsync(this.tokenHelper, principal, this.dbContext);
            if (subjectType != SubjectType.User || string.IsNullOrEmpty(subjectId))
            {
                return await ActionResults.ForbiddenAsync(request, "Only authenticated users may list their applications.");
            }

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var owned = await this.ownerService.ListAppsOwnedByUserAsync(subjectId);

                var registrarCreatedBy = this.RegistrarCreatedBy(subjectId);
                var overviews = owned.Select(a => new S2SAppOverviewOutput
                {
                    DisplayName = a.DisplayName,
                    AadClientId = a.AadClientId,
                    Enabled = a.Enabled,
                    OwnerCount = -1, // populated below if needed; left at -1 to avoid an N+1 in the list view
                    IsRegistrar = string.Equals(a.CreatedBy, registrarCreatedBy, StringComparison.OrdinalIgnoreCase),
                }).ToList();

                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK,
                    new BaseResponseObject<List<S2SAppOverviewOutput>> { Status = "ok", Result = overviews });
            }, nameof(RunListMine), log);
        }

        private static S2SAppOutput ToDto(Application app, IReadOnlyCollection<ApplicationOwner> owners) => new()
        {
            DisplayName = app.DisplayName,
            AadTenantId = app.AadTenantId,
            AadClientId = app.AadClientId,
            ContactEmail = app.ContactEmail,
            Enabled = app.Enabled,
            CreatedAt = app.CreatedAt,
            Owners = owners.Select(o => new S2SAppOwnerOutput
            {
                SubjectType = o.SubjectType,
                SubjectId = o.SubjectId,
                AddedAt = o.AddedAt,
            }).ToList(),
        };

        private static Task<HttpResponseData> BadRequestAsync(HttpRequestData request, string message)
            => ActionResults.CreateResponseAsync(request, HttpStatusCode.BadRequest,
                new BaseResponseObject<object> { Status = "error", Error = message });

        private static Task<HttpResponseData> ConflictAsync(HttpRequestData request, string message)
            => ActionResults.CreateResponseAsync(request, HttpStatusCode.Conflict,
                new BaseResponseObject<object> { Status = "conflict", Error = message });

        private static Task<HttpResponseData> NotFoundAsync(HttpRequestData request, string message)
            => ActionResults.CreateResponseAsync(request, HttpStatusCode.NotFound,
                new BaseResponseObject<object> { Status = "not_found", Error = message });

        /// <summary>GET /s2sapps/{name} — detail (caller must be a direct user owner).</summary>
        public async Task<HttpResponseData> RunDetail(HttpRequestData request, string displayName, ClaimsPrincipal principal, ILogger log)
        {
            var gate = await this.GateAsync(request, principal);
            if (gate.shouldReturn) return gate.response!;

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var app = await this.dbContext.Applications.FirstOrDefaultAsync(a => a.DisplayName == displayName);
                if (app is null) return await NotFoundAsync(request, $"Application '{displayName}' not found.");
                if (!await this.ownerService.IsOwnerAsync(app.Id, OwnerSubjectType.User, gate.callerUpn))
                    return await ActionResults.ForbiddenAsync(request, "Only owners may view this application.");

                var owners = await this.ownerService.ListOwnersAsync(app.Id);
                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<S2SAppOutput> { Status = "ok", Result = ToDto(app, owners) });
            }, nameof(RunDetail), log);
        }

        /// <summary>DELETE /s2sapps/{name} — owner-only; cascades owner rows.</summary>
        public async Task<HttpResponseData> RunDelete(HttpRequestData request, string displayName, ClaimsPrincipal principal, ILogger log)
        {
            var gate = await this.GateAsync(request, principal);
            if (gate.shouldReturn) return gate.response!;

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var app = await this.dbContext.Applications.FirstOrDefaultAsync(a => a.DisplayName == displayName);
                if (app is null)
                {
                    // Idempotent delete — already gone is success.
                    return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                        new BaseResponseObject<string> { Status = "no_content", Result = "already absent" });
                }
                if (!await this.ownerService.IsOwnerAsync(app.Id, OwnerSubjectType.User, gate.callerUpn))
                    return await ActionResults.ForbiddenAsync(request, "Only owners may delete this application.");

                var owners = await this.dbContext.ApplicationOwners.Where(o => o.ApplicationId == app.Id).ToListAsync();
                this.dbContext.ApplicationOwners.RemoveRange(owners);
                this.dbContext.Applications.Remove(app);
                await this.dbContext.SaveChangesAsync();

                log.LogInformation("S2S app '{App}' deleted by {Upn} ({OwnerCount} owners cascaded).",
                    app.DisplayName, gate.callerUpn, owners.Count);
                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<string> { Status = "ok", Result = "deleted" });
            }, nameof(RunDelete), log);
        }

        /// <summary>GET /s2sapps/{name}/owners — owner-only.</summary>
        public async Task<HttpResponseData> RunListOwners(HttpRequestData request, string displayName, ClaimsPrincipal principal, ILogger log)
        {
            var gate = await this.GateAsync(request, principal);
            if (gate.shouldReturn) return gate.response!;

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var app = await this.dbContext.Applications.FirstOrDefaultAsync(a => a.DisplayName == displayName);
                if (app is null) return await NotFoundAsync(request, $"Application '{displayName}' not found.");
                if (!await this.ownerService.IsOwnerAsync(app.Id, OwnerSubjectType.User, gate.callerUpn))
                    return await ActionResults.ForbiddenAsync(request, "Only owners may view ownership.");

                var owners = await this.ownerService.ListOwnersAsync(app.Id);
                var dto = owners.Select(o => new S2SAppOwnerOutput
                {
                    SubjectType = o.SubjectType, SubjectId = o.SubjectId, AddedAt = o.AddedAt,
                }).ToList();
                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<List<S2SAppOwnerOutput>> { Status = "ok", Result = dto });
            }, nameof(RunListOwners), log);
        }

        /// <summary>POST /s2sapps/{name}/owners — owner-only; idempotent.</summary>
        public async Task<HttpResponseData> RunAddOwner(HttpRequestData request, string displayName, ClaimsPrincipal principal, ILogger log)
        {
            var gate = await this.GateAsync(request, principal);
            if (gate.shouldReturn) return gate.response!;

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var app = await this.dbContext.Applications.FirstOrDefaultAsync(a => a.DisplayName == displayName);
                if (app is null) return await NotFoundAsync(request, $"Application '{displayName}' not found.");
                if (!await this.ownerService.IsOwnerAsync(app.Id, OwnerSubjectType.User, gate.callerUpn))
                    return await ActionResults.ForbiddenAsync(request, "Only owners may add owners.");

                var body = await new StreamReader(request.Body).ReadToEndAsync();
                S2SAppOwnerInput? input;
                try { input = DefaultJsonSerializer.Deserialize<S2SAppOwnerInput>(body); }
                catch { return await BadRequestAsync(request, "Body is not valid JSON."); }

                if (input is null || string.IsNullOrWhiteSpace(input.SubjectId))
                    return await BadRequestAsync(request, "SubjectId is required.");

                await this.ownerService.AddOwnerAsync(app.Id, input.SubjectType, input.SubjectId, addedBy: gate.callerUpn);
                var owners = await this.ownerService.ListOwnersAsync(app.Id);
                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<S2SAppOutput> { Status = "ok", Result = ToDto(app, owners) });
            }, nameof(RunAddOwner), log);
        }

        /// <summary>DELETE /s2sapps/{name}/owners/{type}/{principal} — owner-only; refuses if invariant would break.</summary>
        public async Task<HttpResponseData> RunRemoveOwner(HttpRequestData request, string displayName, string subjectTypeString, string subjectId, ClaimsPrincipal principal, ILogger log)
        {
            var gate = await this.GateAsync(request, principal);
            if (gate.shouldReturn) return gate.response!;

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                if (!Enum.TryParse<OwnerSubjectType>(subjectTypeString, ignoreCase: true, out var subjectType))
                    return await BadRequestAsync(request, "Subject type must be 'User' or 'Group'.");

                var app = await this.dbContext.Applications.FirstOrDefaultAsync(a => a.DisplayName == displayName);
                if (app is null) return await NotFoundAsync(request, $"Application '{displayName}' not found.");
                if (!await this.ownerService.IsOwnerAsync(app.Id, OwnerSubjectType.User, gate.callerUpn))
                    return await ActionResults.ForbiddenAsync(request, "Only owners may remove owners.");

                try
                {
                    await this.ownerService.RemoveOwnerAsync(app.Id, subjectType, subjectId);
                }
                catch (ApplicationOwnerInvariantException ex)
                {
                    return await ConflictAsync(request, ex.Message);
                }

                var owners = await this.ownerService.ListOwnersAsync(app.Id);
                return await ActionResults.CreateResponseAsync(request, HttpStatusCode.OK,
                    new BaseResponseObject<S2SAppOutput> { Status = "ok", Result = ToDto(app, owners) });
            }, nameof(RunRemoveOwner), log);
        }

        // Common gate: global filters, feature flag, must be authenticated user.
        // Returns the caller's UPN on success.
        private async Task<(bool shouldReturn, HttpResponseData? response, string callerUpn)> GateAsync(HttpRequestData request, ClaimsPrincipal principal)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return (true, filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent), string.Empty);
            }
            if (!this.features.CurrentValue.S2SAppsSelfService)
            {
                return (true, request.CreateResponse(HttpStatusCode.NoContent), string.Empty);
            }
            (SubjectType subjectType, string subjectId) = await SubjectHelper.GetSubjectInfoAsync(this.tokenHelper, principal, this.dbContext);
            if (subjectType != SubjectType.User || string.IsNullOrEmpty(subjectId))
            {
                return (true, await ActionResults.ForbiddenAsync(request, "User authentication required."), string.Empty);
            }
            return (false, null, subjectId);
        }
    }
}
