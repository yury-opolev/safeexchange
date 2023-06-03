/// <summary>
/// IRequestFilter
/// </summary>

namespace SafeExchange.Core.Filters
{
    using Microsoft.Azure.Functions.Worker.Http;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public interface IRequestFilter
    {
        /// <summary>
        /// Return tuple (bool, IActionResult), where first value indicates that function should
        /// respond immediately with provided IActionResult and not proceed further.
        /// </summary>
        /// <param name="req">Incoming http request.</param>
        /// <param name="principal"><see cref="ClaimsPrincipal">ClaimsPrincipal</see> for authenticated user.</param>
        /// <param name="dbContext">Database context to retrieve/persist user data.</param>
        /// <returns></returns>
        public ValueTask<(bool shouldReturn, HttpResponseData? response)> GetFilterResultAsync(HttpRequestData req, ClaimsPrincipal principal, SafeExchangeDbContext dbContext);
    }
}
