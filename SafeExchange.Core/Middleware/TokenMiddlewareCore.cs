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

        private readonly ILogger log;

        public TokenMiddlewareCore(IConfiguration configuration, SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, IGraphDataProvider graphDataProvider, ILogger<TokenMiddlewareCore> log)
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

                this.log.LogInformation($"Caller [{clientId}] '{tenantId}.{objectId}' is not authenticated with user token or a token from registered application.");
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

            this.log.LogInformation($"Creating user '{userUpn}', account id: '{objectId}.{tenantId}', display name: '{displayName}'.");

            var user = new User(displayName, objectId, tenantId, userUpn, userUpn);
            try
            {
                var createdEntity = await this.dbContext.Users.AddAsync(user);
                await this.dbContext.SaveChangesAsync();
                return createdEntity.Entity;
            }
            catch (DbUpdateException dbUpdateException)
                when (dbUpdateException.InnerException is CosmosException cosmosException &&
                      cosmosException.StatusCode == HttpStatusCode.Conflict)
            {
                this.log.LogInformation($"User '{userUpn}', account id: '{objectId}.{tenantId}', display name: '{displayName}' was created in a different process, returning existing entity.");

                user = await this.dbContext.Users.WithPartitionKey(User.DefaultPartitionKey).FirstOrDefaultAsync(u => u.AadTenantId.Equals(tenantId) && u.AadObjectId.Equals(objectId));

                this.log.LogInformation($"User '{userUpn}' was created previously with Id '{user.Id}'.");
                return user;
            }
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
            var userGroupsResult = await this.graphDataProvider.TryGetMemberOfAsync(accountIdAndToken);

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

            await this.dbContext.SaveChangesAsync();
            this.log.LogInformation($"User '{user.AadUpn}' ({user.AadTenantId}.{user.AadObjectId}) groups synced from graph.");
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
