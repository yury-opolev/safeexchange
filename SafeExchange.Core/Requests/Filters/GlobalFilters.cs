/// <summary>
/// SafeExchange
/// </summary>

namespace SafeExchange.Core.Filters
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
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

            this.currentFilters = new List<IRequestFilter>();
            currentFilters.Add(new UserTokenFilter(tokenHelper, features.UseGroupsAuthorization, graphDataProvider, log));
            currentFilters.Add(new GlobalAccessFilter(configuration, tokenHelper, graphDataProvider, log));

            this.currentAdminFilters = new List<IRequestFilter>();
            currentAdminFilters.Add(new UserTokenFilter(tokenHelper, features.UseGroupsAuthorization, graphDataProvider, log));
            currentAdminFilters.Add(new AdminGroupFilter(configuration, tokenHelper, log));
        }

        public async ValueTask<(bool shouldReturn, IActionResult? actionResult)> GetFilterResultAsync(HttpRequest req, ClaimsPrincipal principal, SafeExchangeDbContext dbContext)
        {
            foreach (var filter in this.currentFilters)
            {
                var filterResult = await filter.GetFilterResultAsync(req, principal, dbContext);
                if (filterResult.shouldReturn)
                {
                    return filterResult;
                }
            }

            return (shouldReturn: false, actionResult: null);
        }

        public async ValueTask<(bool shouldReturn, IActionResult? actionResult)> GetAdminFilterResultAsync(HttpRequest req, ClaimsPrincipal principal, SafeExchangeDbContext dbContext)
        {
            foreach (var filter in this.currentAdminFilters)
            {
                var filterResult = await filter.GetFilterResultAsync(req, principal, dbContext);
                if (filterResult.shouldReturn)
                {
                    return filterResult;
                }
            }

            return (shouldReturn: false, actionResult: null);
        }
    }
}
