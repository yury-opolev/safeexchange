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

        public SafeExchangeS2SApps(
            SafeExchangeDbContext dbContext,
            ITokenHelper tokenHelper,
            GlobalFilters globalFilters,
            IApplicationOwnerService ownerService,
            IOptionsMonitor<Features> features)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.ownerService = ownerService ?? throw new ArgumentNullException(nameof(ownerService));
            this.features = features ?? throw new ArgumentNullException(nameof(features));
        }

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

                // Uniqueness: display name OR (tenantId + clientId) pair.
                var nameTaken = await this.dbContext.Applications.AnyAsync(a => a.DisplayName == input.DisplayName);
                if (nameTaken)
                {
                    return await ConflictAsync(request, $"Application '{input.DisplayName}' is already registered.");
                }
                var clientPairTaken = await this.dbContext.Applications.AnyAsync(
                    a => a.AadTenantId == tenantId && a.AadClientId == input.AadClientId);
                if (clientPairTaken)
                {
                    return await ConflictAsync(request, "An application with that tenant/client id pair already exists.");
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
                var app = new Application(input.DisplayName, registration, createdBy: $"User {subjectId}");
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

                var overviews = owned.Select(a => new S2SAppOverviewOutput
                {
                    DisplayName = a.DisplayName,
                    AadClientId = a.AadClientId,
                    Enabled = a.Enabled,
                    OwnerCount = -1, // populated below if needed; left at -1 to avoid an N+1 in the list view
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
    }
}
