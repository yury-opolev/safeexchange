/// <summary>
/// SafeExchange
/// </summary>

namespace SafeExchange.Core.Filters
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Configuration;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public class GlobalAccessFilter : IRequestFilter
    {
        private readonly GlobalAccessPolicy policy;

        public GlobalAccessFilter(GloballyAllowedGroupsConfiguration groupsConfiguration, ITokenHelper tokenHelper, ILogger log)
        {
            this.policy = new GlobalAccessPolicy(groupsConfiguration, tokenHelper, log);
        }

        public async ValueTask<(bool shouldReturn, HttpResponseData? response)> GetFilterResultAsync(HttpRequestData req, ClaimsPrincipal principal, SafeExchangeDbContext dbContext)
        {
            var denial = await this.policy.EvaluateAsync(principal, dbContext);
            if (denial is null)
            {
                return (shouldReturn: false, response: null);
            }

            return (shouldReturn: true, response: await denial.ToResponseAsync(req));
        }
    }
}
