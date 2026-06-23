/// <summary>
/// TokenFilterMiddleware
/// </summary>

namespace SafeExchange.Core.Middleware
{
    using Microsoft.Azure.Cosmos;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Graph;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Telemetry;
    using SafeExchange.Core.Utilities;
    using System;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;
    using User = Model.User;

    public class TokenMiddlewareCore : ITokenMiddlewareCore
    {
        public static readonly TimeSpan GroupSyncDelay = TimeSpan.FromMinutes(2);

        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly bool useGroups;

        private readonly IGraphDataProvider graphDataProvider;

        private readonly TelemetryIdRotator telemetryIdRotator;

        private readonly ILogger log;

        public TokenMiddlewareCore(IConfiguration configuration, SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, IGraphDataProvider graphDataProvider, TelemetryIdRotator telemetryIdRotator, ILogger<TokenMiddlewareCore> log)
        {
            var features = new Features();
            configuration.GetSection("Features").Bind(features);

            var groupsConfiguration = new GloballyAllowedGroupsConfiguration();
            configuration.GetSection("GlobalAllowLists").Bind(groupsConfiguration);

            var adminConfiguration = new AdminConfiguration();
            configuration.GetSection("AdminConfiguration").Bind(adminConfiguration);

            var useGroups =
                features.UseGroupsAuthorization ||
                !string.IsNullOrWhiteSpace(groupsConfiguration.AllowedGroups) ||
            !string.IsNullOrWhiteSpace(adminConfiguration.AdminGroups);

            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.useGroups = useGroups;
            this.graphDataProvider = graphDataProvider ?? throw new ArgumentNullException(nameof(graphDataProvider));
            this.telemetryIdRotator = telemetryIdRotator ?? throw new ArgumentNullException(nameof(telemetryIdRotator));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public async ValueTask<(bool shouldReturn, HttpResponseData? response)> RunAsync(HttpRequestData request, ClaimsPrincipal principal)
        {
            (bool shouldReturn, HttpResponseData? response) result = (shouldReturn: false, response: null);

            var isUserToken = this.tokenHelper.IsUserToken(principal);
            if (!isUserToken && await this.IsRegisteredApplicationAsync(principal))
            {
                return result;
            }

            if (!isUserToken)
            {
                var tenantId = this.tokenHelper.GetTenantId(principal);
                var objectId = this.tokenHelper.GetObjectId(principal);
                var clientId = this.tokenHelper.GetApplicationClientId(principal);

                this.log.LogInformation("Caller is not authenticated with user token or a token from registered application.");
                result.shouldReturn = true;
                result.response = await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Forbidden,
                    new BaseResponseObject<object> { Status = "forbidden", Error = "Not authenticated with user token." });
                return result;
            }

            var user = await this.GetOrCreateUserAsync(principal);
            if (user is null)
            {
                this.log.LogInformation($"Could not get or create user from claims principal.");

                result.shouldReturn = true;
                result.response = await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.Forbidden,
                    new BaseResponseObject<object> { Status = "forbidden", Error = "User token is invalid." });
                return result;
            }

            await this.EnsureTelemetryIdAsync(user);

            // Stamp the telemetry id onto this request's frame so logs emitted within
            // RunAsync (e.g. the group-sync traces) carry the saex.telemetryId dimension,
            // and stash it on the invocation so TokenFilterMiddleware can re-establish it
            // within the frame that wraps next() — AsyncLocal mutations made here do not
            // reliably flow back up to the caller across the awaits in between.
            TelemetryContext.Current = user.TelemetryId;
            request.FunctionContext.Items[DefaultAuthenticationMiddleware.InvocationContextTelemetryIdKey] = user.TelemetryId;
            request.FunctionContext.Items[DefaultAuthenticationMiddleware.InvocationContextUserIdKey] = user.Id;
            await this.UpdateGroupsAsync(user, request, principal);
            return result;
        }

