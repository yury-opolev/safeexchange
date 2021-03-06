﻿/// <summary>
/// SafeExchange
/// </summary>

namespace SpaceOyster.SafeExchange.Core
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public interface IRequestFilter
    {
        public ValueTask<(bool shouldReturn, IActionResult actionResult)> GetFilterResultAsync(HttpRequest req, ClaimsPrincipal principal, ILogger log);
    }
}
