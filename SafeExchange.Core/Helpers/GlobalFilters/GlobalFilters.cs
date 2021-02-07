/// <summary>
/// SafeExchange
/// </summary>

namespace SafeExchange.Core.Helpers.GlobalFilters
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;
    using SpaceOyster.SafeExchange.Core;
    using System;
    using System.Collections.Generic;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class GlobalFilters : IRequestFilter
    {
        public static Lazy<GlobalFilters> Instance = new Lazy<GlobalFilters>();

        private IList<IRequestFilter> currentFilters;

        public GlobalFilters()
        {
            this.currentFilters = new List<IRequestFilter>();
            currentFilters.Add(new GlobalAccessFilter(new GraphClientProvider()));
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
