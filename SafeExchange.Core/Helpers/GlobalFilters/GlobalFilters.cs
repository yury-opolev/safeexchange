/// <summary>
/// SafeExchange
/// </summary>

namespace SpaceOyster.SafeExchange.Core
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;
    using System.Collections.Generic;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class GlobalFilters : IRequestFilter
    {
        private IList<IRequestFilter> currentFilters;

        public GlobalFilters(ConfigurationSettings configuration, IGraphClientProvider graphClientProvider)
        {
            this.currentFilters = new List<IRequestFilter>();

            currentFilters.Add(new UserTokenFilter());
            currentFilters.Add(new GlobalAccessFilter(configuration, graphClientProvider));
        }

        public async ValueTask<(bool shouldReturn, IActionResult actionResult)> GetFilterResultAsync(HttpRequest req, ClaimsPrincipal principal, ILogger log)
        {
            foreach (var filter in this.currentFilters)
            {
                var filterResult = await filter.GetFilterResultAsync(req, principal, log);
                if (filterResult.shouldReturn)
                {
                    return filterResult;
                }
            }
            return (shouldReturn: false, actionResult: null);
        }
    }
}
