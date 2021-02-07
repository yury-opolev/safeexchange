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
    using System.Threading;
    using System.Threading.Tasks;

    public class GlobalFilters : IRequestFilter
    {
        public static Lazy<GlobalFilters> Instance = new Lazy<GlobalFilters>(() => new GlobalFilters(new GraphClientProvider()), LazyThreadSafetyMode.PublicationOnly);

        private IList<IRequestFilter> currentFilters;

        public GlobalFilters(IGraphClientProvider graphClientProvider)
        {
            this.currentFilters = new List<IRequestFilter>();

            currentFilters.Add(new UserTokenFilter());
            currentFilters.Add(new GlobalAccessFilter(graphClientProvider));
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
