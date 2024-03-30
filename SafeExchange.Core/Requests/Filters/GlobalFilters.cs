/// <summary>
/// SafeExchange
/// </summary>

namespace SafeExchange.Core.Filters
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core.Configuration;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class GlobalFilters : IRequestFilter
    {
        private readonly IOptionsMonitor<GloballyAllowedGroupsConfiguration> groupsConfiguration;
        private readonly IOptionsMonitor<AdminConfiguration> adminConfiguration;

        private readonly ITokenHelper tokenHelper;
        private readonly ILogger log;

        public GlobalFilters(IOptionsMonitor<GloballyAllowedGroupsConfiguration> groupsConfiguration, IOptionsMonitor<AdminConfiguration> adminConfiguration, ITokenHelper tokenHelper, ILogger<GlobalFilters> log)
        {
            this.groupsConfiguration = groupsConfiguration ?? throw new ArgumentNullException(nameof(groupsConfiguration));
            this.adminConfiguration = adminConfiguration ?? throw new ArgumentNullException(nameof(adminConfiguration));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public async ValueTask<(bool shouldReturn, HttpResponseData? response)> GetFilterResultAsync(HttpRequestData req, ClaimsPrincipal principal, SafeExchangeDbContext dbContext)
        {
            var filter = new GlobalAccessFilter(this.groupsConfiguration.CurrentValue, this.tokenHelper, this.log);
            var filterResult = await filter.GetFilterResultAsync(req, principal, dbContext);
            if (filterResult.shouldReturn)
            {
                return filterResult;
            }

            return (shouldReturn: false, response: null);
        }

        public async ValueTask<(bool shouldReturn, HttpResponseData? response)> GetAdminFilterResultAsync(HttpRequestData req, ClaimsPrincipal principal, SafeExchangeDbContext dbContext)
        {
            var filter = new AdminGroupFilter(this.adminConfiguration.CurrentValue, this.tokenHelper, this.log);
            var filterResult = await filter.GetFilterResultAsync(req, principal, dbContext);
            if (filterResult.shouldReturn)
            {
                return filterResult;
            }

            return (shouldReturn: false, response: null);
        }
    }
}
