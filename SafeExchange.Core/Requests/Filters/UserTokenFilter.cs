/// <summary>
/// SafeExchange
/// </summary>

namespace SafeExchange.Core.Filters
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Graph;
    using SafeExchange.Core.Model;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class UserTokenFilter : IRequestFilter
    {
        public static readonly TimeSpan GroupSyncDelay = TimeSpan.FromMinutes(2);

        private readonly ITokenHelper tokenHelper;

        private readonly bool useGroups;

        private readonly IGraphDataProvider graphDataProvider;

        private readonly ILogger log;

        public UserTokenFilter(ITokenHelper tokenHelper, bool useGroups, IGraphDataProvider graphDataProvider, ILogger log)
        {
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.useGroups = useGroups;
            this.graphDataProvider = graphDataProvider ?? throw new ArgumentNullException(nameof(graphDataProvider));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public async ValueTask<(bool shouldReturn, IActionResult? actionResult)> GetFilterResultAsync(HttpRequest request, ClaimsPrincipal principal, SafeExchangeDbContext dbContext)
        {
            (bool shouldReturn, IActionResult? actionResult) result = (shouldReturn: false, actionResult: null);

            if (!this.tokenHelper.IsUserToken(principal))
            {
                var userUpn = this.tokenHelper.GetUpn(principal);
                this.log.LogInformation($"'{userUpn}' is not authenticated with user access/id token.");

                result.shouldReturn = true;
                result.actionResult = new ObjectResult(new { status = "unauthorized", error = $"Not authenticated with user token." }) { StatusCode = StatusCodes.Status401Unauthorized };
                return result;
            }

            var user = await this.GetOrCreateUserAsync(request, principal, dbContext);
            if (user is null)
            {
                this.log.LogInformation($"Could not get or create user from claims principal.");

                result.shouldReturn = true;
                result.actionResult = new ObjectResult(new { status = "unauthorized", error = $"User token is invalid." }) { StatusCode = StatusCodes.Status401Unauthorized };
                return result;
            }

            await this.UpdateGroupsAsync(user, request, principal, dbContext);
            return result;
        }

        private async Task<User?> GetOrCreateUserAsync(HttpRequest request, ClaimsPrincipal principal, SafeExchangeDbContext dbContext)
        {
            var aadObjectId = this.tokenHelper.GetObjectId(principal);
            if (string.IsNullOrEmpty(aadObjectId))
            {
                return default;
            }

            var user = await dbContext.Users.WithPartitionKey(User.DefaultPartitionKey).FirstOrDefaultAsync(u => u.AadObjectId.Equals(aadObjectId));
            return user ?? await this.CreateUserAsync(principal, dbContext);
        }

        private async Task<User?> CreateUserAsync(ClaimsPrincipal principal, SafeExchangeDbContext dbContext)
        {
            var objectId = this.tokenHelper.GetObjectId(principal);
            var tenantId = this.tokenHelper.GetTenantId(principal);
            var userUpn = this.tokenHelper.GetUpn(principal);
            var displayName = this.tokenHelper.GetDisplayName(principal);

            this.log.LogInformation($"Creating user '{userUpn}', account id: '{objectId}.{tenantId}', display name: '{displayName}'.");

            var user = new User(displayName, objectId, tenantId, userUpn, userUpn);
            var createdEntity = await dbContext.Users.AddAsync(user);
            await dbContext.SaveChangesAsync();

            return createdEntity.Entity;
        }

        private async ValueTask UpdateGroupsAsync(User user, HttpRequest request, ClaimsPrincipal principal, SafeExchangeDbContext dbContext)
        {
            if (!this.useGroups)
            {
                return;
            }

            var utcNow = DateTimeProvider.UtcNow;
            if (utcNow <= (user.LastGroupSync + GroupSyncDelay))
            {
                return;
            }

            var accountIdAndToken = this.tokenHelper.GetAccountIdAndToken(request, principal);
            var userGroups = (await this.graphDataProvider.TryGetMemberOfAsync(accountIdAndToken)) ?? Array.Empty<string>();

            user.LastGroupSync = utcNow;
            user.Groups = userGroups.Select(g => new UserGroup() { AadGroupId = g }).ToList();

            await dbContext.SaveChangesAsync();
            this.log.LogInformation($"User '{user.AadUpn}' ({user.AadObjectId}) groups synced from graph.");
        }
    }
}