        /// <summary>
        /// Ensures the user has a current telemetry id, rotating it when expired. Rotation
        /// persists the new id onto the user document under the <c>_etag</c> concurrency
        /// token, so when several requests cross the weekly boundary together (possibly on
        /// different function instances) exactly one write wins. The losers observe a
        /// concurrency conflict, reload the user to adopt the winner's id, and skip the
        /// retired-id bookkeeping. Only the winner records the retired id into the
        /// TelemetryIdMap; a duplicate there is still swallowed as a safety net.
        /// </summary>
        private async Task EnsureTelemetryIdAsync(User user)
        {
            const int maxAttempts = 3;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var rotation = this.telemetryIdRotator.EnsureCurrent(user, DateTimeProvider.UtcNow);
                if (!rotation.Rotated)
                {
                    // Id is already current — it either never expired, or we adopted a
                    // concurrent winner's id on the reload below. Nothing to persist.
                    return;
                }

                try
                {
                    await this.dbContext.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException) when (attempt < maxAttempts)
                {
                    // Another writer rotated first. Reload to discard our rejected id and adopt
                    // the winner's persisted id (and fresh etag); the next loop iteration sees a
                    // current id, returns Rotated=false, and we end up using the winner's id.
                    this.log.LogInformation("Telemetry id rotation lost the race for user '{UserId}'; reloading to adopt the winning id.", user.Id);

                    // Small randomized backoff so a boundary burst doesn't re-collide in lockstep.
                    // Kept tiny (tens of ms) because this is on the hot auth path and the conflict
                    // usually clears on the first reload (the loser adopts the winner's id).
                    await Task.Delay(Random.Shared.Next(20, 101));
                    await this.dbContext.Entry(user).ReloadAsync();
                    continue;
                }

                if (rotation.RetiredTelemetryId is not null)
                {
                    var retiredEntry = new TelemetryIdMapEntry
                    {
                        id = rotation.RetiredTelemetryId,
                        UserId = user.Id,
                        ValidFromUtc = rotation.RetiredValidFromUtc,
                        ValidToUtc = rotation.RetiredValidToUtc,
                    };

                    // Defence in depth: the etag race above already elects a single rotation
                    // winner, so normally only this instance inserts the retired id. Should two
                    // winners ever coincide, the document is deterministic, so treat the Cosmos
                    // 409 as success instead of surfacing a 500.
                    await DbUtils.TryAddOrGetEntityAsync(
                        async () =>
                        {
                            var entity = await this.dbContext.Set<TelemetryIdMapEntry>().AddAsync(retiredEntry);
                            await this.dbContext.SaveChangesAsync();
                            return entity.Entity;
                        },
                        () =>
                        {
                            this.log.LogInformation("Retired telemetry id (tid {RetiredTelemetryId}) was already recorded by a concurrent process.", retiredEntry.id);
                            this.dbContext.Set<TelemetryIdMapEntry>().Remove(retiredEntry);
                            return Task.FromResult(retiredEntry);
                        },
                        this.log);
                }

                return;
            }
        }

        private async Task<User?> GetOrCreateUserAsync(ClaimsPrincipal principal)
        {
            var aadObjectId = this.tokenHelper.GetObjectId(principal);
            if (string.IsNullOrEmpty(aadObjectId))
            {
                return default;
            }

            var aadTenantId = this.tokenHelper.GetTenantId(principal);
            if (string.IsNullOrEmpty(aadTenantId))
            {
                return default;
            }

            var user = await this.dbContext.Users.WithPartitionKey(User.DefaultPartitionKey).FirstOrDefaultAsync(u => u.AadTenantId.Equals(aadTenantId) && u.AadObjectId.Equals(aadObjectId));
            return user ?? await this.CreateUserAsync(principal);
        }

        private async Task<User?> CreateUserAsync(ClaimsPrincipal principal)
        {
            var objectId = this.tokenHelper.GetObjectId(principal);
            var tenantId = this.tokenHelper.GetTenantId(principal);
            var userUpn = this.tokenHelper.GetUpn(principal);
            var displayName = this.tokenHelper.GetDisplayName(principal);

            var user = new User(displayName, objectId, tenantId, userUpn, userUpn);
            this.telemetryIdRotator.EnsureCurrent(user, DateTimeProvider.UtcNow);
            this.log.LogInformation("Creating user (tid {TelemetryId}).", user.TelemetryId);
            return await DbUtils.TryAddOrGetEntityAsync(
                async () =>
                {
                    var createdEntity = await this.dbContext.Users.AddAsync(user);
                    await this.dbContext.SaveChangesAsync();
                    return createdEntity.Entity;
                },
                async () =>
                {
                    this.log.LogInformation("User (tid {TelemetryId}) was created in a different process, returning existing entity.", user.TelemetryId);

                    this.dbContext.Users.Remove(user);
                    var existingUser = await this.dbContext.Users.WithPartitionKey(User.DefaultPartitionKey).FirstOrDefaultAsync(u => u.AadTenantId.Equals(tenantId) && u.AadObjectId.Equals(objectId));

                    this.log.LogInformation("User (tid {TelemetryId}) already exists.", user.TelemetryId);
                    return existingUser;
                },
                this.log);
        }

        private async ValueTask UpdateGroupsAsync(User user, HttpRequestData request, ClaimsPrincipal principal)
        {
            if (!this.useGroups)
            {
                return;
            }

            var utcNow = DateTimeProvider.UtcNow;
            if (utcNow <= user.GroupSyncNotBefore)
            {
                return;
            }

            var accountIdAndToken = this.tokenHelper.GetAccountIdAndToken(request, principal);
            var userGroupsResult = await this.graphDataProvider.TryGetTransitiveMemberOfAsync(accountIdAndToken);

            if (!userGroupsResult.Success)
            {
                user.GroupSyncNotBefore = utcNow + TimeSpan.FromSeconds(30);
                user.ConsentRequired = userGroupsResult.ConsentRequired;
                user.Groups ??= Array.Empty<UserGroup>().ToList();
            }
            else
            {
                user.ConsentRequired = userGroupsResult.ConsentRequired;
                user.GroupSyncNotBefore = utcNow + GroupSyncDelay;
                user.Groups = userGroupsResult.GroupIds.Select(g => new UserGroup() { AadGroupId = g }).ToList();
            }

            this.log.LogInformation("Updating groups for user (tid {TelemetryId}).", user.TelemetryId);
            try
            {
                await this.dbContext.SaveChangesAsync();
                this.log.LogInformation("User groups synced from graph (tid {TelemetryId}).", user.TelemetryId);
            }
            catch (DbUpdateConcurrencyException)
            {
                // A concurrent request already updated this user document (e.g. telemetry-id
                // rotation or its own group sync). Group sync is advisory and re-runs on the
                // next request after GroupSyncNotBefore, so drop this redundant write rather
                // than surface a 500.
                this.log.LogInformation("User group sync skipped due to a concurrent update (tid {TelemetryId}).", user.TelemetryId);
                this.dbContext.Entry(user).State = EntityState.Detached;
            }
        }

        private async Task<bool> IsRegisteredApplicationAsync(ClaimsPrincipal principal)
        {
            var clientId = this.tokenHelper.GetApplicationClientId(principal);
            if (string.IsNullOrEmpty(clientId))
            {
                return false;
            }

            var tenantId = this.tokenHelper.GetTenantId(principal);
            if (string.IsNullOrEmpty(tenantId))
            {
                return false;
            }

            var registeredApplication = await this.dbContext.Applications.FirstOrDefaultAsync(a => a.Enabled && a.AadClientId.Equals(clientId) && a.AadTenantId.Equals(tenantId));
            return registeredApplication != default;
        }
    }
}
