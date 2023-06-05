///
///
///

namespace SafeExchange.Core
{
    using Microsoft.Azure.Functions.Worker;
    using SafeExchange.Core.Middleware;
    using System.Security.Claims;

    public static class FunctinoContextAuthenticationExtensions
    {
        public static ClaimsPrincipal GetPrincipal(this FunctionContext context)
        {
            return context.Items[DefaultAuthenticationMiddleware.ClaimsPrincipalKey] as ClaimsPrincipal
                ?? throw new ArgumentNullException(DefaultAuthenticationMiddleware.ClaimsPrincipalKey);
        }
    }
}
