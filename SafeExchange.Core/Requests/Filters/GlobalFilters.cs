/// <summary>
/// SafeExchange
/// </summary>

namespace SafeExchange.Core.Filters
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Graph;
    using System.Collections.Generic;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class GlobalFilters : IRequestFilter
    {
        private IList<IRequestFilter> currentFilters;

        private IList<IRequestFilter> currentAdminFilters;

        public GlobalFilters(IConfiguration configuration, ITokenHelper tokenHelper, IGraphDataProvider graphDataProvider, ILogger<GlobalFilters> log)
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

            this.currentFilters = new List<IRequestFilter>();
            currentFilters.Add(new GlobalAccessFilter(groupsConfiguration, tokenHelper, log));

            this.currentAdminFilters = new List<IRequestFilter>();
            currentAdminFilters.Add(new AdminGroupFilter(adminConfiguration, tokenHelper, log));
        }

        public async ValueTask<(bool shouldReturn, HttpResponseData? response)> GetFilterResultAsync(HttpRequestData req, ClaimsPrincipal principal, SafeExchangeDbContext dbContext)
        {
            foreach (var filter in this.currentFilters)
            {
                var filterResult = await filter.GetFilterResultAsync(req, principal, dbContext);
                if (filterResult.shouldReturn)
                {
                    return filterResult;
                }
            }

            return (shouldReturn: false, response: null);
        }

        public async ValueTask<(bool shouldReturn, HttpResponseData? response)> GetAdminFilterResultAsync(HttpRequestData req, ClaimsPrincipal principal, SafeExchangeDbContext dbContext)
        {
            foreach (var filter in this.currentAdminFilters)
            {
                var filterResult = await filter.GetFilterResultAsync(req, principal, dbContext);
                if (filterResult.shouldReturn)
                {
                    return filterResult;
                }
            }

            return (shouldReturn: false, response: null);
        }
    }
}
