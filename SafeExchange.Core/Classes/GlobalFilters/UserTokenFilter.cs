/// <summary>
/// SafeExchange
/// </summary>

namespace SpaceOyster.SafeExchange.Core
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;
    using System.Security.Claims;
    using System.Threading.Tasks;

    class UserTokenFilter : IRequestFilter
    {
        public async ValueTask<(bool shouldReturn, IActionResult actionResult)> GetFilterResultAsync(HttpRequest req, ClaimsPrincipal principal, ILogger log)
        {
            (bool shouldReturn, IActionResult actionResult) result = (shouldReturn: false, actionResult: null);

            if (!TokenHelper.IsUserToken(principal, log))
            {
                var userName = TokenHelper.GetName(principal);
                log.LogInformation($"{userName} is not authenticated with user access/id token.");

                result.shouldReturn = true;
                result.actionResult = new ObjectResult(new { status = "unauthorized", error = $"Not authenticated with user token." }) { StatusCode = StatusCodes.Status401Unauthorized };
            }

            return await Task.FromResult(result);
        }
    }
}
