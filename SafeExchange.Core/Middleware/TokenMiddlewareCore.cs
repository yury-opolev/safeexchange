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

            var telemetryIdChanged = this.telemetryIdRotator.EnsureCurrent(user, DateTimeProvider.UtcNow);
            TelemetryContext.Current = user.TelemetryId;
            if (telemetryIdChanged)
            {
                await this.dbContext.SaveChangesAsync();
            }

            request.FunctionContext.Items[DefaultAuthenticationMiddleware.InvocationContextUserIdKey] = user.Id;
            await this.UpdateGroupsAsync(user, request, principal);
            return result;
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
            await this.dbContext.SaveChangesAsync();
            this.log.LogInformation("User groups synced from graph (tid {TelemetryId}).", user.TelemetryId);
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
